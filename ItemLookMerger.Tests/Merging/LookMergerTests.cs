using ItemLookMerger.Core;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ItemLookMerger.Tests.Merging;

public class LookMergerTests
{
    private readonly TestPlugins _plugins = new();
    private readonly TestLog _log = new();

    /// <summary>Content merging off: the patch may keep the look plugins as masters.</summary>
    private MergeResult Merge(IReadOnlyList<ISkyrimModGetter> loadOrder, bool forwardNames, params LookSource[] sources) =>
        LookMerger.Merge(loadOrder, _plugins.Patch, sources, forwardNames, selfContained: false, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);

    /// <summary>Content merging on: the patch must not need any look plugin.</summary>
    private MergeResult MergeSelfContained(IReadOnlyList<ISkyrimModGetter> loadOrder, params LookSource[] sources) =>
        LookMerger.Merge(loadOrder, _plugins.Patch, sources, forwardNames: false, selfContained: true, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);

    private static string? GroundModel(IArmorGetter armor) => armor.WorldModel?.Male?.Model?.File.GivenPath;

    private IEnumerable<FormKey> PatchFormKeysAndLinks() =>
        _plugins.Patch.EnumerateMajorRecords().SelectMany(record => record.EnumerateFormLinks().Select(link => link.FormKey).Append(record.FormKey));

    [Fact]
    public void Look_fields_are_forwarded_and_the_winners_stats_are_kept()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");

        var result = Merge(_plugins.LoadOrder(look), forwardNames: false, new LookSource(look, "Look Mod"));

        var patched = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal(_plugins.Cuirass.FormKey, patched.FormKey);
        Assert.Equal(40, patched.ArmorRating);
        Assert.Equal(500u, patched.Value);
        Assert.Equal("Iron Armor", patched.Name?.String);
        Assert.Equal(@"Armor\Fancy\Fancy_go.nif", GroundModel(patched));
        // Even without merging the addon the look added is a new record in the patch, not the look plugin's own.
        var addon = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.Equal(addon.FormKey, Assert.Single(patched.Armature).FormKey);
        Assert.Equal(_plugins.Patch.ModKey, addon.FormKey.ModKey);

        var change = Assert.Single(result.Changes);
        Assert.Equal(new[] { look.ModKey }, change.LookPlugins);
        Assert.Equal(_plugins.Stats.ModKey, change.StatsPlugin);
        Assert.Equal(new[] { "WorldModel", "Armature", "ARMA FancyAA [000801:Look.esp]", "TXST FancyTXST [000800:Look.esp]" }, change.Fields);
        Assert.False(change.WholeRecord);
        Assert.Equal(2, result.DuplicatedRecords);
        var assets = result.Assets.Select(asset => asset.Path).ToList();
        Assert.Contains(@"Meshes\Armor\Fancy\Fancy_go.nif", assets);
        Assert.Contains(@"Textures\Armor\Fancy\Fancy_d.dds", assets);
        Assert.DoesNotContain(assets, path => path.Contains(@"\Iron\", StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Assets, asset => Assert.Equal(look.ModKey, asset.Source.ModKey));
    }

    [Fact]
    public void An_items_object_bounds_are_part_of_its_look_and_come_from_the_look_plugin()
    {
        var look = TestPlugins.NewMod("Look.esp");
        var lookSword = look.Weapons.GetOrAddAsOverride(_plugins.Sword);
        lookSword.ObjectBounds.First = new P3Int16(-3, -4, -5);
        lookSword.ObjectBounds.Second = new P3Int16(3, 4, 5);
        lookSword.BasicStats = new WeaponBasicStats { Value = 1 };

        var result = Merge(_plugins.LoadOrder(look), forwardNames: false, new LookSource(look, "Look Mod"));

        var patched = Assert.Single(_plugins.Patch.Weapons);
        Assert.Equal(new P3Int16(-3, -4, -5), patched.ObjectBounds.First);
        Assert.Equal(new P3Int16(3, 4, 5), patched.ObjectBounds.Second);
        Assert.Equal(77u, patched.BasicStats!.Value);
        Assert.Equal(new[] { "ObjectBounds" }, Assert.Single(result.Changes).Fields);
    }

    [Fact]
    public void Records_with_identical_looks_are_skipped()
    {
        var statsOnly = _plugins.StatsOnlyPlugin("StatsOnly.esp");

        var result = Merge(_plugins.LoadOrder(statsOnly), forwardNames: false, new LookSource(statsOnly, null));

        Assert.Empty(_plugins.Patch.Armors);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.IdenticalSkipped);
    }

    [Fact]
    public void Names_are_forwarded_only_when_asked()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");

        var result = Merge(_plugins.LoadOrder(look), forwardNames: true, new LookSource(look, null));

        Assert.Equal("Fancy Armor", Assert.Single(_plugins.Patch.Armors).Name?.String);
        Assert.Contains("Name", Assert.Single(result.Changes).Fields);
    }

    [Fact]
    public void A_name_only_difference_counts_only_with_forward_names_on()
    {
        var renamed = TestPlugins.NewMod("Renamed.esp");
        renamed.Armors.GetOrAddAsOverride(_plugins.Cuirass).Name = "Shiny Iron Armor";

        var result = Merge(_plugins.LoadOrder(renamed), forwardNames: false, new LookSource(renamed, null));

        Assert.Empty(result.Changes);
    }

    [Theory]
    [InlineData(false, false, "Second")]
    [InlineData(false, true, "Second")]
    [InlineData(true, false, "First")]
    [InlineData(true, true, "First")]
    public void The_look_plugin_later_in_the_load_order_wins_whatever_the_list_order_and_mo2_priority(
        bool firstLoadsLast, bool reversedList, string expectedMesh)
    {
        var first = _plugins.LookPlugin("First.esp", "First");
        var second = _plugins.LookPlugin("Second.esp", "Second");
        var loadOrder = firstLoadsLast ? _plugins.LoadOrder(second, first) : _plugins.LoadOrder(first, second);
        // The MO2 priorities point the other way; they only decide between assets.
        LookSource[] sources = [new(first, "First Mod", firstLoadsLast ? 1 : 9), new(second, "Second Mod", firstLoadsLast ? 9 : 1)];
        if (reversedList) Array.Reverse(sources);

        var result = Merge(loadOrder, forwardNames: false, sources);

        Assert.Equal($@"Armor\{expectedMesh}\{expectedMesh}_go.nif", GroundModel(Assert.Single(_plugins.Patch.Armors)));
        Assert.Equal(_plugins.Stats.ModKey, Assert.Single(result.Changes).StatsPlugin);
        Assert.Equal(40, _plugins.Patch.Armors.Single().ArmorRating);
    }

    [Fact]
    public void Without_a_stats_override_the_whole_look_record_is_forwarded()
    {
        var gem = _plugins.Base.MiscItems.AddNew("FancyGem");
        gem.Model = new Model { File = @"Clutter\gem.nif" };
        gem.Value = 10;
        var look = TestPlugins.NewMod("Look.esp");
        var lookGem = look.MiscItems.GetOrAddAsOverride(gem);
        lookGem.Model = new Model { File = @"Clutter\fancy_gem.nif" };
        lookGem.Value = 999;

        var result = Merge(_plugins.LoadOrder(look), forwardNames: false, new LookSource(look, null));

        var patched = Assert.Single(_plugins.Patch.MiscItems);
        Assert.Equal(@"Clutter\fancy_gem.nif", patched.Model?.File.GivenPath);
        Assert.Equal(999u, patched.Value);
        var change = Assert.Single(result.Changes);
        Assert.True(change.WholeRecord);
        Assert.Equal(_plugins.Base.ModKey, change.StatsPlugin);
        Assert.Equal(new[] { look.ModKey }, change.LookPlugins);
    }

    [Fact]
    public void A_look_plugin_with_unresolvable_overrides_fails_the_run_listing_all()
    {
        var look = TestPlugins.NewMod("Broken.esp");
        look.MiscItems.Add(new MiscItem(new FormKey(ModKey.FromFileName("Missing.esm"), 0x000800), SkyrimRelease.SkyrimSE) { EditorID = "Orphan" });
        look.MiscItems.Add(new MiscItem(new FormKey(_plugins.Base.ModKey, 0x000ABC), SkyrimRelease.SkyrimSE) { EditorID = "Ghost" });

        var ex = Assert.Throws<RunFailedException>(() =>
            Merge(_plugins.LoadOrder(look), forwardNames: false, new LookSource(look, "Broken Mod")));

        Assert.Contains("Broken.esp: MISC 000800:Missing.esm", ex.Message);
        Assert.Contains("Broken.esp: MISC 000ABC:Base.esm", ex.Message);
    }

    [Fact]
    public void A_self_contained_patch_copies_the_look_records_it_uses_and_has_no_look_plugin_as_a_master()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        var firstPerson = _plugins.AddSwordLook(look);
        look.Keywords.AddNew("LookOnlyKeyword");

        var result = MergeSelfContained(_plugins.LoadOrder(look), new LookSource(look, "Look Mod"));

        var patched = _plugins.Patch.Armors.Single();
        Assert.Equal(40, patched.ArmorRating);
        var addonKey = Assert.Single(patched.Armature).FormKey;
        Assert.Equal(_plugins.Patch.ModKey, addonKey.ModKey);
        var addon = _plugins.Patch.ArmorAddons.Single(record => record.FormKey == addonKey);
        Assert.Equal(@"Armor\Fancy\Fancy_m.nif", addon.WorldModel?.Male?.File.GivenPath);
        // Followed through the copied armor addon's own look link.
        var textureSetKey = Assert.Single(addon.WorldModel!.Male!.AlternateTextures!).NewTexture.FormKey;
        Assert.Equal(_plugins.Patch.ModKey, textureSetKey.ModKey);
        Assert.Single(_plugins.Patch.TextureSets);

        var sword = _plugins.Patch.Weapons.Single();
        Assert.Equal(77u, sword.BasicStats?.Value);
        Assert.Equal(@"Weapons\Fancy\sword.nif", sword.Model?.File.GivenPath);
        Assert.Equal(_plugins.Patch.ModKey, sword.FirstPersonModel.FormKey.ModKey);
        Assert.Equal(firstPerson.EditorID + "_ILM", _plugins.Patch.Statics.Single().EditorID);

        Assert.Equal(3, result.DuplicatedRecords);
        Assert.DoesNotContain(PatchFormKeysAndLinks(), formKey => formKey.ModKey == look.ModKey);
        // The keyword nothing visual uses is not copied; the log says disabling the plugin drops it.
        Assert.Empty(_plugins.Patch.Keywords);
        Assert.Contains(_log.Infos, line => line.Contains("1 Keyword"));
        Assert.Empty(_log.Warnings);

        var assets = result.Assets.Select(asset => asset.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"Meshes\Armor\Fancy\Fancy_m.nif", assets);
        Assert.Contains(@"Meshes\Weapons\Fancy\sword_1st.nif", assets);
    }

    [Fact]
    public void In_a_whole_record_non_visual_links_to_a_look_plugin_fall_back_to_the_stats_side_with_a_warning()
    {
        var baseKeyword = _plugins.Base.Keywords.AddNew("VendorItemGem");
        var gem = _plugins.Base.MiscItems.AddNew("FancyGem");
        gem.Model = new Model { File = @"Clutter\gem.nif" };
        gem.Keywords = [baseKeyword.ToLink()];
        var look = TestPlugins.NewMod("Look.esp");
        var lookKeyword = look.Keywords.AddNew("LookGemKeyword");
        var lookGem = look.MiscItems.GetOrAddAsOverride(gem);
        lookGem.Model = new Model { File = @"Clutter\fancy_gem.nif" };
        lookGem.Keywords = [new FormLink<IKeywordGetter>(lookKeyword.FormKey)];
        lookGem.Value = 999;

        var result = MergeSelfContained(_plugins.LoadOrder(look), new LookSource(look, null));

        var patched = Assert.Single(_plugins.Patch.MiscItems);
        Assert.True(Assert.Single(result.Changes).WholeRecord);
        Assert.Equal(@"Clutter\fancy_gem.nif", patched.Model?.File.GivenPath);
        Assert.Equal(999u, patched.Value);
        Assert.Equal(baseKeyword.FormKey, Assert.Single(patched.Keywords!).FormKey);
        Assert.Empty(_plugins.Patch.Keywords);
        Assert.Contains(_log.Warnings, warning => warning.Contains("FancyGem") && warning.Contains("Keywords") && warning.Contains("takes the stats side's value"));
        Assert.DoesNotContain(PatchFormKeysAndLinks(), formKey => formKey.ModKey == look.ModKey);
    }

    [Fact]
    public void A_non_visual_link_of_a_copied_look_record_is_cleared_with_a_warning()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        var lookRace = new FormKey(look.ModKey, 0x000DEA);
        look.ArmorAddons.Single().AdditionalRaces.Add(new FormLink<IRaceGetter>(lookRace));

        var result = MergeSelfContained(_plugins.LoadOrder(look), new LookSource(look, "Look Mod"));

        Assert.Single(result.Changes);
        Assert.Contains(_log.Warnings, warning => warning.Contains(lookRace.ToString()) && warning.Contains("AdditionalRaces") && warning.Contains("cleared"));
        Assert.DoesNotContain(PatchFormKeysAndLinks(), formKey => formKey.ModKey == look.ModKey);
    }

    [Fact]
    public void A_link_to_a_look_plugin_the_run_cannot_remove_fails_a_self_contained_patch()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        var lookKeyword = look.Keywords.AddNew("LookKeyword");
        // An earlier patcher of the group already linked a record to the look plugin.
        _plugins.Patch.Keywords.AddNew("EarlierPatcherKeyword");
        _plugins.Patch.FormLists.AddNew("EarlierPatcherList").Items.Add(lookKeyword.ToLink());

        var ex = Assert.Throws<RunFailedException>(() => MergeSelfContained(_plugins.LoadOrder(look), new LookSource(look, "Look Mod")));

        Assert.Contains("must not need a look plugin as a master", ex.Message);
        Assert.Contains($"EarlierPatcherList [{_plugins.Patch.FormLists.Single().FormKey}] -> {lookKeyword.FormKey}", ex.Message);
    }

    [Fact]
    public void A_new_armor_addon_with_a_weight_mesh_brings_the_other_weight_too()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        look.ArmorAddons.Single().WorldModel!.Male!.File.GivenPath = @"Armor\Fancy\Fancy_1.nif";

        var result = MergeSelfContained(_plugins.LoadOrder(look), new LookSource(look, "Look Mod"));

        var assets = result.Assets.Select(asset => asset.Path).ToList();
        Assert.Contains(@"Meshes\Armor\Fancy\Fancy_1.nif", assets);
        Assert.Contains(@"Meshes\Armor\Fancy\Fancy_0.nif", assets);
    }

    [Fact]
    public void An_earlier_patchers_override_is_the_stats_side_and_is_never_replaced_whole()
    {
        var gem = _plugins.Base.MiscItems.AddNew("FancyGem");
        gem.Model = new Model { File = @"Clutter\gem.nif" };
        gem.Value = 10;
        _plugins.Patch.MiscItems.GetOrAddAsOverride(gem).Value = 5;
        var look = TestPlugins.NewMod("Look.esp");
        var lookGem = look.MiscItems.GetOrAddAsOverride(gem);
        lookGem.Model = new Model { File = @"Clutter\fancy_gem.nif" };
        lookGem.Value = 999;

        var result = MergeSelfContained(_plugins.LoadOrder(look), new LookSource(look, null));

        var patched = Assert.Single(_plugins.Patch.MiscItems);
        Assert.Equal(@"Clutter\fancy_gem.nif", patched.Model?.File.GivenPath);
        Assert.Equal(5u, patched.Value);
        Assert.False(Assert.Single(result.Changes).WholeRecord);
    }

    [Fact]
    public void Models_differing_only_in_their_hash_data_count_as_identical()
    {
        _plugins.Sword.Model!.Data = new byte[] { 1, 2, 3 };
        var look = TestPlugins.NewMod("Resaved.esp");
        var lookSword = look.Weapons.GetOrAddAsOverride(_plugins.Sword);
        lookSword.Model = new Model { File = @"WEAPONS\IRON\SWORD.NIF", Data = new byte[] { 9, 9 } };

        var result = Merge(_plugins.LoadOrder(look), forwardNames: false, new LookSource(look, null));

        Assert.Empty(result.Changes);
        Assert.Equal(1, result.IdenticalSkipped);
    }

    [Fact]
    public void A_look_plugin_read_as_a_binary_overlay_is_made_self_contained_like_an_in_memory_one()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        var stream = new MemoryStream();
        look.WriteToBinary(stream, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(_plugins.LoadOrder().Select(mod => mod.ModKey)),
        });
        stream.Position = 0;
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(stream, SkyrimRelease.SkyrimSE, look.ModKey);

        var result = MergeSelfContained([_plugins.Base, _plugins.Stats, overlay], new LookSource(overlay, "Look Mod"));

        var patched = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal(40, patched.ArmorRating);
        Assert.Equal(@"Armor\Fancy\Fancy_go.nif", GroundModel(patched));
        Assert.Equal(_plugins.Patch.ModKey, Assert.Single(patched.Armature).FormKey.ModKey);
        Assert.Equal(2, result.DuplicatedRecords);
        Assert.DoesNotContain(PatchFormKeysAndLinks(), formKey => formKey.ModKey == look.ModKey);
        Assert.Contains(@"Textures\Armor\Fancy\Fancy_d.dds", result.Assets.Select(asset => asset.Path));
    }
}
