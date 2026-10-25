using System.IO.Abstractions.TestingHelpers;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Tests.Merging;

public class SourceResolverTests
{
    /// <summary>Hands out prepared plugins by file name instead of reading files.</summary>
    private sealed class FakeOpener(params SkyrimMod[] plugins) : IPluginOpener
    {
        public List<string> Opened { get; } = [];

        public ISkyrimModGetter Open(string path)
        {
            Opened.Add(path);
            return plugins.Single(plugin => plugin.ModKey.FileName == Path.GetFileName(path));
        }
    }

    private readonly TestPlugins _plugins = new();
    private readonly TestLog _log = new();
    private readonly MockFileSystem _fileSystem = Mo2Fixtures.Portable();

    private IReadOnlyList<LookSource> Resolve(IPluginOpener opener, bool allowMerging, params string[] plugins) =>
        SourceResolver.Resolve(
            plugins.Select(name => ModKey.FromFileName(name)).ToList(),
            _plugins.LoadOrder(),
            _plugins.Patch.ModKey,
            Mo2Instance.Load(_fileSystem, Mo2Fixtures.PortableFolder),
            opener,
            allowMerging,
            _log);

    private void AddPluginFile(string mod, string plugin) =>
        _fileSystem.AddFile(Path.Combine(Mo2Fixtures.PortableFolder, "mods", mod, plugin), new MockFileData([]));

    /// <summary>Fails like Mutagen does on a truncated plugin file.</summary>
    private sealed class CorruptOpener : IPluginOpener
    {
        public ISkyrimModGetter Open(string path) => throw new EndOfStreamException("Unexpected end of file.");
    }

    [Fact]
    public void A_corrupt_plugin_file_is_skipped_with_an_error()
    {
        AddPluginFile("Disabled Looks", "Look.esp");

        Assert.Empty(Resolve(new CorruptOpener(), allowMerging: true, "Look.esp"));
        Assert.Contains("cannot be read", Assert.Single(_log.Errors));
    }

    [Fact]
    public void A_disabled_plugin_in_overwrite_is_found_there_with_the_highest_priority()
    {
        var look = _plugins.LookPlugin("Generated.esp", "Fancy");
        _fileSystem.AddFile(@"C:\MO2Portable\overwrite\Generated.esp", new MockFileData([]));
        var opener = new FakeOpener(look);

        var source = Assert.Single(Resolve(opener, allowMerging: true, "Generated.esp"));

        Assert.Equal(Mo2Instance.OverwriteName, source.ModName);
        Assert.Equal(@"C:\MO2Portable\overwrite\Generated.esp", Assert.Single(opener.Opened));
        // Above the four real mods of the fixture (priorities 0 to 3).
        Assert.Equal(4, source.ModPriority);
    }

    [Fact]
    public void A_plugin_in_the_load_order_is_used_as_loaded()
    {
        AddPluginFile("Stats Overhaul", "Stats.esp");

        var source = Assert.Single(Resolve(new FakeOpener(), allowMerging: true, "Stats.esp"));

        Assert.True(source.InLoadOrder);
        Assert.Same(_plugins.Stats, source.Mod);
        Assert.Equal("Stats Overhaul", source.ModName);
    }

    [Fact]
    public void A_disabled_plugin_is_read_from_its_mo2_mod()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        look.ModHeader.MasterReferences.Add(new MasterReference { Master = _plugins.Base.ModKey });
        AddPluginFile("Disabled Looks", "Look.esp");
        var opener = new FakeOpener(look);

        var source = Assert.Single(Resolve(opener, allowMerging: true, "Look.esp"));

        Assert.False(source.InLoadOrder);
        Assert.Equal("Disabled Looks", source.ModName);
        Assert.Equal(@"C:\MO2Portable\mods\Disabled Looks\Look.esp", Assert.Single(opener.Opened));
    }

    [Fact]
    public void A_plugin_with_masters_outside_the_load_order_is_skipped_with_an_error()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        look.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromFileName("Missing.esm") });
        AddPluginFile("Disabled Looks", "Look.esp");

        Assert.Empty(Resolve(new FakeOpener(look), allowMerging: true, "Look.esp"));
        Assert.Contains("Missing.esm", Assert.Single(_log.Errors));
    }

    [Fact]
    public void Without_merging_a_plugin_outside_the_load_order_is_skipped()
    {
        AddPluginFile("Disabled Looks", "Look.esp");
        var opener = new FakeOpener();

        Assert.Empty(Resolve(opener, allowMerging: false, "Look.esp"));
        Assert.Empty(opener.Opened);
        Assert.Single(_log.Errors);
    }
}
