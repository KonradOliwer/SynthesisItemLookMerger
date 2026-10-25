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

    /// <summary>Puts <paramref name="topLines"/> above the fixture's modlist.txt, so those mods have the highest priority.</summary>
    private static void AddModsOnTop(MockFileSystem fileSystem, string topLines) =>
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableProfile, "modlist.txt"), new MockFileData(topLines + Mo2Fixtures.Fixture("modlist.txt")));

    [Fact]
    public void A_failed_run_leaves_the_forward_folder_untouched()
    {
        var plugins = new TestPlugins();
        var broken = TestPlugins.NewMod("Broken.esp");
        broken.MiscItems.Add(new MiscItem(new FormKey(ModKey.FromFileName("Missing.esm"), 0x000800), SkyrimRelease.SkyrimSE));
        var fileSystem = Mo2Fixtures.Portable();
        var forward = Path.Combine(Mo2Fixtures.PortableMods, "OUTPUT ItemLookMerger");
        AddModsOnTop(fileSystem, "+Broken Mod\r\n");
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Broken Mod", "Broken.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(forward, ".itemlookmerger"), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(forward, @"Meshes\in_use.nif"), new MockFileData([1]));
        fileSystem.AddFile(Path.Combine(forward, "CREDITS.md"), new MockFileData("old credits"));
        var options = new MergeOptions(["Broken Mod"], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");

        Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(broken), fileSystem, options, new TestLog())));

        Assert.True(fileSystem.File.Exists(Path.Combine(forward, @"Meshes\in_use.nif")));
        Assert.Equal("old credits", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
    }

    private sealed class SingleOpener(ISkyrimModGetter plugin) : IPluginOpener
    {
        public ISkyrimModGetter Open(string path) => plugin;
    }

    /// <summary>A portable fixture whose profile has the look mod "Relics of Skyrim" disabled, and a forward folder from an earlier run.</summary>
    private static MockFileSystem WithDisabledRelicsAndForwardFolder(out string forward)
    {
        var fileSystem = Mo2Fixtures.Portable();
        AddModsOnTop(fileSystem, "-Relics of Skyrim\r\n");
        forward = Path.Combine(Mo2Fixtures.PortableMods, "OUTPUT ItemLookMerger");
        fileSystem.AddFile(Path.Combine(forward, ".itemlookmerger"), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(forward, "earlier.nif"), new MockFileData([1]));
        fileSystem.AddFile(Path.Combine(forward, "CREDITS.md"), new MockFileData("old credits"));
        return fileSystem;
    }

    [Fact]
    public void A_look_plugin_in_a_disabled_mod_is_merged_in()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Relics of Skyrim.esm", "Relic");
        var fileSystem = WithDisabledRelicsAndForwardFolder(out var forward);
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Relics of Skyrim", "Relics of Skyrim.esm"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Relics of Skyrim", @"Meshes\Armor\Relic\Relic_go.nif"), new MockFileData([5]));
        var options = new MergeOptions(["Relics of Skyrim"], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");
        var inputs = Inputs(plugins, plugins.LoadOrder(), fileSystem, options, new TestLog()) with { PluginOpener = new SingleOpener(look) };

        var result = PatchRun.Run(inputs);

        Assert.Single(result.Changes);
        Assert.False(fileSystem.File.Exists(Path.Combine(forward, "earlier.nif")));
        Assert.Equal([5], fileSystem.File.ReadAllBytes(Path.Combine(forward, @"Meshes\Armor\Relic\Relic_go.nif")));
        Assert.Contains("Relics of Skyrim.esm (Relics of Skyrim)", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
    }

    [Fact]
    public void An_unknown_mod_name_fails_the_run_before_any_change()
    {
        var plugins = new TestPlugins();
        var fileSystem = WithDisabledRelicsAndForwardFolder(out var forward);
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Relics of Skyrim", "Relics of Skyrim.esm"), new MockFileData([]));
        var options = new MergeOptions(["Relics of Skyrm"], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");

        var ex = Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), fileSystem, options, new TestLog())));

        Assert.Contains("Did you mean \"Relics of Skyrim\"?", ex.Message);
        Assert.Empty(plugins.Patch.Armors);
        Assert.True(fileSystem.File.Exists(Path.Combine(forward, "earlier.nif")));
        Assert.Equal("old credits", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
    }

    [Fact]
    public void Without_a_look_source_the_forward_folder_and_credits_are_left_alone()
    {
        var plugins = new TestPlugins();
        var fileSystem = WithDisabledRelicsAndForwardFolder(out var forward);
        var log = new TestLog();
        var options = new MergeOptions(["", "  ", null], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");

        var result = PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), fileSystem, options, log));

        Assert.Empty(result.Changes);
        Assert.True(fileSystem.File.Exists(Path.Combine(forward, "earlier.nif")));
        Assert.Equal("old credits", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
        Assert.Contains(log.Warnings, warning => warning.Contains("nothing to forward"));
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
        var options = new MergeOptions(["Fancy Looks"], false, false, AllowMerging: true, "OUTPUT ItemLookMerger");
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
        AddModsOnTop(fileSystem, "+Fancy Looks\r\n");
        fileSystem.AddFile(Path.Combine(mods, "Fancy Looks", "Look.esp"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(mods, "Fancy Looks", @"Meshes\Armor\Fancy\Fancy_go.nif"), new MockFileData([7]));
        fileSystem.AddFile(Path.Combine(mods, "Stats Overhaul", @"Meshes\Armor\Iron\cuirass_go.nif"), new MockFileData([8]));
        fileSystem.AddFile(Path.Combine(forward, ".itemlookmerger"), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(forward, "stale.dds"), new MockFileData([0]));
        var options = new MergeOptions(["Fancy Looks"], ForwardNames: false, VerboseLogging: false, AllowMerging: true, "OUTPUT ItemLookMerger");

        PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, options, new TestLog()));

        Assert.False(fileSystem.File.Exists(Path.Combine(forward, "stale.dds")));
        Assert.True(fileSystem.File.Exists(Path.Combine(forward, ".itemlookmerger")));
        Assert.Equal([7], fileSystem.File.ReadAllBytes(Path.Combine(forward, @"Meshes\Armor\Fancy\Fancy_go.nif")));
        Assert.False(fileSystem.File.Exists(Path.Combine(forward, @"Meshes\Armor\Iron\cuirass_go.nif")));
        Assert.Contains("Look.esp (Fancy Looks)", fileSystem.File.ReadAllText(Path.Combine(forward, "CREDITS.md")));
    }

    [Fact]
    public void Without_merging_the_credits_go_next_to_the_patch()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Look.esp", "Fancy");
        var fileSystem = Mo2Fixtures.Portable();
        AddModsOnTop(fileSystem, "+Fancy Looks\r\n");
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Fancy Looks", "Look.esp"), new MockFileData([]));
        var log = new TestLog();
        var options = new MergeOptions(["Fancy Looks", ""], ForwardNames: false, VerboseLogging: true, AllowMerging: false, null);

        var result = PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, options, log));

        Assert.Single(result.Changes);
        var credits = fileSystem.File.ReadAllText(@"C:\Synthesis\Output\CREDITS.md");
        Assert.Contains("| ArmorIronCuirass | Iron Armor |", credits);
        Assert.Contains(log.Infos, line => line.StartsWith("Items changed: 1."));
        Assert.Empty(log.Errors);
    }
}
