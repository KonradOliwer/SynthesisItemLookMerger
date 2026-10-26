using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Merging;
using ItemLookMerger.Tests.Support;
using NiflySharp;
using NiflySharp.Blocks;

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
    [InlineData(@"armor\textures\x_d.dds", @"Textures\armor\textures\x_d.dds")]
    [InlineData(@"Data\armor\textures\x_d.dds", @"Textures\armor\textures\x_d.dds")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Nif_texture_paths_are_made_data_relative(string? raw, string? expected) =>
        Assert.Equal(expected, NifReferences.NormalizeTexture(raw));

    [Theory]
    [InlineData(@"SKSE\Plugins\hdtSkinnedMeshConfigs\foo.xml", @"SKSE\Plugins\hdtSkinnedMeshConfigs\foo.xml")]
    [InlineData(@"Data/meshes/armor/foo.xml", @"meshes\armor\foo.xml")]
    [InlineData("  ", null)]
    public void Nif_smp_config_paths_are_made_data_relative(string? raw, string? expected) =>
        Assert.Equal(expected, NifReferences.NormalizeData(raw));

    [Theory]
    [InlineData(@"armor\foo\foo.tri", @"meshes\armor\foo\foo.tri")]
    [InlineData(@"meshes\armor\foo.tri", @"meshes\armor\foo.tri")]
    [InlineData(@"Data\Meshes/armor/foo.tri", @"Meshes\armor\foo.tri")]
    [InlineData(@"armor\x\meshes\a.tri", @"meshes\armor\x\meshes\a.tri")]
    [InlineData(@"Data\armor\x\meshes\a.tri", @"meshes\armor\x\meshes\a.tri")]
    [InlineData(null, null)]
    public void Nif_tri_paths_are_made_data_relative(string? raw, string? expected) =>
        Assert.Equal(expected, NifReferences.NormalizeUnder(raw, "meshes"));

    [Fact]
    public void An_unreadable_nif_has_no_references() => Assert.Empty(NifReferences.Read([1, 2, 3]));

    private static byte[] Nif(string[]? textures = null, string? smp = null, string? tri = null)
    {
        var nif = new NifFile(NiVersion.GetSSE(), true);
        var root = nif.GetRootNode();
        if (textures != null)
        {
            var textureSet = nif.AddBlock(new BSShaderTextureSet { Textures = textures.Select(texture => new NiString4(texture)).ToList() });
            var shader = nif.AddBlock(new BSLightingShaderProperty { TextureSetRef = new NiBlockRef<BSShaderTextureSet>(textureSet) });
            var shape = nif.AddBlock(new BSTriShape { ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(shader) });
            root.Children.AddBlockRef(shape);
        }
        void AddExtra(string name, string value)
        {
            var id = nif.AddBlock(new NiStringExtraData { Name = new NiStringRef(name), StringData = new NiStringRef(value) });
            root.ExtraDataList ??= new NiBlockRefArray<NiExtraData>();
            root.ExtraDataList.AddBlockRef(id);
        }
        if (smp != null) AddExtra("HDT Skinned Mesh Physics Object", smp);
        if (tri != null) AddExtra("BODYTRI", tri);
        using var stream = new MemoryStream();
        Assert.Equal(0, nif.Save(stream));
        return stream.ToArray();
    }

    private void AddLookFile(string relativePath, byte[] bytes, string mod = "Disabled Looks") =>
        _fileSystem.AddFile(Path.Combine(Mods, mod, relativePath), new MockFileData(bytes));

    private void AddLookFile(string relativePath, string text, string mod = "Disabled Looks") =>
        _fileSystem.AddFile(Path.Combine(Mods, mod, relativePath), new MockFileData(text));

    private bool InForward(string relativePath) => _fileSystem.File.Exists(Path.Combine(Forward, relativePath));

    private const string SliderSets = @"CalienteTools\BodySlide\SliderSets";
    private const string ShapeData = @"CalienteTools\BodySlide\ShapeData";
    private const string SliderGroups = @"CalienteTools\BodySlide\SliderGroups";

    private static string Osp(string name, string dataFolder, string outputPath, string outputFile, bool genWeights = false) =>
        $"""
        <SliderSetInfo version="1"><SliderSet name="{name}"><DataFolder>{dataFolder}</DataFolder><SourceFile>{name}.nif</SourceFile>
        <OutputPath>{outputPath}</OutputPath><OutputFile GenWeights="{(genWeights ? "true" : "false")}">{outputFile}</OutputFile></SliderSet></SliderSetInfo>
        """;

    [Fact]
    public void An_smp_config_a_nif_names_is_copied_from_the_look_mod()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", Nif(smp: @"SKSE\Plugins\hdtSkinnedMeshConfigs\fancy.xml"));
        AddLookFile(@"SKSE\Plugins\hdtSkinnedMeshConfigs\fancy.xml", "<physics/>");

        var result = Copy(new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)));

        Assert.Equal(2, result.Copied);
        Assert.True(InForward(@"SKSE\Plugins\hdtSkinnedMeshConfigs\fancy.xml"));
    }

    [Fact]
    public void A_bodytri_file_a_nif_names_is_copied_and_a_missing_one_is_warned_about()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", Nif(tri: @"armor\fancy\fancy.tri"));
        AddLookFile(@"Meshes\Armor\Fancy\fancy.tri", [7]);
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_1.nif", Nif(tri: @"armor\fancy\lost.tri"));
        var source = Source("Look.esp", "Disabled Looks", 5);

        var result = Copy(
            new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", source),
            new AssetNeed(@"Meshes\Armor\Fancy\Fancy_1.nif", "ArmorIronCuirass", source));

        Assert.Equal(new AssetCopyResult(Copied: 3, BaseGameSkipped: 0, FromOtherMods: 0, Missing: 1), result);
        Assert.True(InForward(@"meshes\armor\fancy\fancy.tri"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("lost.tri"));
    }

    [Fact]
    public void A_bodyslide_project_its_shape_data_and_group_are_copied_for_a_needed_mesh_and_unrelated_sets_are_not()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", Nif());
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Fancy Set", "Fancy", @"meshes\armor\fancy", "Fancy_go"));
        AddLookFile($@"{SliderSets}\Other.osp", Osp("Other Set", "Other", @"meshes\armor\other", "Other_go"));
        AddLookFile($@"{ShapeData}\Fancy\Fancy.nif", [1]);
        AddLookFile($@"{ShapeData}\Fancy\Fancy.tri", [2]);
        AddLookFile($@"{ShapeData}\Other\Other.nif", [3]);
        AddLookFile($@"{SliderGroups}\Fancy.xml", """<SliderGroups><Group name="Fancy"><Member name="Fancy Set"/></Group></SliderGroups>""");
        AddLookFile($@"{SliderGroups}\Other.xml", """<SliderGroups><Group name="Other"><Member name="Other Set"/></Group></SliderGroups>""");

        var result = Copy(new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)));

        Assert.Equal(5, result.Copied);
        Assert.True(InForward(@"Meshes\Armor\Fancy\Fancy_go.nif"));
        Assert.True(InForward($@"{SliderSets}\Fancy.osp"));
        Assert.True(InForward($@"{ShapeData}\Fancy\Fancy.nif"));
        Assert.True(InForward($@"{ShapeData}\Fancy\Fancy.tri"));
        Assert.True(InForward($@"{SliderGroups}\Fancy.xml"));
        Assert.False(InForward($@"{SliderSets}\Other.osp"));
        Assert.False(InForward($@"{ShapeData}\Other\Other.nif"));
        Assert.False(InForward($@"{SliderGroups}\Other.xml"));
    }

    [Theory]
    [InlineData("Fancy_0")]
    [InlineData("Fancy_1")]
    public void Gen_weights_builds_the_zero_and_one_meshes(string mesh)
    {
        AddLookFile($@"Meshes\Armor\Fancy\{mesh}.nif", Nif());
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Fancy Set", "Fancy", @"meshes\armor\fancy", "Fancy_", genWeights: false));
        AddLookFile($@"{SliderSets}\Weighted.osp", Osp("Weighted Set", "Weighted", @"meshes\armor\fancy", "Fancy", genWeights: true));
        AddLookFile($@"{ShapeData}\Fancy\Fancy.nif", [1]);
        AddLookFile($@"{ShapeData}\Weighted\Weighted.nif", [2]);

        Copy(new AssetNeed($@"Meshes\Armor\Fancy\{mesh}.nif", "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)));

        Assert.True(InForward($@"{SliderSets}\Weighted.osp"));
        Assert.True(InForward($@"{ShapeData}\Weighted\Weighted.nif"));
        Assert.False(InForward($@"{SliderSets}\Fancy.osp"));
    }

    private const string FancyMesh = @"Meshes\Armor\Fancy\Fancy_go.nif";

    private void AddFancyProject(string mod = "Disabled Looks")
    {
        AddLookFile(FancyMesh, Nif(), mod);
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Fancy Set", "Fancy", @"meshes\armor\fancy", "Fancy_go"), mod);
    }

    private AssetCopyResult CopyFancy(params AssetNeed[] more) =>
        Copy([new AssetNeed(FancyMesh, "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)), .. more]);

    [Fact]
    public void A_mesh_a_disabled_mod_has_is_not_the_bodyslide_output()
    {
        _fileSystem.File.AppendAllText(Path.Combine(Mo2Fixtures.PortableProfile, "modlist.txt"), "\r\n-Old Output\r\n");
        AddFancyProject();
        AddLookFile(FancyMesh, [9], mod: "Old Output");

        CopyFancy();

        Assert.True(InForward(FancyMesh));
        Assert.Contains(_log.Infos, info => info.Contains("rebuild it with BodySlide"));
    }

    [Fact]
    public void A_mesh_in_the_forward_folder_is_not_the_bodyslide_output()
    {
        _fileSystem.File.AppendAllText(Path.Combine(Mo2Fixtures.PortableProfile, "modlist.txt"), "\r\n+OUTPUT ItemLookMerger\r\n");
        AddFancyProject();
        AddLookFile(FancyMesh, [9], mod: "OUTPUT ItemLookMerger");

        CopyFancy();

        Assert.Equal(Nif(), _fileSystem.File.ReadAllBytes(Path.Combine(Forward, FancyMesh)));
        Assert.DoesNotContain(_log.Infos, info => info.Contains("left to the BodySlide output"));
    }

    [Fact]
    public void A_mesh_a_second_look_mod_has_is_not_the_bodyslide_output()
    {
        AddFancyProject();
        AddLookFile(FancyMesh, [9], mod: "Stats Overhaul");
        AddLookFile(@"Textures\Steel\steel.dds", [1], mod: "Stats Overhaul");

        CopyFancy(new AssetNeed(@"Textures\Steel\steel.dds", "ArmorSteelX", Source("Stats.esp", "Stats Overhaul", 3)));

        Assert.True(InForward(FancyMesh));
        Assert.DoesNotContain(_log.Infos, info => info.Contains("left to the BodySlide output"));
    }

    [Fact]
    public void A_mesh_in_overwrite_is_the_bodyslide_output()
    {
        AddFancyProject();
        _fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableFolder, "overwrite", FancyMesh), new MockFileData([9]));

        var result = CopyFancy();

        Assert.False(InForward(FancyMesh));
        Assert.True(InForward($@"{SliderSets}\Fancy.osp"));
        Assert.Equal(1, result.Copied);
        Assert.Contains(_log.Infos, info => info.Contains("left to the BodySlide output") && info.Contains("(Overwrite)"));
    }

    private void AssertWritesOnlyInForward(IReadOnlyCollection<string> before) =>
        Assert.All(_fileSystem.AllFiles.Except(before), file => Assert.StartsWith(Forward, file));

    [Fact]
    public void An_smp_or_bodytri_path_with_dot_dot_is_never_written_outside_the_forward_folder()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Smp.nif", Nif(smp: @"..\..\Other Mod\evil.xml"));
        AddLookFile(@"Meshes\Armor\Fancy\Tri.nif", Nif(tri: @"..\..\..\Other Mod\evil.tri"));
        AddLookFile(@"Other Mod\evil.xml", [1]);
        AddLookFile(@"Other Mod\evil.tri", [1]);
        AddLookFile(@"Meshes\Other Mod\evil.tri", [1]);
        var before = _fileSystem.AllFiles.ToList();
        var source = Source("Look.esp", "Disabled Looks", 5);

        var result = Copy(
            new AssetNeed(@"Meshes\Armor\Fancy\Smp.nif", "ArmorIronCuirass", source),
            new AssetNeed(@"Meshes\Armor\Fancy\Tri.nif", "ArmorIronCuirass", source));

        Assert.Equal(2, result.Copied);
        Assert.Equal(2, result.Missing);
        AssertWritesOnlyInForward(before);
        Assert.Equal(2, _log.Warnings.Count(warning => warning.Contains("leaves the Data folder")));
    }

    [Fact]
    public void A_bodyslide_data_folder_with_dot_dot_copies_nothing_from_outside_the_shape_data_folder()
    {
        AddLookFile(FancyMesh, Nif());
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Fancy Set", @"..\..\..\..\Other Mod", @"meshes\armor\fancy", "Fancy_go"));
        AddLookFile(@"Other Mod\secret.nif", [1]);
        var before = _fileSystem.AllFiles.ToList();

        var result = CopyFancy();

        Assert.Equal(2, result.Copied);
        AssertWritesOnlyInForward(before);
        Assert.DoesNotContain(_fileSystem.AllFiles, file => file.StartsWith(Forward) && file.EndsWith("secret.nif"));
    }

    [Fact]
    public void A_malformed_slider_group_is_warned_about_and_skipped()
    {
        AddFancyProject();
        AddLookFile($@"{SliderGroups}\Broken.xml", "<SliderGroups><Group name=");
        AddLookFile($@"{SliderGroups}\Fancy.xml", """<SliderGroups><Group name="Fancy"><Member name="Fancy Set"/></Group></SliderGroups>""");

        var result = CopyFancy();

        Assert.Equal(3, result.Copied);
        Assert.True(InForward($@"{SliderGroups}\Fancy.xml"));
        Assert.False(InForward($@"{SliderGroups}\Broken.xml"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Broken.xml") && warning.Contains("skipped"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_higher_priority_look_mod_provides_a_bodyslide_file_both_have(bool lowerFirst)
    {
        AddLookFile(@"Meshes\Armor\High\High_go.nif", Nif(), "Better Iron Armor");
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("High Set", "High", @"meshes\armor\high", "High_go"), "Better Iron Armor");
        AddLookFile(@"Meshes\Armor\Low\Low_go.nif", Nif(), "Stats Overhaul");
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Low Set", "Low", @"meshes\armor\low", "Low_go"), "Stats Overhaul");
        var high = new AssetNeed(@"Meshes\Armor\High\High_go.nif", "ArmorSteelX", Source("High.esp", "Better Iron Armor", 9));
        var low = new AssetNeed(@"Meshes\Armor\Low\Low_go.nif", "ArmorSteelY", Source("Low.esp", "Stats Overhaul", 3));

        var result = lowerFirst ? Copy(low, high) : Copy(high, low);

        Assert.Equal(3, result.Copied);
        Assert.Contains("High Set", _fileSystem.File.ReadAllText(Path.Combine(Forward, SliderSets, "Fancy.osp")));
    }

    [Fact]
    public void A_mods_bodyslide_files_are_read_once_for_all_its_plugins()
    {
        AddLookFile(FancyMesh, Nif());
        AddLookFile(@"Meshes\Armor\Fancy\Other.nif", Nif());
        AddLookFile($@"{SliderSets}\Broken.osp", "<SliderSetInfo><SliderSet name=");

        Copy(
            new AssetNeed(FancyMesh, "ArmorIronCuirass", Source("A.esp", "Disabled Looks", 5)),
            new AssetNeed(@"Meshes\Armor\Fancy\Other.nif", "ArmorIronCuirass", Source("B.esp", "Disabled Looks", 5)));

        Assert.Single(_log.Warnings, warning => warning.Contains("Broken.osp"));
    }

    [Fact]
    public void A_mesh_another_enabled_mod_provides_is_left_to_it_but_its_textures_are_still_copied()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", Nif([@"Textures\Armor\Fancy\fancy_d.dds"], smp: @"SKSE\Plugins\hdtSkinnedMeshConfigs\fancy.xml"));
        AddLookFile(@"Textures\Armor\Fancy\fancy_d.dds", [4]);
        AddLookFile(@"SKSE\Plugins\hdtSkinnedMeshConfigs\fancy.xml", "<physics/>");
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Fancy Set", "Fancy", @"meshes\armor\fancy", "Fancy_go"));
        AddLookFile($@"{ShapeData}\Fancy\Fancy.nif", [1]);
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", [9], mod: "Base Fixes");

        var result = Copy(new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)));

        Assert.Equal(new AssetCopyResult(Copied: 4, BaseGameSkipped: 0, FromOtherMods: 0), result);
        Assert.False(InForward(@"Meshes\Armor\Fancy\Fancy_go.nif"));
        Assert.True(InForward(@"Textures\Armor\Fancy\fancy_d.dds"));
        Assert.True(InForward(@"SKSE\Plugins\hdtSkinnedMeshConfigs\fancy.xml"));
        Assert.True(InForward($@"{SliderSets}\Fancy.osp"));
        Assert.True(InForward($@"{ShapeData}\Fancy\Fancy.nif"));
        Assert.Contains(_log.Infos, info => info.Contains("left to the BodySlide output") && info.Contains("\"Base Fixes\""));
    }

    [Fact]
    public void Without_another_mod_providing_it_the_prebuilt_mesh_is_copied_and_the_log_says_it_can_be_rebuilt()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", Nif());
        AddLookFile($@"{SliderSets}\Fancy.osp", Osp("Fancy Set", "Fancy", @"meshes\armor\fancy", "Fancy_go"));

        Copy(new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)));

        Assert.True(InForward(@"Meshes\Armor\Fancy\Fancy_go.nif"));
        Assert.Contains(_log.Infos, info => info.Contains("rebuild it with BodySlide"));
    }

    [Fact]
    public void A_malformed_osp_is_warned_about_and_skipped()
    {
        AddLookFile(@"Meshes\Armor\Fancy\Fancy_go.nif", Nif());
        AddLookFile($@"{SliderSets}\Broken.osp", "<SliderSetInfo><SliderSet name=");

        var result = Copy(new AssetNeed(@"Meshes\Armor\Fancy\Fancy_go.nif", "ArmorIronCuirass", Source("Look.esp", "Disabled Looks", 5)));

        Assert.Equal(1, result.Copied);
        Assert.False(InForward($@"{SliderSets}\Broken.osp"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Broken.osp") && warning.Contains("skipped"));
    }

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
    public void A_base_game_path_a_replacer_mod_overrides_is_skipped_without_a_warning_and_a_mod_only_path_is_warned()
    {
        _fileSystem.AddFile(Path.Combine(Mods, "Base Fixes", @"Textures\Vanilla\replaced_d.dds"), new MockFileData([2]));
        _fileSystem.AddFile(Path.Combine(Mods, "Base Fixes", @"Textures\Armor\Fancy\modonly_d.dds"), new MockFileData([3]));
        var source = Source("Look.esp", "Disabled Looks", 5);

        var result = Copy(
            new AssetNeed(@"Textures\Vanilla\replaced_d.dds", "ArmorIronCuirass", source),
            new AssetNeed(@"Textures\Armor\Fancy\modonly_d.dds", "ArmorIronCuirass", source));

        Assert.Equal(new AssetCopyResult(Copied: 0, BaseGameSkipped: 1, FromOtherMods: 1), result);
        Assert.False(_fileSystem.File.Exists(Path.Combine(Forward, @"Textures\Vanilla\replaced_d.dds")));
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("replaced_d.dds"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("modonly_d.dds") && warning.Contains("\"Base Fixes\""));
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
