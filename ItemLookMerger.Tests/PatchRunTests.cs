using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Merging;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Tests;

public class PatchRunTests
{
    private const string ForwardFolderName = "OUTPUT ItemLookMerger";
    private static readonly string Forward = Path.Combine(Mo2Fixtures.PortableMods, ForwardFolderName);

    /// <summary>
    /// The run's inputs, with the portable fixture profile made to match the load order (so MO2 detection accepts it). The profile's
    /// plugins.txt goes on with <paramref name="linesAfter"/>, which Synthesis trims from its load order but keeps in its full listing.
    /// </summary>
    private static RunInputs Inputs(
        TestPlugins plugins, IReadOnlyList<ISkyrimModGetter> loadOrder, MockFileSystem fileSystem, MergeOptions options, TestLog log, string linesAfter = "")
    {
        if (fileSystem.Directory.Exists(Mo2Fixtures.PortableProfile))
            Mo2Fixtures.MatchPortableProfile(fileSystem, loadOrder.Select(mod => mod.ModKey), linesAfter);
        var enabled = loadOrder.Select(mod => mod.ModKey)
            .Concat(PluginsTxt.Entries(linesAfter).Where(entry => entry.Active && entry.Plugin != plugins.Patch.ModKey).Select(entry => entry.Plugin))
            .Distinct()
            .ToList();
        return new(loadOrder, enabled, plugins.Patch, Mo2Fixtures.PortableData, options,
            new FakeMo2Environment(Path.Combine(Mo2Fixtures.PortableFolder, "ModOrganizer.exe")), fileSystem, log);
    }

    private static MergeOptions Merging(params ModKey[] plugins) => new(plugins, false, false, AllowMerging: true, ForwardFolderName);

    /// <summary>Puts <paramref name="topLines"/> above the fixture's modlist.txt, so those mods have the highest priority.</summary>
    private static void AddModsOnTop(MockFileSystem fileSystem, string topLines) =>
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableProfile, "modlist.txt"), new MockFileData(topLines + Mo2Fixtures.Fixture("modlist.txt")));

    /// <summary>
    /// A portable fixture with the look mod "Relics of Skyrim" (holding Relics of Skyrim.esm and a mesh) on top of the mod list, enabled or
    /// not, and a forward folder from an earlier run.
    /// </summary>
    private static MockFileSystem WithRelicsAndForwardFolder(bool enabled)
    {
        var fileSystem = Mo2Fixtures.Portable();
        AddModsOnTop(fileSystem, (enabled ? "+" : "-") + "Relics of Skyrim\r\n");
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Relics of Skyrim", "Relics of Skyrim.esm"), new MockFileData([]));
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Relics of Skyrim", @"Meshes\Armor\Relic\Relic_go.nif"), new MockFileData([5]));
        fileSystem.AddFile(Path.Combine(Forward, ForwardFolder.MarkerFileName), new MockFileData("marker"));
        fileSystem.AddFile(Path.Combine(Forward, "earlier.nif"), new MockFileData([1]));
        fileSystem.AddFile(Path.Combine(Forward, "CREDITS.md"), new MockFileData("old credits"));
        return fileSystem;
    }

    private static void AssertForwardFolderUntouched(MockFileSystem fileSystem)
    {
        Assert.True(fileSystem.File.Exists(Path.Combine(Forward, "earlier.nif")));
        Assert.Equal("old credits", fileSystem.File.ReadAllText(Path.Combine(Forward, "CREDITS.md")));
        Assert.False(fileSystem.Directory.Exists(Path.Combine(Forward, ForwardFolder.StagingFolderName)));
    }

    [Fact]
    public void With_merging_the_patch_needs_no_look_plugin_and_the_look_mods_assets_replace_the_forward_folder()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Relics of Skyrim.esm", "Relic");
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Stats Overhaul", @"Meshes\Armor\Iron\cuirass_go.nif"), new MockFileData([8]));
        var log = new TestLog();

        var result = PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, Merging(look.ModKey), log));

        Assert.Single(result.Changes);
        Assert.DoesNotContain(
            plugins.Patch.EnumerateMajorRecords().SelectMany(record => record.EnumerateFormLinks().Select(link => link.FormKey).Append(record.FormKey)),
            formKey => formKey.ModKey == look.ModKey);
        Assert.False(fileSystem.File.Exists(Path.Combine(Forward, "earlier.nif")));
        Assert.Equal([5], fileSystem.File.ReadAllBytes(Path.Combine(Forward, @"Meshes\Armor\Relic\Relic_go.nif")));
        Assert.False(fileSystem.File.Exists(Path.Combine(Forward, @"Meshes\Armor\Iron\cuirass_go.nif")));
        Assert.True(fileSystem.File.Exists(Path.Combine(Forward, ForwardFolder.MarkerFileName)));
        Assert.Contains("Relics of Skyrim.esm (Relics of Skyrim)", fileSystem.File.ReadAllText(Path.Combine(Forward, "CREDITS.md")));
        Assert.Contains(log.Infos, line => line.Contains("you can disable the look mods"));
    }

    [Fact]
    public void Without_merging_the_patch_keeps_the_look_plugin_as_a_master_and_writes_no_credits()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Relics of Skyrim.esm", "Relic");
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);
        var log = new TestLog();
        var options = new MergeOptions([look.ModKey, ModKey.Null], ForwardNames: false, VerboseLogging: true, AllowMerging: false, ForwardFolderName);

        var result = PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, options, log));

        Assert.Single(result.Changes);
        // The addon the look plugin added is a copy in the patch, not a link into the look plugin.
        Assert.Equal(plugins.Patch.ModKey, Assert.Single(plugins.Patch.Armors.Single().Armature).FormKey.ModKey);
        Assert.Single(plugins.Patch.ArmorAddons);
        Assert.DoesNotContain(
            fileSystem.AllFiles, file => file.EndsWith("CREDITS.md", StringComparison.OrdinalIgnoreCase) && !file.StartsWith(Forward, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(log.Infos, line => line.StartsWith("Credits:", StringComparison.Ordinal));
        Assert.Contains(log.Infos, line => line.StartsWith("Items changed: 1.", StringComparison.Ordinal));
        AssertForwardFolderUntouched(fileSystem);
        Assert.Contains(log.Warnings, warning => warning.Contains("left as it is"));
    }

    [Fact]
    public void A_listed_plugin_that_is_not_enabled_fails_the_run_before_any_change()
    {
        var plugins = new TestPlugins();
        var fileSystem = WithRelicsAndForwardFolder(enabled: false);

        var ex = Assert.Throws<RunFailedException>(() =>
            PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), fileSystem, Merging(ModKey.FromFileName("Relics of Skyrim.esm")), new TestLog())));

        Assert.Contains("Relics of Skyrim.esm is not enabled", ex.Message);
        Assert.Empty(plugins.Patch.EnumerateMajorRecords());
        AssertForwardFolderUntouched(fileSystem);
    }

    [Fact]
    public void A_listed_plugin_after_the_patch_finds_the_profile_but_fails_the_run_with_its_own_message()
    {
        var plugins = new TestPlugins();
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);
        var relics = ModKey.FromFileName("Relics of Skyrim.esm");
        var log = new TestLog();

        // The user's case: plugins.txt still lists the patch (inactive, its file gone) with the active look plugin below it.
        // Synthesis cut its copy of plugins.txt at the patch, so its load order ends before both.
        var ex = Assert.Throws<RunFailedException>(() =>
            PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), fileSystem, Merging(relics), log, $"{plugins.Patch.ModKey}\r\n*{relics}\r\n")));

        Assert.Contains(log.Infos, line => line.Contains("profile \"My Profile\""));
        Assert.Contains($"Relics of Skyrim.esm is after {plugins.Patch.ModKey} in the load order; Synthesis gives patchers nothing below its patch", ex.Message);
        Assert.Contains($"Move {plugins.Patch.ModKey} below it", ex.Message);
        Assert.DoesNotContain("not enabled", ex.Message);
        AssertForwardFolderUntouched(fileSystem);
    }

    [Fact]
    public void An_empty_list_fails_the_run_before_any_change()
    {
        var plugins = new TestPlugins();
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);

        var ex = Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), fileSystem, Merging(ModKey.Null), new TestLog())));

        Assert.Contains("is empty", ex.Message);
        AssertForwardFolderUntouched(fileSystem);
    }

    [Fact]
    public void A_failed_merge_leaves_the_forward_folder_untouched()
    {
        var plugins = new TestPlugins();
        var broken = TestPlugins.NewMod("Broken.esp");
        broken.MiscItems.Add(new MiscItem(new FormKey(ModKey.FromFileName("Missing.esm"), 0x000800), SkyrimRelease.SkyrimSE));
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);
        AddModsOnTop(fileSystem, "+Broken Mod\r\n");
        fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, "Broken Mod", "Broken.esp"), new MockFileData([]));

        var ex = Assert.Throws<RunFailedException>(() =>
            PatchRun.Run(Inputs(plugins, plugins.LoadOrder(broken), fileSystem, Merging(broken.ModKey), new TestLog())));

        Assert.Contains("Broken.esp", ex.Message);
        AssertForwardFolderUntouched(fileSystem);
    }

    [Fact]
    public void A_wide_type_change_logs_an_error_and_leaves_that_item_alone_without_failing_the_run()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Relics of Skyrim.esm", "Relic");
        plugins.AddSwordLook(look);
        plugins.Stats.Weapons.Single().Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword };
        look.Weapons.Single().Data = new WeaponData { AnimationType = WeaponAnimationType.Bow };
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);
        var log = new TestLog();

        var result = PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, Merging(look.ModKey), log));

        Assert.Empty(plugins.Patch.Weapons);
        Assert.Equal(1, result.TypeSkipped);
        var error = Assert.Single(log.Errors);
        Assert.Contains("IronSword", error);
        Assert.Contains("OneHandSword", error);
        Assert.Contains("Bow", error);
        Assert.Contains(log.Infos, line => line.Contains("Items left unchanged because of a type discrepancy: 1."));
        Assert.Contains(log.Infos, line => line.Contains("Errors: 1"));
    }

    [Fact]
    public void A_failed_asset_copy_keeps_the_previous_output_and_leaves_no_staging_folder()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Relics of Skyrim.esm", "Relic");
        var fileSystem = WithRelicsAndForwardFolder(enabled: true);
        // Locked, as by a running game or editor: reading it fails.
        fileSystem.AddFile(
            Path.Combine(Mo2Fixtures.PortableMods, "Relics of Skyrim", @"Meshes\Armor\Relic\Relic_go.nif"),
            new MockFileData([5]) { AllowedFileShare = FileShare.None });

        var ex = Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(look), fileSystem, Merging(look.ModKey), new TestLog())));

        Assert.Contains("keeps its previous output", ex.Message);
        AssertForwardFolderUntouched(fileSystem);
    }

    [Fact]
    public void The_run_fails_without_mo2_before_touching_the_patch()
    {
        var plugins = new TestPlugins();
        var look = plugins.LookPlugin("Look.esp", "Fancy");
        var inputs = Inputs(plugins, plugins.LoadOrder(look), new MockFileSystem(), Merging(look.ModKey), new TestLog()) with
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
        var options = new MergeOptions([plugins.Stats.ModKey], false, false, AllowMerging: true, " ");

        var ex = Assert.Throws<RunFailedException>(() => PatchRun.Run(Inputs(plugins, plugins.LoadOrder(), Mo2Fixtures.Portable(), options, new TestLog())));
        Assert.Contains("forward folder", ex.Message);
    }
}
