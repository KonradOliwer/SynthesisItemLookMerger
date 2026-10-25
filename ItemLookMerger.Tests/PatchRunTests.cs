using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Tests.Merging;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Tests;

public class PatchRunTests
{
    private sealed class NoOpener : IPluginOpener
    {
        public ISkyrimModGetter Open(string path) => throw new InvalidOperationException("No plugin should be opened.");
    }

    private const string OutputPath = @"C:\Synthesis\Output\ItemLookMerger.esp";

    /// <summary>The run's inputs, with the portable fixture profile made to match the load order (so MO2 detection accepts it).</summary>
    private static RunInputs Inputs(TestPlugins plugins, IReadOnlyList<ISkyrimModGetter> loadOrder, MockFileSystem fileSystem, MergeOptions options, TestLog log)
    {
        if (fileSystem.Directory.Exists(Mo2Fixtures.PortableProfile))
            Mo2Fixtures.MatchPortableProfile(fileSystem, loadOrder.Select(mod => mod.ModKey));
        return new(loadOrder, plugins.Patch, OutputPath, Mo2Fixtures.PortableData, options,
            new FakeMo2Environment(Path.Combine(Mo2Fixtures.PortableFolder, "ModOrganizer.exe")), fileSystem, new NoOpener(), log);
    }

    [Fact]
    public void A_failed_run_leaves_the_forward_folder_untouched()
    {
        var plugins = new TestPlugins();
        var broken = TestPlugins.NewMod("Broken.esp");
        broken.MiscItems.Add(new MiscItem(new FormKey(ModKey.FromFileName("Missing.esm"), 0x000800), SkyrimRelease.SkyrimSE));
        var fileSystem = Mo2Fixtures.Portable();
        var forward = Path.Combine(Mo2Fixtures.PortableMods, "OUTPUT ItemLookMerger");
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Better Iron Armor", "Broken.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(forward, ".itemlookmerger"), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(forward, @"Meshes\in_use.nif"), new MockFileData([1]));
        fileSystem.AddFile(Path.Combine(forward, "CREDITS.md"), new MockFileData("old credits"));
        var options = new MergeOptions([broken.ModKey], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");

        Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(broken), fileSystem, options, new TestLog())));

        Assert.True(fileSystem.File.Exists(Path.Combine(forward, @"Meshes\in_use.nif")));
        Assert.Equal("old credits", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
    }

    [Fact]
    public void With_merging_off_a_leftover_forward_folder_is_reported_and_left_alone()
    {
        var plugins = new TestPlugins();
        var fileSystem = Mo2Fixtures.Portable();
        var forward = Path.Combine(Mo2Fixtures.PortableMods, "OUTPUT ItemLookMerger");
        fileSystem.AddFile(Path.Combine(forward, ".itemlookmerger"), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(forward, "old.nif"), new MockFileData([1]));
        var log = new TestLog();

        PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), fileSystem, new MergeOptions([], false, false, AllowMerging: false, "OUTPUT ItemLookMerger"), log));

        Assert.True(fileSystem.File.Exists(Path.Combine(forward, "old.nif")));
        Assert.Contains(log.Warnings, warning => warning.Contains("left as it is"));
    }

    [Fact]
    public void The_run_fails_without_mo2_before_touching_the_patch()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Look.esp", "Fancy");
        var options = new MergeOptions([look.ModKey], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");
        var inputs = Inputs(plugins, plugins.LoadOrder(look), new MockFileSystem(), options, new TestLog()) with
        {
            Mo2Environment = new FakeMo2Environment(null),
        };

        Assert.Throws<RunFailedException>(() => PatchRun.Run(inputs));
        Assert.Empty(plugins.Patch.Armors);
    }

    [Fact]
    public void An_empty_forward_folder_name_fails_the_run_while_merging()
    {
        var plugins = new TestPlugins();
        var options = new MergeOptions([], false, false, AllowMerging: true, " ");

        var ex = Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), Mo2Fixtures.Portable(), options, new TestLog())));
        Assert.Contains("forward folder", ex.Message);
    }

    [Fact]
    public void With_merging_an_enabled_look_mods_assets_are_copied_into_the_emptied_forward_folder()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Look.esp", "Fancy");
        var fileSystem = Mo2Fixtures.Portable();
        var mods = Mo2Fixtures.PortableMods;
        var forward = Path.Combine(mods, "OUTPUT ItemLookMerger");
        fileSystem.AddFile(Path.Combine(mods, "Better Iron Armor", "Look.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(mods, "Better Iron Armor", @"Meshes\Armor\Fancy\Fancy_go.nif"), new MockFileData([7]));
        fileSystem.AddFile(Path.Combine(mods, "Stats Overhaul", @"Meshes\Armor\Iron\cuirass_go.nif"), new MockFileData([8]));
        fileSystem.AddFile(Path.Combine(forward, ".itemlookmerger"), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(forward, "stale.dds"), new MockFileData([0]));
        var options = new MergeOptions([look.ModKey], ForwardNames: false, VerboseLogging: false, AllowMerging: true, "OUTPUT ItemLookMerger");

        PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, options, new TestLog()));

        Assert.False(fileSystem.File.Exists(Path.Combine(forward, "stale.dds")));
        Assert.True(fileSystem.File.Exists(Path.Combine(forward, ".itemlookmerger")));
        Assert.Equal([7], fileSystem.File.ReadAllBytes(Path.Combine(forward, @"Meshes\Armor\Fancy\Fancy_go.nif")));
        Assert.False(fileSystem.File.Exists(Path.Combine(forward, @"Meshes\Armor\Iron\cuirass_go.nif")));
        Assert.Contains("Look.esp (Better Iron Armor)", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
    }

    [Fact]
    public void Without_merging_the_credits_go_next_to_the_patch()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Look.esp", "Fancy");
        var fileSystem = Mo2Fixtures.Portable();
        var log = new TestLog();
        var options = new MergeOptions([look.ModKey, ModKey.Null], ForwardNames: false, VerboseLogging: true, AllowMerging: false, null);

        var result = PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, options, log));

        Assert.Single(result.Changes);
        var credits = fileSystem.File.ReadAllText(@"C:\Synthesis\Output\CREDITS.md");
        Assert.Contains("| ArmorIronCuirass | Iron Armor |", credits);
        Assert.Contains(log.Infos, line => line.StartsWith("Items changed: 1."));
        Assert.Empty(log.Errors);
    }
}
