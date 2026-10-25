using System.IO.Abstractions;
using ItemLookMerger.Core.Mo2;

namespace ItemLookMerger.Core.Assets;

/// <summary>
/// The MO2 mod folder that receives the copied assets and CREDITS.md. It is emptied at the start of every merging run, so it must be
/// a plain folder name directly inside the mods folder, and it is only emptied when this patcher's marker file shows it owns it.
/// </summary>
internal static class ForwardFolder
{
    public const string MarkerFileName = ".itemlookmerger";

    /// <summary>
    /// The full path of the forward folder, or a run failure when <paramref name="name"/> is empty, rooted, or could reach
    /// outside the mods folder (".." or a path separator).
    /// </summary>
    public static string Resolve(IFileSystem fileSystem, Mo2Instance mo2, string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new RunFailedException("Content merging is on, but the forward folder name is empty. Name the MO2 mod folder that receives the assets and CREDITS.md.");
        if (!IsPlainFolderName(fileSystem, trimmed))
            throw new RunFailedException(
                $"The forward folder name \"{trimmed}\" must be a plain folder name inside the MO2 mods folder, without \"..\", \\ or /.");

        var folder = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(mo2.ModsDirectory, trimmed));
        // Compared with a trailing separator, so a mods folder at a drive root ("E:\") works too.
        var parent = WithSeparator(fileSystem.Path.GetDirectoryName(folder) ?? string.Empty);
        if (!string.Equals(parent, WithSeparator(fileSystem.Path.GetFullPath(mo2.ModsDirectory)), StringComparison.OrdinalIgnoreCase))
            throw new RunFailedException($"The forward folder {folder} is not directly inside the MO2 mods folder {mo2.ModsDirectory}.");
        return folder;
    }

    /// <summary>
    /// Whether <paramref name="name"/> names a folder directly inside another: not empty, not rooted, without "..", path separators
    /// or characters a file name cannot have.
    /// </summary>
    public static bool IsPlainFolderName(IFileSystem fileSystem, string name) =>
        name.Length > 0
        && !fileSystem.Path.IsPathRooted(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && name.IndexOfAny(['/', '\\']) < 0
        && name.IndexOfAny(fileSystem.Path.GetInvalidFileNameChars()) < 0;

    /// <summary>
    /// Makes <paramref name="folder"/> an empty folder owned by this patcher: creates it with the marker, or deletes everything in it
    /// except the marker. A non-empty folder without the marker belongs to someone else, and the run fails instead.
    /// </summary>
    public static void PrepareEmpty(IFileSystem fileSystem, string folder, ILog log)
    {
        var marker = fileSystem.Path.Combine(folder, MarkerFileName);
        if (!fileSystem.Directory.Exists(folder))
        {
            fileSystem.Directory.CreateDirectory(folder);
            WriteMarker(fileSystem, marker);
            log.Info($"Created the forward folder {folder}; enable it in MO2.");
            return;
        }

        var entries = fileSystem.Directory.EnumerateFileSystemEntries(folder).ToList();
        var hasMarker = entries.Any(entry => string.Equals(fileSystem.Path.GetFileName(entry), MarkerFileName, StringComparison.OrdinalIgnoreCase));
        if (!hasMarker && entries.Count > 0)
            throw new RunFailedException(
                $"The forward folder {folder} already exists and was not made by ItemLookMerger (no {MarkerFileName} file in it); it looks like another mod. "
                + "Pick another forward folder name, or empty that folder yourself.");

        // Only the folder's own entries are deleted. A link or junction (intact or broken) is removed itself, never followed;
        // Directory.Delete does not follow links inside a real subfolder either.
        var deleted = 0;
        foreach (var entry in entries)
        {
            if (string.Equals(fileSystem.Path.GetFileName(entry), MarkerFileName, StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = fileSystem.File.GetAttributes(entry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteLink(fileSystem, entry, isDirectory);
            else if (isDirectory) fileSystem.Directory.Delete(entry, recursive: true);
            else fileSystem.File.Delete(entry);
            deleted++;
        }
        if (!hasMarker) WriteMarker(fileSystem, marker);
        log.Verbose($"Emptied the forward folder {folder} ({deleted} entries removed).");
    }

    /// <summary>Removes a link without touching its target: a directory link is removed as an (empty) directory entry.</summary>
    private static void DeleteLink(IFileSystem fileSystem, string entry, bool isDirectory)
    {
        if (isDirectory) fileSystem.Directory.Delete(entry, recursive: false);
        else fileSystem.File.Delete(entry);
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

    private static string WithSeparator(string path) => path.TrimEnd('\\', '/') + '\\';

    private static void WriteMarker(IFileSystem fileSystem, string marker) =>
        fileSystem.File.WriteAllText(marker, "This folder is written by the ItemLookMerger Synthesis patcher and emptied at the start of each run.\n");
}
