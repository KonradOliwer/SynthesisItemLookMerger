using System.IO.Abstractions;
using ItemLookMerger.Core.Mo2;

namespace ItemLookMerger.Core.Assets;

/// <summary>
/// The MO2 mod folder that receives the copied assets and CREDITS.md. Each merging run writes its output into a staging folder inside
/// it and swaps that in only once everything is written, so a failed run leaves the previous output as it was. It must be a plain
/// folder name directly inside the mods folder, and its content is only replaced when this patcher's marker file shows it owns it.
/// </summary>
internal static class ForwardFolder
{
    public const string MarkerFileName = ".itemlookmerger";

    /// <summary>MO2's own metadata file (category, notes, version) at the root of a mod folder. Never moved, replaced or deleted here.</summary>
    public const string MetaFileName = "meta.ini";

    /// <summary>
    /// Where a run writes its output before the swap. A leftover from a crashed run is deleted by the next run that is not blocked by a
    /// leftover previous folder. Every path written there is longer than its final one by this folder's name plus a separator, so a path
    /// that fits finally can fail while it is being written (a long-path IO error; the run is rolled back).
    /// </summary>
    public const string StagingFolderName = ".itemlookmerger-staging";

    /// <summary>
    /// Where the swap moves the previous output before deleting it. A leftover means a run was interrupted or its cleanup failed; the next
    /// run does not guess and refuses to start until the user has dealt with it.
    /// </summary>
    public const string PreviousFolderName = ".itemlookmerger-previous";

    /// <summary>
    /// The full path of the forward folder, or a run failure when <paramref name="name"/> is empty, rooted, or could reach
    /// outside the mods folder ("." or "..", or a path separator).
    /// </summary>
    public static string Resolve(IFileSystem fileSystem, Mo2Instance mo2, string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new RunFailedException("Content merging is on, but the forward folder name is empty. Name the MO2 mod folder that receives the assets and CREDITS.md.");
        if (!IsPlainFolderName(fileSystem, trimmed))
            throw new RunFailedException(
                $"The forward folder name \"{trimmed}\" must be a plain folder name inside the MO2 mods folder: not \".\" or \"..\", without \\ or /.");

        var folder = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(mo2.ModsDirectory, trimmed));
        // Compared with a trailing separator, so a mods folder at a drive root ("E:\") works too.
        var parent = WithSeparator(fileSystem.Path.GetDirectoryName(folder) ?? string.Empty);
        if (!string.Equals(parent, WithSeparator(fileSystem.Path.GetFullPath(mo2.ModsDirectory)), StringComparison.OrdinalIgnoreCase))
            throw new RunFailedException($"The forward folder {folder} is not directly inside the MO2 mods folder {mo2.ModsDirectory}.");
        return folder;
    }

    /// <summary>
    /// Whether <paramref name="name"/> names a folder directly inside another: not empty, not rooted, not "." or ".." (nor only dots,
    /// which Windows reads as the folder itself), without path separators or characters a file name cannot have. Dots inside a name
    /// ("Armors... Remastered") are fine.
    /// </summary>
    public static bool IsPlainFolderName(IFileSystem fileSystem, string name) =>
        name.Length > 0
        && name.Any(c => c != '.')
        && !fileSystem.Path.IsPathRooted(name)
        && name.IndexOfAny(['/', '\\']) < 0
        && name.IndexOfAny(fileSystem.Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Claims <paramref name="folder"/> for this patcher and returns an empty staging folder inside it. The folder is created with the
    /// marker when missing; a non-empty folder without the marker belongs to someone else, and the run fails instead. A leftover previous
    /// folder also fails the run, before anything is changed. A leftover staging folder is never published and is deleted. The previous
    /// output itself is not touched.
    /// </summary>
    public static string BeginStaging(IFileSystem fileSystem, string folder, ILog log)
    {
        var path = fileSystem.Path;
        var marker = path.Combine(folder, MarkerFileName);
        if (!fileSystem.Directory.Exists(folder))
        {
            fileSystem.Directory.CreateDirectory(folder);
            WriteMarker(fileSystem, marker);
            log.Info($"Created the forward folder {folder}; enable it in MO2.");
        }
        else
        {
            var entries = fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList();
            var hasMarker = entries.Any(entry => IsNamed(fileSystem, entry, MarkerFileName));
            // MO2's "create empty mod" leaves a meta.ini, which does not make the folder someone else's.
            if (!hasMarker && entries.Any(entry => !IsNamed(fileSystem, entry, MetaFileName)))
                throw new RunFailedException(
                    $"The forward folder {folder} already exists and was not made by ItemLookMerger (no {MarkerFileName} file in it); it looks like another mod. "
                    + "Pick another forward folder name, or empty that folder yourself.");
            // Which of the two it is cannot be told, so nothing is deleted or restored here.
            // A file with that name counts too, exactly as for HasLeftoverPrevious.
            if (entries.Any(entry => IsNamed(fileSystem, entry, PreviousFolderName)))
                throw new RunFailedException(LeftoverPreviousMessage(folder, path.Combine(folder, PreviousFolderName), "exists"));
            var stagingPath = path.Combine(folder, StagingFolderName);
            if (entries.Any(entry => IsNamed(fileSystem, entry, StagingFolderName)) && !fileSystem.Directory.Exists(stagingPath))
                throw new RunFailedException($"{stagingPath} is a file, not the staging folder. Nothing was changed. Delete it, then run the patcher again.");
            if (!hasMarker) WriteMarker(fileSystem, marker);
        }

        var staging = path.Combine(folder, StagingFolderName);
        if (fileSystem.Directory.Exists(staging))
        {
            DeleteEntries(fileSystem, staging);
            fileSystem.Directory.Delete(staging, recursive: true);
            log.Verbose($"Removed the staging folder an earlier run left in {folder}.");
        }
        fileSystem.Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>
    /// Replaces the output in <paramref name="folder"/> with what <paramref name="staging"/> holds: the previous entries (all but the
    /// marker) are moved aside, the staged ones moved in, then the previous ones deleted. When anything fails before the swap is done,
    /// the new entries are removed again and the previous ones put back; if that fails too, the previous folder is kept, an error names it
    /// and the run fails. The commit point is the end of the swap: the cleanup after it only warns. Links and junctions are moved or
    /// removed themselves, never followed.
    /// </summary>
    public static void CommitStaging(IFileSystem fileSystem, string folder, string staging, ILog log)
    {
        var path = fileSystem.Path;
        var previous = path.Combine(folder, PreviousFolderName);
        var movedIn = new List<string>();
        int oldCount = 0, stagedCount = 0;
        // Checked again just before the first change: if something created it meanwhile, nothing may be moved or deleted.
        if (Exists(fileSystem, previous))
            throw new RunFailedException(LeftoverPreviousMessage(folder, previous, "appeared while the run was writing its output")) { PreviousNotFromThisRun = true };
        try
        {
            // Sorted, so the entries are always moved in the same order.
            var oldEntries = OutputEntries(fileSystem, folder).OrderBy(entry => path.GetFileName(entry), StringComparer.OrdinalIgnoreCase).ToList();
            var stagedEntries = fileSystem.Directory.EnumerateFileSystemEntries(staging).OrderBy(entry => path.GetFileName(entry), StringComparer.OrdinalIgnoreCase).ToList();
            (oldCount, stagedCount) = (oldEntries.Count, stagedEntries.Count);
            fileSystem.Directory.CreateDirectory(previous);
            foreach (var entry in oldEntries) Move(fileSystem, entry, path.Combine(previous, path.GetFileName(entry)));
            foreach (var entry in stagedEntries)
            {
                var target = path.Combine(folder, path.GetFileName(entry));
                Move(fileSystem, entry, target);
                movedIn.Add(target);
            }
        }
        catch (Exception ex)
        {
            var restored = SwapBack(fileSystem, folder, previous, movedIn, log);
            var reason = $"The new output could not be moved into the forward folder {folder} ({ex.Message})";
            throw new RunFailedException(
                restored
                    ? $"{reason}; the output in the forward folder is back as it was. Close any program that has files of that folder open, then run the patcher again."
                    : $"{reason}, and the previous output could not be put back; what is left of it is kept in {previous}. Delete {previous} and run the patcher again.",
                ex);
        }

        // The commit point: from here on the run is a success, whatever the cleanup does. Never rolled back, never failed.
        var leftovers = new List<string>();
        try
        {
            DeleteEntries(fileSystem, previous);
            fileSystem.Directory.Delete(previous, recursive: true);
        }
        catch (Exception ex)
        {
            leftovers.Add(previous);
            log.Warning(
                $"The new output is in place, but the old output in {previous} could not be removed ({ex.Message}). "
                + $"Delete {previous} yourself: the next run does not start while it exists.");
        }
        try
        {
            if (fileSystem.Directory.Exists(staging)) fileSystem.Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex)
        {
            leftovers.Add(staging);
            log.Warning($"The new output is in place, but the staging folder {staging} could not be removed ({ex.Message}); a later run removes it once no previous folder is left.");
        }
        log.Verbose(
            $"Replaced the forward folder's output ({oldCount} previous entries replaced by {stagedCount} written"
            + (leftovers.Count == 0 ? ")." : $"; not cleaned up: {string.Join(", ", leftovers)})."));
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is missing or holds nothing but the marker and MO2's meta.ini, so a run that fails there must
    /// leave it without output.
    /// </summary>
    public static bool IsMissingOrEmpty(IFileSystem fileSystem, string folder) =>
        !fileSystem.Directory.Exists(folder)
        || !fileSystem.Directory.EnumerateFileSystemEntries(folder)
            .Any(entry => !IsNamed(fileSystem, entry, MarkerFileName) && !IsNamed(fileSystem, entry, MetaFileName));

    /// <summary>
    /// Whether <paramref name="folder"/> holds output of an earlier run: anything but the marker, meta.ini and a leftover staging folder.
    /// </summary>
    public static bool HasOutput(IFileSystem fileSystem, string folder) =>
        fileSystem.Directory.Exists(folder)
        && fileSystem.Directory.EnumerateFileSystemEntries(folder)
            .Any(entry => !IsNamed(fileSystem, entry, MarkerFileName) && !IsNamed(fileSystem, entry, MetaFileName) && !IsNamed(fileSystem, entry, StagingFolderName));

    /// <summary>
    /// Whether <paramref name="folder"/> exists, holds something besides meta.ini and has no marker: a folder of someone else that
    /// <see cref="BeginStaging"/> refuses and a run must not touch.
    /// </summary>
    public static bool IsForeign(IFileSystem fileSystem, string folder) =>
        fileSystem.Directory.Exists(folder)
        && fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList() is var entries
        && entries.Any(entry => !IsNamed(fileSystem, entry, MetaFileName))
        && !entries.Any(entry => IsNamed(fileSystem, entry, MarkerFileName));

    /// <summary>
    /// The failure for a leftover previous folder. It may hold only part of the earlier output (the swap was interrupted, a restore or a
    /// cleanup stopped half-way), so the only safe way on is deleting it and running again, which rebuilds the whole folder.
    /// </summary>
    private static string LeftoverPreviousMessage(string folder, string leftover, string what) =>
        $"{leftover} {what}: an earlier run was interrupted while replacing the output, or could not delete it afterwards. Nothing was changed. "
        + $"Delete {leftover} and run the patcher again, which rebuilds the whole forward folder {folder} from the look mods. "
        + $"{leftover} may hold only an incomplete copy of earlier output, so do not move it back as it is; look through it only to salvage a file.";

    /// <summary>
    /// Undoes a failed run: deletes the staging folder, and a folder that the run created or found empty goes back to missing or empty
    /// (it then holds only the marker the run wrote). The previous output of any other folder stays as it was. The marker is deleted
    /// last, and only when nothing else of the patcher is left in the folder. Never throws, so the failure that caused the rollback is
    /// not hidden; it logs what it could not undo and returns what is left behind (empty when the folder is as it was). Only call it
    /// for a folder the run has claimed, never for a foreign one.
    /// </summary>
    public static IReadOnlyList<string> Rollback(
        IFileSystem fileSystem, string folder, string staging, bool wasMissing, bool wasEmpty, ILog log, bool previousNotFromThisRun = false)
    {
        var left = new List<string>();
        try
        {
            if (!DiscardStaging(fileSystem, staging, log)) left.Add($"the staging folder {staging}");
            var previous = fileSystem.Path.Combine(folder, PreviousFolderName);
            var previousExists = Exists(fileSystem, previous);
            // A previous folder this run did not make was not touched: it is neither left behind by the run nor reset away.
            if (previousExists && !previousNotFromThisRun) left.Add($"the previous output in {previous}");
            // Only a folder with nothing else of ours in it is reset, so the marker never goes while something is left.
            if (left.Count == 0 && wasEmpty && !previousExists && fileSystem.Directory.Exists(folder))
            {
                try
                {
                    ResetFolder(fileSystem, folder, wasMissing);
                }
                catch (Exception ex)
                {
                    log.Warning($"The forward folder {folder} could not be reset to its earlier empty state ({ex.Message}).");
                    left.Add($"content in the forward folder {folder}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"Undoing the failed run in {folder} failed too ({ex.Message}); check that folder by hand.");
            left.Add($"unknown (the forward folder {folder} could not be checked)");
            return left;
        }
        if (left.Count == 0) log.Info("Run failed; forward folder left unchanged.");
        else log.Warning($"Run failed; the forward folder {folder} is not as it was. Left behind: {string.Join("; ", left)}.");
        return left;
    }

    /// <summary>Empties <paramref name="folder"/> but for meta.ini, the marker last, and removes it too when the run created it.</summary>
    private static void ResetFolder(IFileSystem fileSystem, string folder, bool remove)
    {
        foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList())
            if (!IsNamed(fileSystem, entry, MarkerFileName) && !IsNamed(fileSystem, entry, MetaFileName)) DeleteEntry(fileSystem, entry);
        var marker = fileSystem.Path.Combine(folder, MarkerFileName);
        if (fileSystem.File.Exists(marker)) fileSystem.File.Delete(marker);
        if (remove) fileSystem.Directory.Delete(folder, recursive: false);
    }

    /// <summary>
    /// Undoes a swap that failed: the new entries that got in are deleted, every previous entry is moved back and verified, and only then
    /// is the previous folder deleted. Returns false, with an error that names the previous folder and says how to recover, when anything
    /// of that failed; the previous folder is then kept. Never throws.
    /// </summary>
    private static bool SwapBack(IFileSystem fileSystem, string folder, string previous, List<string> movedIn, ILog log)
    {
        var path = fileSystem.Path;
        var problems = new List<string>();
        var restored = new List<string>();
        try
        {
            foreach (var target in Enumerable.Reverse(movedIn))
            {
                try
                {
                    if (Exists(fileSystem, target)) DeleteEntry(fileSystem, target);
                }
                catch (Exception ex)
                {
                    problems.Add($"{target}: {ex.Message}");
                }
            }
            if (fileSystem.Directory.Exists(previous))
            {
                foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(previous).ToList())
                {
                    var target = path.Combine(folder, path.GetFileName(entry));
                    try
                    {
                        Move(fileSystem, entry, target);
                        restored.Add(target);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{entry}: {ex.Message}");
                    }
                }
                problems.AddRange(restored.Where(target => !Exists(fileSystem, target)).Select(target => $"{target} is missing after it was put back"));
                if (problems.Count == 0 && fileSystem.Directory.EnumerateFileSystemEntries(previous).Any())
                    problems.Add("some entries were not put back");
                if (problems.Count == 0) fileSystem.Directory.Delete(previous, recursive: false);
            }
        }
        catch (Exception ex)
        {
            problems.Add(ex.Message);
        }
        if (problems.Count == 0) return true;
        var done = restored.Count == 0 ? "none" : string.Join(", ", restored.Select(path.GetFileName));
        log.Error(
            $"The previous output of the forward folder {folder} could not be put back ({string.Join("; ", problems)}). Put back already: {done}. "
            + $"The rest is kept in {previous}, which may be incomplete. To recover: close any program that has files of {folder} open, delete {previous}, "
            + "and run the patcher again, which rebuilds the whole forward folder from the look mods. Look through it only to salvage a file.");
        return false;
    }

    private static bool Exists(IFileSystem fileSystem, string entry) => fileSystem.File.Exists(entry) || fileSystem.Directory.Exists(entry);

    /// <summary>Whether an earlier run left <see cref="PreviousFolderName"/> (a folder, or a file of that name) in <paramref name="folder"/>.</summary>
    public static bool HasLeftoverPrevious(IFileSystem fileSystem, string folder) =>
        Exists(fileSystem, fileSystem.Path.Combine(folder, PreviousFolderName));

    /// <summary>Deletes the staging folder of a failed run; the previous output stays as it was. False when it could not be removed.</summary>
    public static bool DiscardStaging(IFileSystem fileSystem, string staging, ILog log)
    {
        try
        {
            if (!fileSystem.Directory.Exists(staging)) return true;
            DeleteEntries(fileSystem, staging);
            fileSystem.Directory.Delete(staging, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            // A later run removes it, once no previous folder blocks the start.
            log.Warning($"The staging folder {staging} could not be removed ({ex.Message}); a later run removes it once no previous folder is left.");
            return false;
        }
    }

    /// <summary>
    /// When merging is off, an earlier run's forward folder is left alone; this says so, since its assets and CREDITS.md are now stale.
    /// </summary>
    public static void ReportLeftover(IFileSystem fileSystem, Mo2Instance mo2, string? name, ILog log)
    {
        string folder;
        try
        {
            folder = Resolve(fileSystem, mo2, name);
        }
        catch (RunFailedException)
        {
            return;
        }
        if (fileSystem.File.Exists(fileSystem.Path.Combine(folder, MarkerFileName)))
            log.Warning($"Content merging is off, so the forward folder {folder} from an earlier run is left as it is. Disable it in MO2 if its assets are no longer wanted.");
    }

    /// <summary>The folder's output entries: everything but the marker, MO2's meta.ini and the staging and previous folders.</summary>
    private static List<string> OutputEntries(IFileSystem fileSystem, string folder) =>
        fileSystem.Directory.EnumerateFileSystemEntries(folder)
            .Where(entry => !IsNamed(fileSystem, entry, MarkerFileName)
                && !IsNamed(fileSystem, entry, MetaFileName)
                && !IsNamed(fileSystem, entry, StagingFolderName)
                && !IsNamed(fileSystem, entry, PreviousFolderName))
            .ToList();

    /// <summary>Deletes the entries of <paramref name="folder"/>. A link or junction (intact or broken) is removed itself, never followed.</summary>
    private static void DeleteEntries(IFileSystem fileSystem, string folder)
    {
        // Directory.Delete does not follow links inside a real subfolder either.
        foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList()) DeleteEntry(fileSystem, entry);
    }

    private static void DeleteEntry(IFileSystem fileSystem, string entry)
    {
        var attributes = fileSystem.File.GetAttributes(entry);
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) && isDirectory) fileSystem.Directory.Delete(entry, recursive: false);
        else if (isDirectory) fileSystem.Directory.Delete(entry, recursive: true);
        else fileSystem.File.Delete(entry);
    }

    /// <summary>Moves a file, folder or link (the link itself) to <paramref name="target"/>.</summary>
    private static void Move(IFileSystem fileSystem, string entry, string target)
    {
        if (fileSystem.File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) fileSystem.Directory.Move(entry, target);
        else fileSystem.File.Move(entry, target);
    }

    private static bool IsNamed(IFileSystem fileSystem, string entry, string name) =>
        string.Equals(fileSystem.Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase);

    private static string WithSeparator(string path) => path.TrimEnd('\\', '/') + '\\';

    private static void WriteMarker(IFileSystem fileSystem, string marker) =>
        fileSystem.File.WriteAllText(marker, "This folder is written by the ItemLookMerger Synthesis patcher; each successful run replaces its content.\n");
}
