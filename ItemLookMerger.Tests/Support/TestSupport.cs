using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Tests.Support;

/// <summary>Collects the log lines by kind.</summary>
internal sealed class TestLog : ILog
{
    public List<string> Infos { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Verboses { get; } = [];

    public void Info(string text) => Infos.Add(text);
    public void Warning(string text) => Warnings.Add(text);
    public void Error(string text) => Errors.Add(text);
    public void Verbose(string text) => Verboses.Add(text);
}

/// <summary>Fixed answers for the MO2 search.</summary>
internal sealed class FakeMo2Environment(string? executable, string? localAppData = null, string? globalInstance = null) : IMo2Environment
{
    public Mo2ProcessLookup FindModOrganizer() => new(executable);
    public string? LocalAppData => localAppData;
    public string? CurrentGlobalInstance() => globalInstance;
}

/// <summary>MO2 instances on an in-memory file system, built from the files in Fixtures\Mo2.</summary>
internal static class Mo2Fixtures
{
    public const string PortableFolder = @"C:\MO2Portable";
    public const string PortableMods = @"C:\MO2Portable\mods";
    public const string PortableProfile = @"C:\MO2Portable\profiles\My Profile";
    public const string PortableData = @"C:\Games\Skyrim Special Edition\Data";
    public const string LocalAppData = @"C:\Users\me\AppData\Local";
    public const string GlobalInstanceName = "Skyrim SE";
    public const string GlobalBase = @"E:\Mods\SkyrimSE";
    public const string GlobalData = @"D:\Steam\steamapps\common\Skyrim Special Edition\Data";

    /// <summary>The enabled plugins of the fixture profiles' plugins.txt, plus the game's Skyrim.esm.</summary>
    public static readonly IReadOnlyList<ModKey> FixturePlugins =
        [ModKey.FromFileName("Skyrim.esm"), ModKey.FromFileName("BetterIron.esp"), ModKey.FromFileName("Stats.esp")];

    public static string Fixture(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mo2", relativePath));

    /// <summary>
    /// A portable instance with default paths: mods and profiles inside the instance folder. Its profile has BetterIron.esp
    /// (mod "Better Iron Armor") and Stats.esp (mod "Stats Overhaul") active; Looks.esp sits in the disabled mod "Disabled Looks".
    /// </summary>
    public static MockFileSystem Portable()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(Path.Combine(PortableFolder, "ModOrganizer.exe"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(PortableFolder, "ModOrganizer.ini"), new MockFileData(Fixture(@"Portable\ModOrganizer.ini")));
        AddProfileFiles(fileSystem, PortableProfile);
        AddFixturePlugins(fileSystem, PortableMods, PortableData);
        return fileSystem;
    }

    /// <summary>A global instance whose ini moves base, mods and profiles elsewhere with %BASE_DIR%.</summary>
    public static MockFileSystem Global()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.AddFile(@"C:\Program Files\Mod Organizer\ModOrganizer.exe", new MockFileData([]));
        fileSystem.AddFile(
            Path.Combine(LocalAppData, "ModOrganizer", GlobalInstanceName, "ModOrganizer.ini"), new MockFileData(Fixture(@"Global\ModOrganizer.ini")));
        AddProfileFiles(fileSystem, Path.Combine(GlobalBase, "profiles", "Survival ä"));
        AddFixturePlugins(fileSystem, Path.Combine(GlobalBase, "my mods"), GlobalData);
        return fileSystem;
    }

    public static void AddProfileFiles(MockFileSystem fileSystem, string profileFolder, string? pluginsTxt = null)
    {
        fileSystem.AddFile(Path.Combine(profileFolder, "modlist.txt"), new MockFileData(Fixture("modlist.txt")));
        fileSystem.AddFile(Path.Combine(profileFolder, "plugins.txt"), new MockFileData(pluginsTxt ?? Fixture("plugins.txt")));
    }

    public static void AddFixturePlugins(MockFileSystem fileSystem, string modsFolder, string dataFolder)
    {
        fileSystem.AddFile(Path.Combine(modsFolder, "Better Iron Armor", "BetterIron.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(modsFolder, "Stats Overhaul", "Stats.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(modsFolder, "Disabled Looks", "Looks.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(dataFolder, "Skyrim.esm"), new MockFileData([]));
    }

    /// <summary>
    /// Makes the portable profile match <paramref name="loadOrder"/>: all of it active in plugins.txt, and each plugin no mod holds
    /// put into the game's Data folder. <paramref name="linesAfter"/> follows in plugins.txt (the patch's line and what comes below it,
    /// which Synthesis leaves out of its load order).
    /// </summary>
    public static void MatchPortableProfile(MockFileSystem fileSystem, IEnumerable<ModKey> loadOrder, string linesAfter = "")
    {
        var plugins = loadOrder.ToList();
        fileSystem.AddFile(
            Path.Combine(PortableProfile, "plugins.txt"),
            new MockFileData(string.Concat(plugins.Select(plugin => $"*{plugin.FileName}\r\n")) + linesAfter));
        foreach (var plugin in plugins)
        {
            var held = fileSystem.Directory.EnumerateDirectories(PortableMods).Any(mod => fileSystem.File.Exists(Path.Combine(mod, plugin.FileName)));
            if (!held) fileSystem.AddFile(Path.Combine(PortableData, plugin.FileName), new MockFileData([]));
        }
    }
}
