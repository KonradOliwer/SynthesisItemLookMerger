using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Tests.Merging;

public class SourceResolverTests
{
    private readonly TestPlugins _plugins = new();
    private readonly TestLog _log = new();
    private readonly MockFileSystem _fileSystem = Mo2Fixtures.Portable();

    private IReadOnlyList<LookSource> Resolve(IReadOnlyList<ISkyrimModGetter> loadOrder, params ModKey[] plugins) =>
        SourceResolver.Resolve(plugins, loadOrder, _plugins.Patch.ModKey, Mo2Instance.Load(_fileSystem, Mo2Fixtures.PortableFolder), _log);

    private void AddPluginFile(string mod, string plugin) =>
        _fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableMods, mod, plugin), new MockFileData([]));

    [Fact]
    public void An_enabled_plugin_is_used_as_loaded_with_the_enabled_mod_mo2_serves_it_from()
    {
        // A disabled mod with a copy of the file doesn't count; the enabled one serves it.
        AddPluginFile("Disabled Looks", "Stats.esp");

        var source = Assert.Single(Resolve(_plugins.LoadOrder(), _plugins.Stats.ModKey));

        Assert.Same(_plugins.Stats, source.Mod);
        Assert.Equal("Stats Overhaul", source.ModName);
        Assert.Equal(1, source.ModPriority);
    }

    [Fact]
    public void Of_two_enabled_mods_with_the_plugin_the_higher_priority_one_serves_it()
    {
        AddPluginFile("Better Iron Armor", "Stats.esp");

        var source = Assert.Single(Resolve(_plugins.LoadOrder(), _plugins.Stats.ModKey));

        // Better Iron Armor is above Stats Overhaul in modlist.txt, so it wins the file.
        Assert.Equal("Better Iron Armor", source.ModName);
    }

    [Fact]
    public void A_plugin_only_a_disabled_mod_has_comes_from_the_data_folder()
    {
        var look = _plugins.LookPlugin("Looks.esp", "Fancy");

        // The fixture's Looks.esp sits in the disabled mod "Disabled Looks"; the load order's copy can only be the Data folder's.
        var source = Assert.Single(Resolve(_plugins.LoadOrder(look), look.ModKey));

        Assert.Null(source.ModName);
        Assert.Equal(-1, source.ModPriority);
    }

    [Fact]
    public void Plugins_that_are_not_enabled_fail_the_run_in_one_error()
    {
        var ex = Assert.Throws<RunFailedException>(() =>
            Resolve(_plugins.LoadOrder(), _plugins.Stats.ModKey, ModKey.FromFileName("Looks.esp"), ModKey.FromFileName("Relics of Skyrim.esm")));

        Assert.Contains("Looks.esp, Relics of Skyrim.esm are not enabled", ex.Message);
        Assert.Contains("you can disable them again after the run", ex.Message);
    }

    [Fact]
    public void An_empty_list_fails_the_run()
    {
        var ex = Assert.Throws<RunFailedException>(() => Resolve(_plugins.LoadOrder(), ModKey.Null));
        Assert.Contains("\"Plugins to forward\" is empty", ex.Message);
    }

    [Fact]
    public void The_patch_itself_fails_the_run()
    {
        var ex = Assert.Throws<RunFailedException>(() => Resolve(_plugins.LoadOrder(), _plugins.Patch.ModKey, _plugins.Stats.ModKey));
        Assert.Contains("own output", ex.Message);
    }

    [Fact]
    public void Duplicates_and_unset_entries_are_ignored()
    {
        var sources = Resolve(_plugins.LoadOrder(), _plugins.Stats.ModKey, ModKey.Null, _plugins.Stats.ModKey);
        Assert.Single(sources);
    }
}
