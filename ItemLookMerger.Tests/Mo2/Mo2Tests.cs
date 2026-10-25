using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Tests.Mo2;

public class Mo2Tests
{
    [Theory]
    [InlineData(@"@ByteArray(Default)", "Default")]
    [InlineData(@"@ByteArray(C:\\Games\\Skyrim Special Edition)", @"C:\Games\Skyrim Special Edition")]
    [InlineData(@"@ByteArray(Survival \xc3\xa4)", "Survival ä")]
    [InlineData(@"""quoted \""value\""""", @"quoted ""value""")]
    [InlineData(@"C:/Modding/MO2", "C:/Modding/MO2")]
    [InlineData(@"  spaced  ", "spaced")]
    // Qt5 writes non-ASCII characters of plain (QString) values as \x plus the UTF-16 code unit, up to four hex digits.
    [InlineData(@"C:/Users/\x418\x432\x430\x43d/AppData", "C:/Users/Иван/AppData")]
    [InlineData(@"\x4e2d\x6587", "中文")]
    [InlineData(@"caf\xe9", "café")]
    public void Ini_values_are_decoded_like_QSettings(string raw, string expected) =>
        Assert.Equal(expected, IniDocument.DecodeValue(raw));

    [Fact]
    public void Plain_utf16_escapes_in_an_ini_file_decode_to_the_right_path()
    {
        var ini = IniDocument.Parse("[Settings]\r\nbase_directory=C:/Users/\\x418\\x432\\x430\\x43d/MO2\r\n");
        Assert.Equal("C:/Users/Иван/MO2", ini.Get("Settings", "base_directory"));
    }

    [Fact]
    public void Ini_sections_and_keys_ignore_case_and_comments()
    {
        var ini = IniDocument.Parse("; comment\r\n[General]\r\nGamePath=x\r\n# other\r\n[settings]\r\nbase_directory = y\r\n");
        Assert.Equal("x", ini.Get("general", "gamepath"));
        Assert.Equal("y", ini.Get("Settings", "base_directory"));
        Assert.Null(ini.Get("Settings", "mod_directory"));
    }

    private const string GlobalExe = @"C:\Program Files\Mod Organizer\ModOrganizer.exe";
    private static readonly string PortableExe = Path.Combine(Mo2Fixtures.PortableFolder, "ModOrganizer.exe");
    private readonly TestLog _log = new();

    private Mo2Instance Locate(IMo2Environment environment, MockFileSystem fileSystem, string dataFolder, IReadOnlyList<ModKey>? plugins = null) =>
        Mo2Locator.Locate(environment, fileSystem, new LoadOrderFacts(dataFolder, plugins ?? Mo2Fixtures.FixturePlugins), _log);

    [Fact]
    public void Portable_instance_is_found_next_to_the_executable_with_default_paths()
    {
        var fileSystem = Mo2Fixtures.Portable();
        var environment = new FakeMo2Environment(PortableExe);

        var instance = Locate(environment, fileSystem, Mo2Fixtures.PortableData);

        Assert.Equal(Mo2Fixtures.PortableFolder, instance.InstanceDirectory);
        Assert.Equal(Mo2Fixtures.PortableFolder, instance.BaseDirectory);
        Assert.Equal(@"C:\MO2Portable\mods", instance.ModsDirectory);
        Assert.Equal(@"C:\MO2Portable\profiles", instance.ProfilesDirectory);
        Assert.Equal("My Profile", instance.ProfileName);
        Assert.Equal(@"C:\Games\Skyrim Special Edition\Data", instance.GameDataDirectory);
    }

    [Fact]
    public void Global_instance_is_read_from_local_app_data_and_expands_base_dir()
    {
        var fileSystem = Mo2Fixtures.Global();
        var environment = new FakeMo2Environment(GlobalExe, Mo2Fixtures.LocalAppData, Mo2Fixtures.GlobalInstanceName);

        var instance = Locate(environment, fileSystem, Mo2Fixtures.GlobalData);

        Assert.Equal(Path.Combine(Mo2Fixtures.LocalAppData, "ModOrganizer", Mo2Fixtures.GlobalInstanceName), instance.InstanceDirectory);
        Assert.Equal(Mo2Fixtures.GlobalBase, instance.BaseDirectory);
        Assert.Equal(@"E:\Modding\SkyrimSE\my mods", instance.ModsDirectory);
        Assert.Equal(@"E:\Modding\SkyrimSE\profiles", instance.ProfilesDirectory);
        Assert.Equal("Survival ä", instance.ProfileName);
        Assert.Equal(@"D:\Steam\steamapps\common\Skyrim Special Edition\Data", instance.GameDataDirectory);
    }

    [Fact]
    public void Missing_mo2_fails_the_run()
    {
        var ex = Assert.Throws<RunFailedException>(() => Locate(new FakeMo2Environment(null), new MockFileSystem(), Mo2Fixtures.PortableData));
        Assert.Contains("through MO2", ex.Message);
    }

    [Fact]
    public void A_global_instance_is_found_without_the_registry_value_by_checking_every_instance()
    {
        var fileSystem = Mo2Fixtures.Global();
        var environment = new FakeMo2Environment(GlobalExe, Mo2Fixtures.LocalAppData, globalInstance: null);

        Assert.Equal("Survival ä", Locate(environment, fileSystem, Mo2Fixtures.GlobalData).ProfileName);
    }

    [Fact]
    public void A_leftover_portable_ini_for_another_game_does_not_beat_the_matching_global_instance()
    {
        var fileSystem = Mo2Fixtures.Global();
        fileSystem.AddFile(Path.Combine(Path.GetDirectoryName(GlobalExe)!, "ModOrganizer.ini"), new MockFileData(Mo2Fixtures.Fixture(@"Portable\ModOrganizer.ini")));
        var environment = new FakeMo2Environment(GlobalExe, Mo2Fixtures.LocalAppData, Mo2Fixtures.GlobalInstanceName);

        var instance = Locate(environment, fileSystem, Mo2Fixtures.GlobalData);

        Assert.Equal(Mo2Fixtures.GlobalBase, instance.BaseDirectory);
        Assert.Contains(_log.Verboses, line => line.Contains("is for the game at"));
    }

    [Fact]
    public void Only_the_profile_whose_plugins_txt_matches_the_load_order_is_used()
    {
        var fileSystem = Mo2Fixtures.Portable();
        Mo2Fixtures.AddProfileFiles(fileSystem, @"C:\MO2Portable\profiles\Other", "*BetterIron.esp\r\n*Looks.esp\r\n");

        var instance = Locate(new FakeMo2Environment(PortableExe), fileSystem, Mo2Fixtures.PortableData);

        Assert.Equal("My Profile", instance.ProfileName);
        Assert.Contains(_log.Verboses, line => line.Contains("profile \"Other\"") && line.Contains("Looks.esp"));
    }

    [Fact]
    public void Copies_of_one_profile_with_an_identical_mod_list_are_accepted()
    {
        var fileSystem = Mo2Fixtures.Portable();
        Mo2Fixtures.AddProfileFiles(fileSystem, @"C:\MO2Portable\profiles\Copy");

        var instance = Locate(new FakeMo2Environment(PortableExe), fileSystem, Mo2Fixtures.PortableData);

        Assert.Contains(instance.ProfileName, new[] { "Copy", "My Profile" });
        Assert.Contains(_log.Infos, line => line.Contains("match equally"));
    }

    [Fact]
    public void Several_differing_matching_profiles_fail_the_run_listing_them()
    {
        var fileSystem = Mo2Fixtures.Portable();
        // Same plugins, but another mod order: the result could differ, so it is not a copy.
        fileSystem.AddFile(@"C:\MO2Portable\profiles\Reordered\modlist.txt", new MockFileData("+Stats Overhaul\r\n+Better Iron Armor\r\n-Disabled Looks\r\n"));
        fileSystem.AddFile(@"C:\MO2Portable\profiles\Reordered\plugins.txt", new MockFileData(Mo2Fixtures.Fixture("plugins.txt")));

        var ex = Assert.Throws<RunFailedException>(() => Locate(new FakeMo2Environment(PortableExe), fileSystem, Mo2Fixtures.PortableData));

        Assert.Contains("Several MO2 profiles", ex.Message);
        Assert.Contains("\"Reordered\"", ex.Message);
        Assert.Contains("\"My Profile\"", ex.Message);
    }

    [Fact]
    public void No_matching_profile_fails_the_run_with_the_reasons()
    {
        var fileSystem = Mo2Fixtures.Portable();
        IReadOnlyList<ModKey> plugins = [ModKey.FromFileName("Skyrim.esm"), ModKey.FromFileName("BetterIron.esp"), ModKey.FromFileName("Stats.esp"), ModKey.FromFileName("Looks.esp")];

        var ex = Assert.Throws<RunFailedException>(() => Locate(new FakeMo2Environment(PortableExe), fileSystem, Mo2Fixtures.PortableData, plugins));

        Assert.Contains("No MO2 instance and profile matches", ex.Message);
        Assert.Contains("Looks.esp", ex.Message);
    }

    [Fact]
    public void A_profile_whose_active_plugin_is_only_in_a_disabled_mod_is_rejected()
    {
        var fileSystem = Mo2Fixtures.Portable();
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableProfile, "plugins.txt"), new MockFileData("*BetterIron.esp\r\n*Stats.esp\r\n*Looks.esp\r\n"));
        IReadOnlyList<ModKey> plugins = [.. Mo2Fixtures.FixturePlugins, ModKey.FromFileName("Looks.esp")];

        var ex = Assert.Throws<RunFailedException>(() => Locate(new FakeMo2Environment(PortableExe), fileSystem, Mo2Fixtures.PortableData, plugins));

        Assert.Contains("only in disabled mods", ex.Message);
    }

    [Fact]
    public void Overwrite_counts_as_the_highest_priority_mod()
    {
        var fileSystem = Mo2Fixtures.Portable();
        fileSystem.AddFile(@"C:\MO2Portable\overwrite\Generated.esp", new MockFileData([]));
        var instance = Mo2Instance.Load(fileSystem, Mo2Fixtures.PortableFolder);

        var overwrite = instance.FindModWithFile("Generated.esp");

        Assert.Equal(Mo2Instance.OverwriteName, overwrite?.Name);
        Assert.Equal(@"C:\MO2Portable\overwrite", instance.ModFolder(Mo2Instance.OverwriteName));
        Assert.True(instance.PriorityOf(overwrite) > instance.PriorityOf(instance.Mods.First(mod => mod.Name == "Disabled Looks")));
    }

    [Fact]
    public void Modlist_keeps_order_and_flags_and_skips_separators_and_unmanaged_entries()
    {
        var mods = ModList.Parse(Mo2Fixtures.Fixture("modlist.txt"));

        Assert.Equal(
            new[]
            {
                new ModListEntry("Disabled Looks", false, 0),
                new ModListEntry("Better Iron Armor", true, 1),
                new ModListEntry("Stats Overhaul", true, 2),
                new ModListEntry("Base Fixes", true, 3),
            },
            mods);
    }

    [Fact]
    public void Plugin_lookup_prefers_enabled_mods_then_priority()
    {
        var fileSystem = Mo2Fixtures.Portable();
        var mods = @"C:\MO2Portable\mods";
        fileSystem.AddFile(Path.Combine(mods, "Disabled Looks", "Looks.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(mods, "Base Fixes", "Looks.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(mods, "Stats Overhaul", "Looks.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(mods, "Disabled Looks", "Only.esp"), new MockFileData([]));
        var instance = Mo2Instance.Load(fileSystem, Mo2Fixtures.PortableFolder);

        Assert.Equal("Stats Overhaul", instance.FindModWithFile("Looks.esp")?.Name);
        Assert.Equal("Disabled Looks", instance.FindModWithFile("Only.esp")?.Name);
        Assert.Null(instance.FindModWithFile("Nowhere.esp"));
    }

    [Fact]
    public void Meta_ini_general_facts_and_nexus_link_are_read()
    {
        var fileSystem = Mo2Fixtures.Portable();
        fileSystem.AddFile(@"C:\MO2Portable\mods\Better Iron Armor\meta.ini", new MockFileData(Mo2Fixtures.Fixture("meta.ini")));
        var instance = Mo2Instance.Load(fileSystem, Mo2Fixtures.PortableFolder);

        var meta = instance.ReadMeta("Better Iron Armor");

        Assert.NotNull(meta);
        Assert.Equal("12345", meta.ModId);
        Assert.Equal("1.2.0", meta.Version);
        Assert.Equal("Better Iron Armor-12345-1-2-0-1700000000.7z", meta.InstallationFile);
        Assert.Equal("Nexus", meta.Repository);
        Assert.Equal(string.Empty, meta.Url);
        Assert.Equal("https://www.nexusmods.com/skyrimspecialedition/mods/12345", meta.NexusUrl);
        Assert.Null(instance.ReadMeta("Stats Overhaul"));
    }
}
