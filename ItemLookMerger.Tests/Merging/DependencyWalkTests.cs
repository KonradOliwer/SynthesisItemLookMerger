using ItemLookMerger.Core;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Allocators;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ItemLookMerger.Tests.Merging;

/// <summary>The dependency walk: the forms a forwarded look depends on, each carried as a new record in the patch.</summary>
public class DependencyWalkTests
{
    private readonly TestPlugins _plugins = new();
    private readonly TestLog _log = new();

    private MergeResult Merge(bool selfContained, IReadOnlyList<ISkyrimModGetter> loadOrder, params LookSource[] sources) =>
        LookMerger.Merge(loadOrder, _plugins.Patch, sources, forwardNames: false, selfContained, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);

    /// <summary>A vanilla texture set that the iron armor addon uses as an alternate texture.</summary>
    private TextureSet AddIronTextureSet()
    {
        var textureSet = _plugins.Base.TextureSets.AddNew("IronTXST");
        textureSet.Diffuse = @"Armor\Iron\iron_d.dds";
        _plugins.BaseAddon.WorldModel!.Male!.AlternateTextures = [new AlternateTexture { Name = "Body", Index = 0, NewTexture = textureSet.ToLink() }];
        return textureSet;
    }

    /// <summary>A look plugin that overrides the iron cuirass and its armor addon exactly as they are: only a way for the walk in.</summary>
    private SkyrimMod Retexture(string fileName)
    {
        var look = TestPlugins.NewMod(fileName);
        look.Armors.GetOrAddAsOverride(_plugins.Cuirass);
        look.ArmorAddons.GetOrAddAsOverride(_plugins.BaseAddon);
        return look;
    }

    private static GenderedItem<Model?> Male(string file) => new(new Model { File = file }, null);

    [Fact]
    public void A_shared_armor_addon_gets_a_private_copy_and_the_original_stays_untouched()
    {
        var race = new FormKey(_plugins.Base.ModKey, 0x000900);
        _plugins.Stats.ArmorAddons.GetOrAddAsOverride(_plugins.BaseAddon).Race.SetTo(race);
        var generic = _plugins.Base.Armors.AddNew("GenericIronArmor");
        generic.Armature.Add(_plugins.BaseAddon.ToLink());
        var look = Retexture("Retexture.esp");
        var lookAddon = look.ArmorAddons.Single();
        lookAddon.WorldModel = Male(@"Armor\Better\cuirass_1.nif");
        lookAddon.Race.SetTo(new FormKey(_plugins.Base.ModKey, 0x000901));

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, "Retexture Mod"));

        var addon = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.NotEqual(_plugins.BaseAddon.FormKey, addon.FormKey);
        Assert.Equal(_plugins.Patch.ModKey, addon.FormKey.ModKey);
        Assert.Equal("IronCuirassAA_ILM", addon.EditorID);
        Assert.Equal(@"Armor\Better\cuirass_1.nif", addon.WorldModel?.Male?.File.GivenPath);
        // The non-visual fields stay the winner's.
        Assert.Equal(race, addon.Race.FormKey);
        var armor = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal(40, armor.ArmorRating);
        Assert.Equal(addon.FormKey, Assert.Single(armor.Armature).FormKey);

        // The shared original and the generic item that uses it keep the look they had.
        Assert.Equal(@"Armor\Iron\cuirass_m.nif", _plugins.BaseAddon.WorldModel!.Male!.File.GivenPath);
        Assert.DoesNotContain(_plugins.Patch.ArmorAddons, record => record.FormKey == _plugins.BaseAddon.FormKey);
        Assert.DoesNotContain(_plugins.Patch.Armors, record => record.FormKey == generic.FormKey);
        Assert.Equal(_plugins.BaseAddon.FormKey, Assert.Single(generic.Armature).FormKey);

        var change = Assert.Single(result.Changes);
        Assert.Contains(change.Fields, field => field.StartsWith("ARMA IronCuirassAA", StringComparison.Ordinal));
        Assert.Equal(new[] { look.ModKey }, change.LookPlugins);
        Assert.Equal(1, result.ForwardedLookRecords);
        Assert.Empty(_log.Warnings);
        var assets = result.Assets.Select(asset => asset.Path).ToList();
        Assert.Contains(@"Meshes\Armor\Better\cuirass_1.nif", assets);
        // The other weight's mesh is inferred: the game loads both.
        Assert.Contains(@"Meshes\Armor\Better\cuirass_0.nif", assets);
    }

    [Fact]
    public void One_copy_is_shared_by_all_items_that_reach_the_form()
    {
        var second = _plugins.Base.Armors.AddNew("IronGauntletsLike");
        second.Armature.Add(_plugins.BaseAddon.ToLink());
        var look = Retexture("Retexture.esp");
        look.Armors.GetOrAddAsOverride(second);
        look.ArmorAddons.Single().WorldModel = Male(@"Armor\Better\cuirass_m.nif");

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var addon = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.Equal(2, _plugins.Patch.Armors.Count);
        Assert.All(_plugins.Patch.Armors, armor => Assert.Equal(addon.FormKey, Assert.Single(armor.Armature).FormKey));
        Assert.Equal(2, result.Changes.Count);
        Assert.Single(result.Copies);
    }

    [Fact]
    public void The_walk_stops_at_a_form_no_look_plugin_contains()
    {
        var textureSet = AddIronTextureSet();
        // The look plugin changes the texture set but not the armor addon that leads to it.
        var look = TestPlugins.NewMod("Retexture.esp");
        look.Armors.GetOrAddAsOverride(_plugins.Cuirass);
        look.TextureSets.GetOrAddAsOverride(textureSet).Diffuse = @"Armor\Better\iron_d.dds";

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        Assert.Empty(result.Copies);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.IdenticalSkipped);
        Assert.Empty(_plugins.Patch.EnumerateMajorRecords());
    }

    [Fact]
    public void The_walk_goes_on_through_look_contained_forms_and_the_copies_point_at_each_other()
    {
        var textureSet = AddIronTextureSet();
        var look = Retexture("Retexture.esp");
        look.TextureSets.GetOrAddAsOverride(textureSet).Diffuse = @"Armor\Better\iron_d.dds";

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, "Retexture Mod"));

        var textureCopy = Assert.Single(_plugins.Patch.TextureSets);
        Assert.NotEqual(textureSet.FormKey, textureCopy.FormKey);
        Assert.Equal(@"Armor\Better\iron_d.dds", textureCopy.Diffuse?.GivenPath);
        Assert.Equal(@"Armor\Iron\iron_d.dds", textureSet.Diffuse?.GivenPath);
        // The armor addon looks the same as before but has to point at the copied texture set, so it is copied too.
        var addonCopy = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.Equal(textureCopy.FormKey, Assert.Single(addonCopy.WorldModel!.Male!.AlternateTextures!).NewTexture.FormKey);
        Assert.Equal(addonCopy.FormKey, Assert.Single(Assert.Single(_plugins.Patch.Armors).Armature).FormKey);
        Assert.Equal(2, result.ForwardedLookRecords);
        Assert.Contains(@"Textures\Armor\Better\iron_d.dds", result.Assets.Select(asset => asset.Path));
        Assert.DoesNotContain(result.Assets, asset => asset.Path.Contains(@"\Iron\", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false, "Second")]
    [InlineData(true, "First")]
    public void The_latest_look_plugin_in_the_load_order_provides_each_form(bool firstLoadsLast, string expectedMesh)
    {
        var first = Retexture("First.esp");
        first.ArmorAddons.Single().WorldModel = Male(@"Armor\First\cuirass_m.nif");
        var second = Retexture("Second.esp");
        second.ArmorAddons.Single().WorldModel = Male(@"Armor\Second\cuirass_m.nif");
        var loadOrder = firstLoadsLast ? _plugins.LoadOrder(second, first) : _plugins.LoadOrder(first, second);

        var result = Merge(selfContained: true, loadOrder, new LookSource(first, null), new LookSource(second, null));

        Assert.Equal($@"Armor\{expectedMesh}\cuirass_m.nif", Assert.Single(_plugins.Patch.ArmorAddons).WorldModel?.Male?.File.GivenPath);
        var winner = firstLoadsLast ? first : second;
        Assert.Equal(new[] { winner.ModKey }, Assert.Single(result.Changes).LookPlugins);
    }

    [Fact]
    public void A_form_whose_visual_fields_match_needs_no_copy()
    {
        _plugins.BaseAddon.WorldModel!.Male!.Data = new byte[] { 1, 2, 3 };
        var look = Retexture("Resaved.esp");
        look.ArmorAddons.Single().WorldModel = new GenderedItem<Model?>(new Model { File = @"ARMOR\IRON\CUIRASS_M.NIF", Data = new byte[] { 9 } }, null);

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        Assert.Empty(result.Copies);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.IdenticalSkipped);
        Assert.Empty(_plugins.Patch.EnumerateMajorRecords());
    }

    [Fact]
    public void Without_merging_an_added_form_is_still_copied_and_keeps_its_links_into_the_look_plugin()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        var lookRace = new FormKey(look.ModKey, 0x000DEA);
        look.ArmorAddons.Single().AdditionalRaces.Add(new FormLink<IRaceGetter>(lookRace));

        var result = Merge(selfContained: false, _plugins.LoadOrder(look), new LookSource(look, null));

        var addon = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.Equal(_plugins.Patch.ModKey, addon.FormKey.ModKey);
        Assert.Equal(lookRace, Assert.Single(addon.AdditionalRaces).FormKey);
        Assert.Equal(addon.FormKey, Assert.Single(Assert.Single(_plugins.Patch.Armors).Armature).FormKey);
        Assert.Equal(_plugins.Patch.ModKey, Assert.Single(_plugins.Patch.TextureSets).FormKey.ModKey);
        Assert.Equal(2, result.DuplicatedRecords);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Art_objects_an_armor_addon_uses_are_carried_and_the_addon_points_at_the_copy()
    {
        var art = _plugins.Base.ArtObjects.AddNew("IronArt");
        art.Model = new Model { File = @"Art\iron.nif" };
        _plugins.BaseAddon.ArtObject.SetTo(art);
        var look = Retexture("Retexture.esp");
        look.ArtObjects.GetOrAddAsOverride(art).Model = new Model { File = @"Art\better.nif" };

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var artCopy = Assert.Single(_plugins.Patch.ArtObjects);
        Assert.Equal(@"Art\better.nif", artCopy.Model?.File.GivenPath);
        Assert.Equal(artCopy.FormKey, Assert.Single(_plugins.Patch.ArmorAddons).ArtObject.FormKey);
        Assert.Equal(@"Art\iron.nif", art.Model.File.GivenPath);
        Assert.Contains(@"Meshes\Art\better.nif", result.Assets.Select(asset => asset.Path));
    }

    [Fact]
    public void Impact_data_a_weapon_keeps_is_followed_to_the_impacts_and_the_weapon_points_at_the_copy()
    {
        var impact = _plugins.Base.Impacts.AddNew("IronImpact");
        impact.Model = new Model { File = @"Impacts\iron.nif" };
        var set = _plugins.Base.ImpactDataSets.AddNew("IronImpactSet");
        set.Impacts.Add(new ImpactData { Impact = new FormLink<IImpactGetter>(impact.FormKey) });
        _plugins.Sword.ImpactDataSet.SetTo(set);
        _plugins.Stats.Weapons.Single().ImpactDataSet.SetTo(set);
        var look = TestPlugins.NewMod("Impacts.esp");
        look.Weapons.GetOrAddAsOverride(_plugins.Sword);
        look.ImpactDataSets.GetOrAddAsOverride(set);
        var lookImpact = look.Impacts.GetOrAddAsOverride(impact);
        lookImpact.Model = new Model { File = @"Impacts\better.nif" };
        lookImpact.Duration = 5;

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var impactCopy = Assert.Single(_plugins.Patch.Impacts);
        Assert.Equal(@"Impacts\better.nif", impactCopy.Model?.File.GivenPath);
        var setCopy = Assert.Single(_plugins.Patch.ImpactDataSets);
        Assert.Equal(impactCopy.FormKey, Assert.Single(setCopy.Impacts).Impact.FormKey);
        var sword = Assert.Single(_plugins.Patch.Weapons);
        Assert.Equal(setCopy.FormKey, sword.ImpactDataSet.FormKey);
        Assert.Equal(77u, sword.BasicStats?.Value);
        Assert.Equal(@"Impacts\iron.nif", impact.Model.File.GivenPath);
        Assert.Equal(setCopy.FormKey.ModKey, _plugins.Patch.ModKey);
        Assert.Equal(new[] { "IPCT", "IPDS" }, result.Copies.Select(copy => copy.Signature).Order());
    }

    [Fact]
    public void A_light_a_projectile_adds_is_copied_and_the_arrow_points_at_the_projectile_copy()
    {
        var projectile = _plugins.Base.Projectiles.AddNew("IronArrowProjectile");
        projectile.Model = new Model { File = @"Weapons\Iron\arrow_flight.nif" };
        projectile.Speed = 5000;
        var arrow = _plugins.Base.Ammunitions.AddNew("IronArrow");
        arrow.Projectile.SetTo(projectile);
        var look = TestPlugins.NewMod("Glow.esp");
        var light = look.Lights.AddNew("GlowLight");
        light.Radius = 300;
        look.Ammunitions.GetOrAddAsOverride(arrow);
        var lookProjectile = look.Projectiles.GetOrAddAsOverride(projectile);
        lookProjectile.Light.SetTo(light);
        lookProjectile.Speed = 1;

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var lightCopy = Assert.Single(_plugins.Patch.Lights);
        Assert.Equal(300u, lightCopy.Radius);
        Assert.Equal(_plugins.Patch.ModKey, lightCopy.FormKey.ModKey);
        var projectileCopy = Assert.Single(_plugins.Patch.Projectiles);
        Assert.Equal(lightCopy.FormKey, projectileCopy.Light.FormKey);
        Assert.Equal(5000f, projectileCopy.Speed);
        Assert.Equal(projectileCopy.FormKey, Assert.Single(_plugins.Patch.Ammunitions).Projectile.FormKey);
        Assert.True(projectile.Light.IsNull);
        Assert.Empty(_log.Warnings);
        Assert.Equal(arrow.FormKey, Assert.Single(result.Changes).FormKey);
    }

    [Fact]
    public void An_item_skipped_for_a_type_discrepancy_contributes_no_walk()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        _plugins.AddSwordLook(look);
        _plugins.Stats.Weapons.Single().Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword };
        look.Weapons.Single().Data = new WeaponData { AnimationType = WeaponAnimationType.Bow };

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        Assert.Equal(1, result.TypeSkipped);
        Assert.Empty(_plugins.Patch.Weapons);
        // The static the skipped sword would have needed is not copied; the cuirass goes through.
        Assert.Empty(_plugins.Patch.Statics);
        Assert.Single(_plugins.Patch.Armors);
        Assert.DoesNotContain(result.Copies, copy => copy.Signature == "STAT");
    }

    /// <summary>Like Synthesis' persistent allocator: one FormKey per EditorID, so asking twice for one EditorID gives one record.</summary>
    private sealed class EditorIdAllocator(IMod mod) : IFormKeyAllocator
    {
        private readonly Dictionary<string, FormKey> _byEditorID = [];

        public FormKey GetNextFormKey() => new(mod.ModKey, mod.NextFormID++);

        public FormKey GetNextFormKey(string? editorID)
        {
            if (editorID == null) return GetNextFormKey();
            if (!_byEditorID.TryGetValue(editorID, out var formKey)) _byEditorID[editorID] = formKey = GetNextFormKey();
            return formKey;
        }
    }

    private static GenderedItem<IFormLinkNullableGetter<IFormListGetter>> ListLink(FormKey? formKey) =>
        new(new FormLinkNullable<IFormListGetter>(formKey), new FormLinkNullable<IFormListGetter>());

    [Fact]
    public void Two_added_forms_with_one_EditorID_get_copies_with_their_own_FormKey_and_EditorID()
    {
        _plugins.Patch.SetAllocator(new EditorIdAllocator(_plugins.Patch));
        var second = _plugins.Base.Armors.AddNew("SecondArmor");
        var lookA = _plugins.LookPlugin("LookA.esp", "Fancy");
        var lookB = TestPlugins.NewMod("LookB.esp");
        var textureSet = lookB.TextureSets.AddNew("FancyTXST");
        var addon = lookB.ArmorAddons.AddNew("FancyAA");
        addon.WorldModel = new GenderedItem<Model?>(
            new Model { File = @"Armor\Other\other_m.nif", AlternateTextures = [new AlternateTexture { Name = "Body", Index = 0, NewTexture = textureSet.ToLink() }] },
            null);
        lookB.Armors.GetOrAddAsOverride(second).Armature.SetTo([addon.ToLink()]);

        var result = Merge(selfContained: true, _plugins.LoadOrder(lookA, lookB), new LookSource(lookA, null), new LookSource(lookB, null));

        Assert.Equal(2, _plugins.Patch.TextureSets.Count);
        Assert.Equal(2, _plugins.Patch.ArmorAddons.Count);
        Assert.Equal(2, _plugins.Patch.TextureSets.Select(record => record.FormKey).Distinct().Count());
        Assert.Equal(2, _plugins.Patch.TextureSets.Select(record => record.EditorID).Distinct().Count());
        Assert.Equal(2, _plugins.Patch.ArmorAddons.Select(record => record.EditorID).Distinct().Count());
        Assert.All(_plugins.Patch.TextureSets, record => Assert.StartsWith("FancyTXST_ILM", record.EditorID, StringComparison.Ordinal));
        // Every addon points at the texture set copy of its own plugin.
        var meshes = _plugins.Patch.ArmorAddons.Select(record => record.WorldModel!.Male!.File.GivenPath).Order().ToList();
        Assert.Equal([@"Armor\Fancy\Fancy_m.nif", @"Armor\Other\other_m.nif"], meshes);
        Assert.Equal(4, result.Copies.Count);
        Assert.Equal(4, _plugins.Patch.EnumerateMajorRecords().Where(record => record is ITextureSet or IArmorAddon).Select(record => record.FormKey).Distinct().Count());
    }

    [Fact]
    public void A_copy_is_named_from_its_source_with_the_suffix_once_and_without_a_name_when_the_source_has_none()
    {
        _plugins.BaseAddon.EditorID = "IronCuirassAA_ILM";
        var look = Retexture("Retexture.esp");
        look.ArmorAddons.Single().WorldModel = Male(@"Armor\Better\cuirass_1.nif");

        Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));
        Assert.DoesNotContain("_ILM_ILM", Assert.Single(_plugins.Patch.ArmorAddons).EditorID, StringComparison.Ordinal);

        _plugins.BaseAddon.EditorID = null;
        var other = TestPlugins.NewMod("Patch2.esp");
        LookMerger.Merge(_plugins.LoadOrder(look), other, [new LookSource(look, null)], false, true, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);
        Assert.Null(Assert.Single(other.ArmorAddons).EditorID);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cycle_of_form_lists_is_copied_whichever_form_the_walk_enters_first(bool secondItemFirst)
    {
        var plain = _plugins.Base.TextureSets.AddNew("PlainTXST");
        var listA = _plugins.Base.FormLists.AddNew("ListA");
        var listB = _plugins.Base.FormLists.AddNew("ListB");
        listA.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(listB.FormKey));
        listB.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(listA.FormKey));
        var secondAddon = _plugins.Base.ArmorAddons.AddNew("SecondAA");
        secondAddon.TextureSwapList = ListLink(listB.FormKey);
        _plugins.BaseAddon.TextureSwapList = ListLink(listA.FormKey);
        var second = _plugins.Base.Armors.AddNew("SecondArmor");
        second.Armature.Add(secondAddon.ToLink());

        var look = TestPlugins.NewMod("Cycle.esp");
        if (secondItemFirst) look.Armors.GetOrAddAsOverride(second);
        look.Armors.GetOrAddAsOverride(_plugins.Cuirass);
        if (!secondItemFirst) look.Armors.GetOrAddAsOverride(second);
        look.ArmorAddons.GetOrAddAsOverride(_plugins.BaseAddon);
        look.ArmorAddons.GetOrAddAsOverride(secondAddon);
        // Only A changes: an extra entry, a form no look plugin contains. B equals the stats side's.
        look.FormLists.GetOrAddAsOverride(listA).Items.Add(new FormLink<ISkyrimMajorRecordGetter>(plain.FormKey));
        look.FormLists.GetOrAddAsOverride(listB);

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        Assert.Equal(2, _plugins.Patch.FormLists.Count);
        var copyA = _plugins.Patch.FormLists.Single(record => record.EditorID!.StartsWith("ListA", StringComparison.Ordinal));
        var copyB = _plugins.Patch.FormLists.Single(record => record.EditorID!.StartsWith("ListB", StringComparison.Ordinal));
        // The copies point at each other; an entry that points at a form no look plugin contains stays that form.
        Assert.Equal([copyB.FormKey, plain.FormKey], copyA.Items.Select(link => link.FormKey));
        Assert.Equal([copyA.FormKey], copyB.Items.Select(link => link.FormKey));
        // Both items reach the copies, whichever of the two lists they lead to.
        Assert.Equal(2, _plugins.Patch.ArmorAddons.Count);
        var swapLists = _plugins.Patch.ArmorAddons.Select(record => record.TextureSwapList!.Male.FormKey).ToHashSet();
        Assert.Equal(new[] { copyA.FormKey, copyB.FormKey }.ToHashSet(), swapLists);
        Assert.Equal(2, _plugins.Patch.Armors.Count);
        Assert.Equal(4, result.Copies.Count);
        // The originals are untouched.
        Assert.Equal(listB.FormKey, Assert.Single(listA.Items).FormKey);
    }

    [Fact]
    public void Nested_form_lists_keep_the_look_plugins_list_as_is_with_entries_pointing_at_copies_or_the_original_form()
    {
        var plain = _plugins.Base.TextureSets.AddNew("PlainTXST");
        var inner = _plugins.Base.FormLists.AddNew("InnerList");
        var outer = _plugins.Base.FormLists.AddNew("OuterList");
        var winnerExtra = _plugins.Base.TextureSets.AddNew("WinnerExtraTXST");
        outer.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(inner.FormKey));
        _plugins.BaseAddon.TextureSwapList = ListLink(outer.FormKey);
        // A non-look plugin adds an entry to the outer list; the look plugin's list does not have it.
        _plugins.Stats.FormLists.GetOrAddAsOverride(outer).Items.Add(new FormLink<ISkyrimMajorRecordGetter>(winnerExtra.FormKey));
        var look = Retexture("Retexture.esp");
        var lookOuter = look.FormLists.GetOrAddAsOverride(outer);
        lookOuter.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(plain.FormKey));
        look.FormLists.GetOrAddAsOverride(inner).Items.Add(new FormLink<ISkyrimMajorRecordGetter>(plain.FormKey));

        Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var innerCopy = _plugins.Patch.FormLists.Single(record => record.EditorID!.StartsWith("InnerList", StringComparison.Ordinal));
        var outerCopy = _plugins.Patch.FormLists.Single(record => record.EditorID!.StartsWith("OuterList", StringComparison.Ordinal));
        Assert.Equal([innerCopy.FormKey, plain.FormKey], outerCopy.Items.Select(link => link.FormKey));
        Assert.DoesNotContain(winnerExtra.FormKey, outerCopy.Items.Select(link => link.FormKey));
        Assert.Equal([plain.FormKey], innerCopy.Items.Select(link => link.FormKey));
    }

    [Fact]
    public void An_armors_addon_list_is_the_look_plugins_with_copied_addons_re_pointed_and_the_others_left_as_they_are()
    {
        var otherAddon = _plugins.Base.ArmorAddons.AddNew("OtherAA");
        var winnerAddon = _plugins.Base.ArmorAddons.AddNew("WinnerAddedAA");
        _plugins.Stats.Armors.GetOrAddAsOverride(_plugins.Cuirass).Armature.Add(winnerAddon.ToLink());
        var look = Retexture("Retexture.esp");
        look.ArmorAddons.Single().WorldModel = Male(@"Armor\Better\cuirass_1.nif");
        look.Armors.Single().Armature.Add(otherAddon.ToLink());

        Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var addonCopy = Assert.Single(_plugins.Patch.ArmorAddons);
        var armor = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal([addonCopy.FormKey, otherAddon.FormKey], armor.Armature.Select(link => link.FormKey));
    }

    [Fact]
    public void An_armor_addons_weight_slider_is_part_of_its_look()
    {
        _plugins.BaseAddon.WeightSliderEnabled = new GenderedItem<bool>(false, false);
        var look = Retexture("Retexture.esp");
        look.ArmorAddons.Single().WeightSliderEnabled = new GenderedItem<bool>(true, false);

        Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var addon = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.True(addon.WeightSliderEnabled.Male);
        Assert.False(addon.WeightSliderEnabled.Female);
    }

    [Fact]
    public void Gendered_links_of_an_armor_addon_are_compared_by_form()
    {
        var skin = _plugins.Base.TextureSets.AddNew("SkinTXST");
        var list = _plugins.Base.FormLists.AddNew("SwapList");
        _plugins.BaseAddon.SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
            new FormLinkNullable<ITextureSetGetter>(skin.FormKey), new FormLinkNullable<ITextureSetGetter>());
        _plugins.BaseAddon.TextureSwapList = ListLink(list.FormKey);
        var look = Retexture("Retexture.esp");

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        Assert.Empty(result.Copies);
        Assert.Empty(_plugins.Patch.EnumerateMajorRecords());

        var otherSkin = _plugins.Base.TextureSets.AddNew("OtherSkinTXST");
        look.ArmorAddons.Single().SkinTexture = new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
            new FormLinkNullable<ITextureSetGetter>(otherSkin.FormKey), new FormLinkNullable<ITextureSetGetter>());
        var again = TestPlugins.NewMod("Patch2.esp");
        var second = LookMerger.Merge(_plugins.LoadOrder(look), again, [new LookSource(look, null)], false, true, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);

        Assert.Equal(otherSkin.FormKey, Assert.Single(again.ArmorAddons).SkinTexture!.Male.FormKey);
        Assert.Single(second.Copies);
    }

    [Fact]
    public void A_form_the_look_plugin_alone_overrides_is_copied_from_the_origin_master()
    {
        var look = Retexture("Retexture.esp");
        look.ArmorAddons.Single().WorldModel = Male(@"Armor\Better\cuirass_1.nif");

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var addon = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.Equal("IronCuirassAA_ILM", addon.EditorID);
        Assert.Equal(look.ModKey, Assert.Single(result.Copies).LookPlugin);
        Assert.Equal(_plugins.Stats.ModKey, Assert.Single(result.Changes).StatsPlugin);
    }

    [Fact]
    public void Only_the_assets_a_look_changes_are_asked_for()
    {
        var textureSet = _plugins.Base.TextureSets.AddNew("SteelTXST");
        textureSet.Diffuse = @"Armor\Steel\steel_d.dds";
        textureSet.NormalOrGloss = @"Armor\Steel\steel_n.dds";
        _plugins.BaseAddon.WorldModel!.Male!.AlternateTextures = [new AlternateTexture { Name = "Body", Index = 0, NewTexture = textureSet.ToLink() }];
        var look = Retexture("Retexture.esp");
        look.TextureSets.GetOrAddAsOverride(textureSet).NormalOrGloss = @"Armor\Steel\steel_n_new.dds";

        var result = Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        Assert.Equal([@"Textures\Armor\Steel\steel_n_new.dds"], result.Assets.Select(asset => asset.Path));
    }

    [Fact]
    public void A_self_contained_fallback_replaces_only_the_link_into_the_look_plugin_and_keeps_the_rest_of_the_look()
    {
        var first = _plugins.Base.TextureSets.AddNew("GemTXST1");
        var second = _plugins.Base.TextureSets.AddNew("GemTXST2");
        var gem = _plugins.Base.MiscItems.AddNew("Gem");
        gem.Model = new Model
        {
            File = @"Clutter\gem.nif",
            AlternateTextures =
            [
                new AlternateTexture { Name = "A", Index = 0, NewTexture = first.ToLink() },
                new AlternateTexture { Name = "B", Index = 1, NewTexture = second.ToLink() },
            ],
        };
        _plugins.Stats.MiscItems.GetOrAddAsOverride(gem).Value = 5;
        var look = TestPlugins.NewMod("Look.esp");
        look.MiscItems.GetOrAddAsOverride(gem).Model = new Model
        {
            File = @"Clutter\better.nif",
            AlternateTextures =
            [
                new AlternateTexture { Name = "A", Index = 0, NewTexture = first.ToLink() },
                new AlternateTexture { Name = "B", Index = 1, NewTexture = new FormLink<ITextureSetGetter>(new FormKey(look.ModKey, 0x000ABC)) },
            ],
        };

        Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var model = Assert.Single(_plugins.Patch.MiscItems).Model!;
        Assert.Equal(@"Clutter\better.nif", model.File.GivenPath);
        Assert.Equal([first.FormKey, second.FormKey], model.AlternateTextures!.Select(texture => texture.NewTexture.FormKey));
        Assert.Contains(_log.Warnings, warning => warning.Contains("Gem") && warning.Contains("Model"));
    }

    [Fact]
    public void A_copy_of_an_added_form_points_at_the_copy_of_a_form_it_links_to_outside_its_visual_fields()
    {
        var look = _plugins.LookPlugin("Look.esp", "Fancy");
        var rubble = look.Debris.AddNew("Rubble");
        var crate = look.Lights.AddNew("Lamp");
        crate.Model = new Model { File = @"Clutter\lamp.nif" };
        // The lamp's debris is outside its visual fields; the list below leads the walk to both.
        crate.Destructible = new Destructible
        {
            Stages = [new DestructionStage { Data = new DestructionStageData { Debris = rubble.ToLink() } }],
        };
        var list = look.FormLists.AddNew("SwapList");
        list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(crate.FormKey));
        list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(rubble.FormKey));
        look.ArmorAddons.Single().TextureSwapList = ListLink(list.FormKey);

        Merge(selfContained: true, _plugins.LoadOrder(look), new LookSource(look, null));

        var rubbleCopy = Assert.Single(_plugins.Patch.Debris);
        var crateCopy = Assert.Single(_plugins.Patch.Lights);
        Assert.Equal(rubbleCopy.FormKey, Assert.Single(crateCopy.Destructible!.Stages).Data!.Debris.FormKey);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Identical_effect_shaders_and_material_objects_saved_apart_have_no_differences()
    {
        var shader = _plugins.Base.EffectShaders.AddNew("Fire");
        var materialObject = _plugins.Base.MaterialObjects.AddNew("Stone");
        materialObject.DNAMs.Add(new byte[] { 1, 2, 3 });
        var look = TestPlugins.NewMod("Resaved.esp");
        var lookShader = look.EffectShaders.GetOrAddAsOverride(shader);
        lookShader.DATADataTypeState = EffectShader.DATADataType.Break0;
        var lookMaterial = look.MaterialObjects.GetOrAddAsOverride(materialObject);
        lookMaterial.DATADataTypeState = MaterialObject.DATADataType.Break0;

        Assert.Empty(LookRecordType.All.Single(type => type.Signature == "EFSH").Differences(shader, lookShader));
        Assert.Empty(LookRecordType.All.Single(type => type.Signature == "MATO").Differences(materialObject, lookMaterial));

        lookMaterial.DNAMs[0] = new byte[] { 1, 2, 4 };
        Assert.Equal(["DNAMs"], LookRecordType.All.Single(type => type.Signature == "MATO").Differences(materialObject, lookMaterial));
    }

    [Fact]
    public void A_light_copy_takes_the_look_but_keeps_whether_it_can_be_carried_and_its_time()
    {
        var light = _plugins.Base.Lights.AddNew("Torch");
        light.Time = 100;
        light.Flags = Light.Flag.CanBeCarried;
        var lookLight = TestPlugins.NewMod("Look.esp").Lights.GetOrAddAsOverride(light);
        var type = LookRecordType.All.Single(t => t.Signature == "LIGH");

        lookLight.Flags = Light.Flag.Dynamic;
        lookLight.Time = 5;
        Assert.Equal(["Flags"], type.Differences(light, lookLight));

        lookLight.Flags = Light.Flag.Dynamic | Light.Flag.CanBeCarried;
        lookLight.Radius = 99;
        Assert.Equal(["Radius", "Flags"], type.Differences(light, lookLight));

        var target = _plugins.Patch.Lights.GetOrAddAsOverride(light);
        lookLight.Flags = Light.Flag.Dynamic;
        type.Copy(target, lookLight);
        Assert.Equal(Light.Flag.Dynamic | Light.Flag.CanBeCarried, target.Flags);
        Assert.Equal(99u, target.Radius);
        Assert.Equal(100, target.Time);
    }

    [Fact]
    public void A_debris_copy_takes_the_look_plugins_models()
    {
        var debris = _plugins.Base.Debris.AddNew("Rubble");
        debris.Models.Add(new DebrisModel { Flags = 0 });
        var lookDebris = TestPlugins.NewMod("Look.esp").Debris.GetOrAddAsOverride(debris);
        lookDebris.Models.Clear();
        var type = LookRecordType.All.Single(t => t.Signature == "DEBR");

        Assert.Equal(["Models"], type.Differences(debris, lookDebris));
        var target = _plugins.Patch.Debris.GetOrAddAsOverride(debris);
        type.Copy(target, lookDebris);
        Assert.Empty(target.Models);
    }

    [Fact]
    public void A_static_copy_takes_the_object_bounds()
    {
        var plainStatic = _plugins.Base.Statics.AddNew("Wall");
        var look = TestPlugins.NewMod("Look.esp");
        var lookStatic = look.Statics.GetOrAddAsOverride(plainStatic);
        lookStatic.ObjectBounds.First = new P3Int16(-5, -5, -5);
        var type = LookRecordType.All.Single(t => t.Signature == "STAT");

        Assert.Equal(["ObjectBounds"], type.Differences(plainStatic, lookStatic));
    }

    [Fact]
    public void Every_visual_field_names_a_property_of_its_record_type()
    {
        foreach (var type in LookRecordType.All)
        {
            var names = type.GetterType.GetProperties().Concat(type.GetterType.GetInterfaces().SelectMany(contract => contract.GetProperties()))
                .Select(property => property.Name)
                .ToHashSet();
            Assert.NotEmpty(type.Fields);
            Assert.All(type.Fields, field => Assert.True(names.Contains(field), $"{type.Signature} has no property {field}"));
        }
    }

    [Fact]
    public void Effect_shaders_and_material_objects_take_every_field_except_sounds_and_the_record_header()
    {
        var shader = LookRecordType.All.Single(type => type.Signature == "EFSH").Fields;
        Assert.Contains("FillTexture", shader);
        Assert.Contains("ColorKey1", shader);
        Assert.DoesNotContain("AmbientSound", shader);
        Assert.DoesNotContain("EditorID", shader);
        Assert.DoesNotContain("FormKey", shader);
        Assert.Contains("Model", LookRecordType.All.Single(type => type.Signature == "MATO").Fields);
    }
}
