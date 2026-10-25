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

    /// <summary>Where a run writes its output before the swap. A leftover from a crashed run is deleted by the next run.</summary>
    public const string StagingFolderName = ".itemlookmerger-staging";

    /// <summary>Where the swap moves the previous output before deleting it. A leftover is put back by the next run.</summary>
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
    /// marker when missing; a non-empty folder without the marker belongs to someone else, and the run fails instead. Leftovers of a run
    /// that stopped halfway are cleaned up first. The previous output itself is not touched.
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
            if (!hasMarker && entries.Count > 0)
                throw new RunFailedException(
                    $"The forward folder {folder} already exists and was not made by ItemLookMerger (no {MarkerFileName} file in it); it looks like another mod. "
                    + "Pick another forward folder name, or empty that folder yourself.");
            if (!hasMarker) WriteMarker(fileSystem, marker);
        }

        RestorePrevious(fileSystem, folder, log);
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
    /// marker) are moved aside, the staged ones moved in, then the previous ones deleted. When a move fails, the previous output is
    /// put back and the run fails. Links and junctions are moved or removed themselves, never followed.
    /// </summary>
    public static void CommitStaging(IFileSystem fileSystem, string folder, string staging, ILog log)
    {
        var path = fileSystem.Path;
        var previous = path.Combine(folder, PreviousFolderName);
        var oldEntries = OutputEntries(fileSystem, folder);
        var stagedEntries = fileSystem.Directory.EnumerateFileSystemEntries(staging).ToList();
        var movedIn = new List<string>();
        fileSystem.Directory.CreateDirectory(previous);
        try
        {
            foreach (var entry in oldEntries) Move(fileSystem, entry, path.Combine(previous, path.GetFileName(entry)));
            foreach (var entry in stagedEntries)
            {
                var target = path.Combine(folder, path.GetFileName(entry));
                Move(fileSystem, entry, target);
                movedIn.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put the new entries back into staging (deleted with it) and the previous output where it was.
            foreach (var entry in movedIn) TryMove(fileSystem, entry, path.Combine(staging, path.GetFileName(entry)));
            RestorePrevious(fileSystem, folder, log);
            throw new RunFailedException(
                $"The new output could not be moved into the forward folder {folder} ({ex.Message}); the previous output is kept. "
                + "Close any program that has files of that folder open, then run the patcher again.");
        }

        DeleteEntries(fileSystem, previous);
        fileSystem.Directory.Delete(previous, recursive: true);
        fileSystem.Directory.Delete(staging, recursive: true);
        log.Verbose($"Replaced the forward folder's output ({oldEntries.Count} previous entries removed, {stagedEntries.Count} written).");
    }

    /// <summary>Deletes the staging folder of a failed run; the previous output stays as it was.</summary>
    public static void DiscardStaging(IFileSystem fileSystem, string staging, ILog log)
    {
        try
        {
            if (!fileSystem.Directory.Exists(staging)) return;
            DeleteEntries(fileSystem, staging);
            fileSystem.Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next run removes it.
            log.Warning($"The staging folder {staging} could not be removed ({ex.Message}); the next run removes it.");
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

    /// <summary>The folder's output entries: everything but the marker and the staging and previous folders.</summary>
    private static List<string> OutputEntries(IFileSystem fileSystem, string folder) =>
        fileSystem.Directory.EnumerateFileSystemEntries(folder)
            .Where(entry => !IsNamed(fileSystem, entry, MarkerFileName)
                && !IsNamed(fileSystem, entry, StagingFolderName)
                && !IsNamed(fileSystem, entry, PreviousFolderName))
            .ToList();

    /// <summary>
    /// Puts back what a swap moved aside: the previous folder's entries go back where no entry of that name is now, then the previous
    /// folder is deleted. After a run that stopped halfway through a swap, this restores the older output as far as it can.
    /// </summary>
    private static void RestorePrevious(IFileSystem fileSystem, string folder, ILog log)
    {
        var path = fileSystem.Path;
        var previous = path.Combine(folder, PreviousFolderName);
        if (!fileSystem.Directory.Exists(previous)) return;
        var restored = 0;
        foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(previous).ToList())
        {
            var target = path.Combine(folder, path.GetFileName(entry));
            if (fileSystem.File.Exists(target) || fileSystem.Directory.Exists(target)) continue;
            if (TryMove(fileSystem, entry, target)) restored++;
        }
        DeleteEntries(fileSystem, previous);
        fileSystem.Directory.Delete(previous, recursive: true);
        if (restored > 0) log.Warning($"An earlier run stopped while replacing the output in {folder}; {restored} entries of the older output were put back.");
    }

    /// <summary>Deletes the entries of <paramref name="folder"/>. A link or junction (intact or broken) is removed itself, never followed.</summary>
    private static void DeleteEntries(IFileSystem fileSystem, string folder)
    {
        // Directory.Delete does not follow links inside a real subfolder either.
        foreach (var entry in fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList())
        {
            var attributes = fileSystem.File.GetAttributes(entry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) && isDirectory) fileSystem.Directory.Delete(entry, recursive: false);
            else if (isDirectory) fileSystem.Directory.Delete(entry, recursive: true);
            else fileSystem.File.Delete(entry);
        }
    }

    /// <summary>Moves a file, folder or link (the link itself) to <paramref name="target"/>.</summary>
    private static void Move(IFileSystem fileSystem, string entry, string target)
    {
        if (fileSystem.File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) fileSystem.Directory.Move(entry, target);
        else fileSystem.File.Move(entry, target);
    }

    private static bool TryMove(IFileSystem fileSystem, string entry, string target)
    {
        try
        {
            Move(fileSystem, entry, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsNamed(IFileSystem fileSystem, string entry, string name) =>
        string.Equals(fileSystem.Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase);

    private static string WithSeparator(string path) => path.TrimEnd('\\', '/') + '\\';

    private static void WriteMarker(IFileSystem fileSystem, string marker) =>
        fileSystem.File.WriteAllText(marker, "This folder is written by the ItemLookMerger Synthesis patcher; each successful run replaces its content.\n");
}
