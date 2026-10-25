using System.IO.Abstractions;
using ItemLookMerger.Core.Assets;
using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core.Mo2;

/// <summary>An MO2 instance folder as its ModOrganizer.ini describes it, before a profile is chosen.</summary>
public sealed record Mo2InstanceFolders(
    string InstanceDirectory,
    string BaseDirectory,
    string ModsDirectory,
    string ProfilesDirectory,
    string OverwriteDirectory,
    string GameDataDirectory,
    string? SelectedProfile)
{
    private const string BaseDirToken = "%BASE_DIR%";

    /// <summary>Reads ModOrganizer.ini in <paramref name="instanceDirectory"/>; fails the run when it or its gamePath is missing.</summary>
    public static Mo2InstanceFolders Read(IFileSystem fileSystem, string instanceDirectory)
    {
        var path = fileSystem.Path;
        var iniPath = path.Combine(instanceDirectory, "ModOrganizer.ini");
        if (!fileSystem.File.Exists(iniPath)) throw new RunFailedException($"MO2's ModOrganizer.ini is missing: {iniPath}");
        var ini = IniDocument.Parse(fileSystem.File.ReadAllText(iniPath));

        var gamePath = ini.Get("General", "gamePath");
        if (string.IsNullOrWhiteSpace(gamePath)) throw new RunFailedException($"{iniPath} names no gamePath in [General].");
        var selectedProfile = ini.Get("General", "selected_profile");

        // base_directory defaults to the instance folder; the others default to folders inside it.
        var baseDirectory = Normalize(path, Setting(ini, "base_directory", null) ?? instanceDirectory, instanceDirectory);
        string Folder(string key, string fallback) => Normalize(path, Setting(ini, key, baseDirectory) ?? path.Combine(baseDirectory, fallback), baseDirectory);
        return new Mo2InstanceFolders(
            Normalize(path, instanceDirectory, instanceDirectory),
            baseDirectory,
            Folder("mod_directory", "mods"),
            Folder("profiles_directory", "profiles"),
            Folder("overwrite_directory", "overwrite"),
            path.Combine(Normalize(path, gamePath, instanceDirectory), "Data"),
            string.IsNullOrWhiteSpace(selectedProfile) ? null : selectedProfile);
    }

    /// <summary>The names of the profile folders that hold a modlist.txt.</summary>
    public IReadOnlyList<string> ProfileNames(IFileSystem fileSystem) =>
        fileSystem.Directory.Exists(ProfilesDirectory)
            ? fileSystem.Directory.EnumerateDirectories(ProfilesDirectory)
                .Where(folder => fileSystem.File.Exists(fileSystem.Path.Combine(folder, "modlist.txt")))
                .Select(folder => fileSystem.Path.GetFileName(folder))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    /// <summary>A [Settings] path with %BASE_DIR% expanded, or null when unset.</summary>
    private static string? Setting(IniDocument ini, string key, string? baseDirectory)
    {
        var value = ini.Get("Settings", key);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return baseDirectory == null ? value : value.Replace(BaseDirToken, baseDirectory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>MO2 writes forward slashes; relative paths count from <paramref name="relativeTo"/>.</summary>
    internal static string Normalize(IPath path, string value, string relativeTo)
    {
        var cleaned = value.Trim().Replace('/', path.DirectorySeparatorChar);
        var full = path.GetFullPath(path.IsPathRooted(cleaned) ? cleaned : path.Combine(relativeTo, cleaned));
        // Keep "E:\" for a drive root, drop the trailing separator anywhere else.
        return full.Length > 3 ? full.TrimEnd(path.DirectorySeparatorChar) : full;
    }
}

/// <summary>
/// An MO2 instance with one of its profiles: where the mods are, which are enabled and in which priority (with Overwrite on top),
/// which plugins the profile has active, and where the real game Data folder is.
/// </summary>
public sealed class Mo2Instance
{
    /// <summary>MO2's Overwrite folder, as a pseudo-mod above every real mod (named so that no modlist.txt entry can match it).</summary>
    public const string OverwriteName = "(Overwrite)";

    private readonly IFileSystem _fileSystem;
    private readonly Dictionary<string, AssetFolder> _modAssets = new(StringComparer.OrdinalIgnoreCase);

    private Mo2Instance(IFileSystem fileSystem, Mo2InstanceFolders folders, string profileName, string modListText, IReadOnlyList<ModListEntry> mods, IReadOnlyList<ModKey>? activePlugins)
    {
        _fileSystem = fileSystem;
        Folders = folders;
        ProfileName = profileName;
        ModListText = modListText;
        Mods = mods;
        ActivePlugins = activePlugins;
    }

    public Mo2InstanceFolders Folders { get; }

    public string InstanceDirectory => Folders.InstanceDirectory;

    public string BaseDirectory => Folders.BaseDirectory;

    public string ModsDirectory => Folders.ModsDirectory;

    public string ProfilesDirectory => Folders.ProfilesDirectory;

    /// <summary>The game's real Data folder (gamePath\Data).</summary>
    public string GameDataDirectory => Folders.GameDataDirectory;

    public string ProfileName { get; }

    /// <summary>The profile's modlist.txt as written, to tell identical profiles apart from different ones.</summary>
    internal string ModListText { get; }

    /// <summary>Overwrite first (it beats every mod), then the managed mods of the profile in modlist.txt order (highest priority first).</summary>
    public IReadOnlyList<ModListEntry> Mods { get; }

    /// <summary>The plugins the profile's plugins.txt marks active, or null without a plugins.txt.</summary>
    public IReadOnlyList<ModKey>? ActivePlugins { get; }

    /// <summary>Reads <paramref name="profileName"/> of the instance; fails the run when its modlist.txt is missing.</summary>
    public static Mo2Instance Load(IFileSystem fileSystem, Mo2InstanceFolders folders, string profileName)
    {
        var profileFolder = fileSystem.Path.Combine(folders.ProfilesDirectory, profileName);
        var modListPath = fileSystem.Path.Combine(profileFolder, "modlist.txt");
        if (!fileSystem.File.Exists(modListPath)) throw new RunFailedException($"The MO2 profile's modlist.txt is missing: {modListPath}");
        var modListText = fileSystem.File.ReadAllText(modListPath);
        var pluginsPath = fileSystem.Path.Combine(profileFolder, "plugins.txt");
        var active = fileSystem.File.Exists(pluginsPath) ? PluginsTxt.ActivePlugins(fileSystem.File.ReadAllText(pluginsPath)) : null;
        IReadOnlyList<ModListEntry> mods = [new ModListEntry(OverwriteName, true, -1), .. ModList.Parse(modListText)];
        return new Mo2Instance(fileSystem, folders, profileName, modListText, mods, active);
    }

    /// <summary>Reads the instance in <paramref name="instanceDirectory"/> with its selected profile (or "Default").</summary>
    public static Mo2Instance Load(IFileSystem fileSystem, string instanceDirectory)
    {
        var folders = Mo2InstanceFolders.Read(fileSystem, instanceDirectory);
        return Load(fileSystem, folders, folders.SelectedProfile ?? "Default");
    }

    public string ModFolder(string modName) =>
        modName == OverwriteName ? Folders.OverwriteDirectory : _fileSystem.Path.Combine(ModsDirectory, modName);

    /// <summary>
    /// The mod that has <paramref name="dataRelativePath"/> as a loose file (a plugin name or an asset path).
    /// Enabled mods come first, then priority; null when no mod has it. <paramref name="exceptMod"/> is never returned.
    /// </summary>
    public ModListEntry? FindModWithFile(string dataRelativePath, string? exceptMod = null) =>
        ModsByPreference(exceptMod).FirstOrDefault(mod => _fileSystem.File.Exists(_fileSystem.Path.Combine(ModFolder(mod.Name), dataRelativePath)));

    /// <summary>
    /// The enabled mod whose BSAs hold <paramref name="dataRelativePath"/>, highest priority first, or null. Each mod's BSAs are read
    /// once, only when this is first asked about that mod.
    /// </summary>
    public ModListEntry? FindEnabledModWithArchivedFile(string dataRelativePath, string? exceptMod = null) =>
        ModsByPreference(exceptMod).Where(mod => mod.Enabled).FirstOrDefault(mod => ModAssets(mod.Name).ArchivesContain(dataRelativePath));

    /// <summary>The loose files and every BSA of a mod folder.</summary>
    internal AssetFolder ModAssets(string modName)
    {
        if (!_modAssets.TryGetValue(modName, out var folder))
        {
            folder = AssetFolder.WithAllArchives(_fileSystem, ModFolder(modName));
            _modAssets[modName] = folder;
        }
        return folder;
    }

    /// <summary>The mod's meta.ini facts, or null without a meta.ini.</summary>
    public ModMeta? ReadMeta(string modName)
    {
        var metaPath = _fileSystem.Path.Combine(ModFolder(modName), "meta.ini");
        return _fileSystem.File.Exists(metaPath) ? ModMeta.Parse(_fileSystem.File.ReadAllText(metaPath)) : null;
    }

    /// <summary>The mod's priority as a number where higher wins (Overwrite highest), or -1 without a mod.</summary>
    public int PriorityOf(ModListEntry? mod) => mod == null ? -1 : Mods.Count - 2 - mod.Priority;

    private IEnumerable<ModListEntry> ModsByPreference(string? exceptMod) =>
        Mods
            .Where(mod => !string.Equals(mod.Name, exceptMod, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mod => mod.Enabled ? 0 : 1)
            .ThenBy(mod => mod.Priority);
}
