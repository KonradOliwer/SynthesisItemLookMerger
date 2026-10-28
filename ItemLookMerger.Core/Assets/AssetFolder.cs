using System.IO.Abstractions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Noggog;

namespace ItemLookMerger.Core.Assets;

/// <summary>
/// The assets of one folder laid out like Data: loose files first, then the files in its BSAs. Which BSAs count is set per folder:
/// an MO2 mod folder of a look plugin counts the BSAs that belong to the plugin, other mod folders all their BSAs, and the game's
/// Data folder only the vanilla ones.
/// </summary>
internal sealed class AssetFolder
{
    private readonly IFileSystem _fileSystem;
    private readonly string _folder;
    private readonly Func<string, bool> _archiveApplies;
    private Dictionary<string, IArchiveFile>? _archiveFiles;
    private ILookup<string, string>? _byFileName;

    private AssetFolder(IFileSystem fileSystem, string folder, Func<string, bool> archiveApplies)
    {
        _fileSystem = fileSystem;
        _folder = folder;
        _archiveApplies = archiveApplies;
    }

    /// <summary>An MO2 mod folder holding <paramref name="plugin"/>: its loose files and the BSAs named after the plugin.</summary>
    public static AssetFolder ForMod(IFileSystem fileSystem, string folder, ModKey plugin) =>
        new(fileSystem, folder, archiveName => Archive.IsApplicable(GameRelease.SkyrimSE, plugin, new FileName(archiveName)));

    /// <summary>A folder with every BSA in it.</summary>
    public static AssetFolder WithAllArchives(IFileSystem fileSystem, string folder) => new(fileSystem, folder, _ => true);

    /// <summary>A folder with only the BSAs <paramref name="archiveApplies"/> accepts (by file name).</summary>
    public static AssetFolder WithArchives(IFileSystem fileSystem, string folder, Func<string, bool> archiveApplies) =>
        new(fileSystem, folder, archiveApplies);

    public bool Contains(string path) => LooseExists(path) || ArchivesContain(path);

    public bool LooseExists(string path) => LoosePath(path) is { } loose && _fileSystem.File.Exists(loose);

    public bool ArchivesContain(string path) => ArchiveFiles().ContainsKey(AssetPath.Key(path));

    /// <summary>The file's bytes, or null when the folder does not have it.</summary>
    public byte[]? Read(string path)
    {
        if (LoosePath(path) is { } loose && _fileSystem.File.Exists(loose)) return _fileSystem.File.ReadAllBytes(loose);
        return ArchiveFiles().TryGetValue(AssetPath.Key(path), out var file) ? file.GetBytes() : null;
    }

    /// <summary>
    /// The data-relative paths of the loose files and archived files with this file name, ordered. The file names of the folder are
    /// indexed once, on first use; a folder that cannot be listed counts as empty.
    /// </summary>
    public IReadOnlyList<string> FindByFileName(string fileName)
    {
        if (_byFileName == null)
        {
            var paths = new List<string>();
            try
            {
                if (_fileSystem.Directory.Exists(_folder))
                    paths.AddRange(_fileSystem.Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories)
                        .Select(file => _fileSystem.Path.GetRelativePath(_folder, file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed only means no same-named file is found in it.
            }
            paths.AddRange(ArchiveFiles().Keys);
            _byFileName = paths
                .Select(AssetPath.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToLookup(path => path[(path.LastIndexOf('\\') + 1)..], StringComparer.OrdinalIgnoreCase);
        }
        return _byFileName[fileName].ToList();
    }

    /// <summary>The loose file's full path, or null when the path would leave the folder.</summary>
    private string? LoosePath(string path) => AssetPath.Inside(_fileSystem, _folder, path);

    /// <summary>Every file of the applicable BSAs, read once on first use. A BSA that cannot be read is left out.</summary>
    private Dictionary<string, IArchiveFile> ArchiveFiles()
    {
        if (_archiveFiles != null) return _archiveFiles;
        _archiveFiles = new Dictionary<string, IArchiveFile>(StringComparer.OrdinalIgnoreCase);
        if (!_fileSystem.Directory.Exists(_folder)) return _archiveFiles;
        var archives = _fileSystem.Directory.EnumerateFiles(_folder, "*.bsa")
            .Where(archive => _archiveApplies(_fileSystem.Path.GetFileName(archive)))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            try
            {
                foreach (var file in Archive.CreateReader(GameRelease.SkyrimSE, archive, _fileSystem).Files)
                    _archiveFiles.TryAdd(AssetPath.Key(file.Path), file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotImplementedException or UnauthorizedAccessException)
            {
                // An unreadable archive only means its files are not found here.
            }
        }
        return _archiveFiles;
    }
}

/// <summary>Checks on the data-relative asset paths that come from plugins, NIFs and BSAs.</summary>
internal static class AssetPath
{
    /// <summary>The dictionary key of a path: backslashes, no leading separator.</summary>
    public static string Key(string path) => path.Replace('/', '\\').TrimStart('\\');

    /// <summary>
    /// The key of a path written by hand (a BodySlide OutputPath): like <see cref="Key"/>, without a leading "Data\" or ".\" segment,
    /// doubled separators or a trailing separator.
    /// </summary>
    public static string Normalize(string path)
    {
        var segments = Key(path).Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != ".").ToList();
        if (segments.Count > 1 && string.Equals(segments[0], "Data", StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(0);
        return string.Join('\\', segments);
    }

    /// <summary>Whether the path is data-relative and stays inside Data: not rooted, no ".." segment, no empty name.</summary>
    public static bool IsSafe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var key = Key(path);
        if (key.Length == 0 || key.Contains(':') || Path.IsPathRooted(key)) return false;
        return key.Split('\\').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    /// <summary>The path's full location inside <paramref name="folder"/>, or null when it is unsafe or would land outside it.</summary>
    public static string? Inside(IFileSystem fileSystem, string folder, string path)
    {
        if (!IsSafe(path)) return null;
        var root = fileSystem.Path.GetFullPath(folder).TrimEnd('\\', '/') + fileSystem.Path.DirectorySeparatorChar;
        var full = fileSystem.Path.GetFullPath(fileSystem.Path.Combine(root, Key(path)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
