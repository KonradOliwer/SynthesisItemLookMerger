using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Merging;
using ItemLookMerger.Tests.Support;

namespace ItemLookMerger.Tests.Assets;

public class AssetTests
{
    private const string Mods = Mo2Fixtures.PortableMods;
    private static readonly string Forward = Path.Combine(Mods, "OUTPUT ItemLookMerger");

    private readonly MockFileSystem _fileSystem = Mo2Fixtures.Portable();
    private readonly TestLog _log = new();

    private AssetCopyResult Copy(params AssetNeed[] needs) =>
        AssetCopier.Copy(
            needs,
            Mo2Instance.Load(_fileSystem, Mo2Fixtures.PortableFolder),
            Forward,
            Mo2Fixtures.PortableData,
            Mo2Fixtures.FixturePlugins,
            _fileSystem,
            _log,
            isVanilla: path => path.StartsWith(@"Textures\Vanilla\", StringComparison.OrdinalIgnoreCase));

    private static LookSource Source(string plugin, string mod, int priority) => new(TestPlugins.NewMod(plugin), mod, priority);

    [Theory]
    [InlineData(@"textures\armor\x_d.dds", @"textures\armor\x_d.dds")]
    [InlineData(@"armor/x_n.dds", @"Textures\armor\x_n.dds")]
    [InlineData(@"Data\Textures\armor\x_d.dds", @"Textures\armor\x_d.dds")]
    [InlineData(@"C:\Users\someone\Skyrim\Data\textures\armor\x_d.dds", @"textures\armor\x_d.dds")]
    [InlineData(@"\textures\x.dds", @"textures\x.dds")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Nif_texture_paths_are_made_data_relative(string? raw, string? expected) =>
        Assert.Equal(expected, NifTextures.Normalize(raw));

    [Fact]
    public void An_unreadable_nif_has_no_textures() => Assert.Empty(NifTextures.Read([1, 2, 3]));

    [Theory]
    [InlineData(@"Textures\a.dds", true)]
    [InlineData(@"textures\..\..\Other Mod\Plugin.esp", false)]
    [InlineData(@"..\x.dds", false)]
    [InlineData(@"C:\x.dds", false)]
    [InlineData(@"Textures\.\a.dds", false)]
    [InlineData(@"Textures\\a.dds", false)]
    [InlineData("", false)]
    public void Only_paths_inside_data_are_safe(string path, bool safe) => Assert.Equal(safe, AssetPath.IsSafe(path));

    [Fact]
    public void Assets_come_only_from_the_look_mod_vanilla_ones_are_skipped_and_others_warned()
    {
        _fileSystem.AddFile(Path.Combine(Mods, "Disabled Looks", @"Meshes\Armor\Fancy\Fancy_go.nif"), new MockFileData([9, 9]));
        _fileSystem.AddFile(Path.Combine(Mods, "Base Fixes", @"Textures\Armor\Fancy\other_d.dds"), new MockFileData([2]));
        _fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableData, @"Textures\Armor\manual_d.dds"), new MockFileData([3]));
        var source = Source("Look.esp", "Disabled Looks", 5);

        var result = Copy(
            new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", source),
            new AssetNeed(@"Textures\Vanilla\base_d.dds", "ArmorIronCuirass", source),
            new AssetNeed(@"Textures\Armor\Fancy\other_d.dds", "ArmorIronCuirass", source),
            new AssetNeed(@"Textures\Armor\manual_d.dds", "ArmorIronCuirass", source),
            new AssetNeed(@"Textures\Armor\nowhere_d.dds", "ArmorIronCuirass", source));

        Assert.Equal(new AssetCopyResult(Copied: 1, BaseGameSkipped: 1, FromOtherMods: 2, Missing: 1), result);
        Assert.Equal([9, 9], _fileSystem.File.ReadAllBytes(Path.Combine(Forward, @"Meshes\Armor\Fancy\Fancy_go.nif")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(Forward, @"Textures\Armor\Fancy\other_d.dds")));
        Assert.Contains(_log.Warnings, warning => warning.Contains(@"Textures\Armor\Fancy\other_d.dds") && warning.Contains("\"Base Fixes\"") && warning.Contains("ArmorIronCuirass"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("manual_d.dds") && warning.Contains("no MO2 mod provides"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("nowhere_d.dds") && warning.Contains("no MO2 mod or vanilla archive"));
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("base_d.dds"));
    }

    [Fact]
    public void A_path_two_look_mods_provide_comes_from_the_higher_priority_mod_with_a_warning_when_they_differ()
    {
        _fileSystem.AddFile(Path.Combine(Mods, "Better Iron Armor", @"Textures\Steel\steel.dds"), new MockFileData([1]));
        _fileSystem.AddFile(Path.Combine(Mods, "Stats Overhaul", @"Textures\Steel\steel.dds"), new MockFileData([2]));
        var high = Source("High.esp", "Better Iron Armor", 9);
        var low = Source("Low.esp", "Stats Overhaul", 3);

        // The lower-priority source asks first; the priority still decides.
        var result = Copy(new AssetNeed(@"Textures\Steel\steel.dds", "ArmorSteelX", low), new AssetNeed(@"Textures\Steel\steel.dds", "ArmorSteelY", high));

        Assert.Equal(1, result.Copied);
        Assert.Equal([1], _fileSystem.File.ReadAllBytes(Path.Combine(Forward, @"Textures\Steel\steel.dds")));
        Assert.Contains(_log.Warnings, warning => warning.Contains("different content"));
    }

    [Fact]
    public void A_path_that_would_leave_the_forward_folder_is_never_written()
    {
        _fileSystem.AddFile(Path.Combine(Mods, "Other Mod", "Plugin.esp"), new MockFileData([5]));
        var source = Source("Look.esp", "Disabled Looks", 5);

        var result = Copy(new AssetNeed(@"textures\..\..\Other Mod\Plugin.esp", "ArmorIronCuirass", source));

        Assert.Equal(0, result.Copied);
        Assert.Equal([5], _fileSystem.File.ReadAllBytes(Path.Combine(Mods, "Other Mod", "Plugin.esp")));
        Assert.Contains(_log.Warnings, warning => warning.Contains("leaves the Data folder"));
    }
}
