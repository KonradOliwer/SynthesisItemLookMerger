using ItemLookMerger.Core;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Tests.Merging;

public class TypeDiscrepancyTests
{
    private static readonly SkyrimMod Scratch = TestPlugins.NewMod("Scratch.esp");

    // ---- Detection ----

    [Theory]
    [InlineData(WeaponAnimationType.OneHandSword, WeaponAnimationType.TwoHandSword)]
    [InlineData(WeaponAnimationType.TwoHandAxe, WeaponAnimationType.OneHandAxe)]
    [InlineData(WeaponAnimationType.OneHandSword, WeaponAnimationType.OneHandAxe)]
    [InlineData(WeaponAnimationType.OneHandDagger, WeaponAnimationType.OneHandMace)]
    [InlineData(WeaponAnimationType.HandToHand, WeaponAnimationType.OneHandDagger)]
    public void A_different_animation_type_within_the_melee_family_is_a_narrow_weapon_discrepancy(WeaponAnimationType stats, WeaponAnimationType look)
    {
        var discrepancy = TypeDiscrepancies.Find(WeaponOf(stats), WeaponOf(look));

        Assert.NotNull(discrepancy);
        Assert.False(discrepancy.Wide);
        Assert.Equal(stats.ToString(), discrepancy.StatsType);
        Assert.Equal(look.ToString(), discrepancy.LookType);
    }

    [Theory]
    [InlineData(WeaponAnimationType.OneHandSword, WeaponAnimationType.Bow)]
    [InlineData(WeaponAnimationType.TwoHandSword, WeaponAnimationType.Staff)]
    [InlineData(WeaponAnimationType.Bow, WeaponAnimationType.Crossbow)]
    [InlineData(WeaponAnimationType.Crossbow, WeaponAnimationType.OneHandSword)]
    [InlineData(WeaponAnimationType.Staff, WeaponAnimationType.Bow)]
    public void A_change_between_melee_bow_crossbow_and_staff_is_a_wide_weapon_discrepancy(WeaponAnimationType stats, WeaponAnimationType look) =>
        Assert.True(TypeDiscrepancies.Find(WeaponOf(stats), WeaponOf(look))!.Wide);

    [Fact]
    public void The_same_animation_type_or_a_missing_weapon_data_is_no_discrepancy()
    {
        Assert.Null(TypeDiscrepancies.Find(WeaponOf(WeaponAnimationType.Bow), WeaponOf(WeaponAnimationType.Bow)));
        Assert.Null(TypeDiscrepancies.Find(WeaponOf(WeaponAnimationType.Bow), Scratch.Weapons.AddNew()));
        Assert.Null(TypeDiscrepancies.Find(Scratch.Weapons.AddNew(), WeaponOf(WeaponAnimationType.Staff)));
    }

    [Theory]
    [InlineData(ArmorType.LightArmor, ArmorType.HeavyArmor)]
    [InlineData(ArmorType.HeavyArmor, ArmorType.Clothing)]
    [InlineData(ArmorType.Clothing, ArmorType.LightArmor)]
    public void A_different_armor_type_in_the_same_equip_class_is_a_narrow_armor_discrepancy(ArmorType stats, ArmorType look)
    {
        var discrepancy = TypeDiscrepancies.Find(ArmorOf(BipedObjectFlag.Body, stats), ArmorOf(BipedObjectFlag.Body, look));

        Assert.NotNull(discrepancy);
        Assert.False(discrepancy.Wide);
    }

    [Theory]
    [InlineData(BipedObjectFlag.Body, BipedObjectFlag.Shield)]
    [InlineData(BipedObjectFlag.Shield, BipedObjectFlag.Ring)]
    [InlineData(BipedObjectFlag.Amulet, BipedObjectFlag.Head)]
    [InlineData(BipedObjectFlag.Circlet, BipedObjectFlag.Body | BipedObjectFlag.Hands)]
    [InlineData(BipedObjectFlag.Head | BipedObjectFlag.Circlet, BipedObjectFlag.Ring)]
    public void A_change_between_body_armor_shield_and_jewelry_is_a_wide_armor_discrepancy(BipedObjectFlag stats, BipedObjectFlag look) =>
        Assert.True(TypeDiscrepancies.Find(ArmorOf(stats, ArmorType.LightArmor), ArmorOf(look, ArmorType.LightArmor))!.Wide);

    [Theory]
    [InlineData(BipedObjectFlag.Body, BipedObjectFlag.Feet)]
    [InlineData(BipedObjectFlag.Head | BipedObjectFlag.Hair, BipedObjectFlag.Hands | BipedObjectFlag.Forearms)]
    [InlineData(BipedObjectFlag.Ring, BipedObjectFlag.Amulet)]
    [InlineData(BipedObjectFlag.Circlet, BipedObjectFlag.Ears)]
    public void Pieces_of_one_equip_class_are_no_wide_discrepancy_and_armor_of_the_same_type_is_none(BipedObjectFlag stats, BipedObjectFlag look) =>
        Assert.Null(TypeDiscrepancies.Find(ArmorOf(stats, ArmorType.HeavyArmor), ArmorOf(look, ArmorType.HeavyArmor)));

    [Fact]
    public void An_armor_without_a_body_template_has_no_type_to_compare()
    {
        var bare = Scratch.Armors.AddNew();
        var shield = ArmorOf(BipedObjectFlag.Shield, ArmorType.LightArmor);
        var ring = ArmorOf(BipedObjectFlag.Ring, ArmorType.Clothing);

        Assert.Null(TypeDiscrepancies.Find(bare, shield));
        Assert.Null(TypeDiscrepancies.Find(ring, bare));
        Assert.Null(TypeDiscrepancies.Find(bare, Scratch.Armors.AddNew()));
    }

    [Fact]
    public void Arrow_to_bolt_is_a_wide_ammo_discrepancy_and_the_same_kind_is_none()
    {
        var arrow = Scratch.Ammunitions.AddNew();
        arrow.Flags = Ammunition.Flag.NonBolt;
        var bolt = Scratch.Ammunitions.AddNew();

        Assert.True(TypeDiscrepancies.Find(arrow, bolt)!.Wide);
        Assert.True(TypeDiscrepancies.Find(bolt, arrow)!.Wide);
        Assert.Null(TypeDiscrepancies.Find(arrow, arrow));
    }

    private static Weapon WeaponOf(WeaponAnimationType type)
    {
        var weapon = Scratch.Weapons.AddNew();
        weapon.Data = new WeaponData { AnimationType = type };
        return weapon;
    }

    private static Armor ArmorOf(BipedObjectFlag slots, ArmorType type)
    {
        var armor = Scratch.Armors.AddNew();
        armor.BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, ArmorType = type };
        return armor;
    }

    // ---- Merging ----

    private readonly TestLog _log = new();
    private readonly SkyrimMod _skyrim = TestPlugins.NewMod("Skyrim.esm");
    private readonly SkyrimMod _stats = TestPlugins.NewMod("Stats.esp");
    private readonly SkyrimMod _look = TestPlugins.NewMod("Look.esp");
    private readonly SkyrimMod _patch = TestPlugins.NewMod("Patch.esp");
    private readonly Keyword _sword;
    private readonly Keyword _greatsword;
    private readonly Keyword _iron;
    private readonly Keyword _steel;
    private readonly Keyword _vendor;
    private readonly EquipType _rightHand;
    private readonly EquipType _bothHands;
    private readonly SoundDescriptor _swingSword;
    private readonly SoundDescriptor _swingGreat;
    private readonly Weapon _ironGreatsword;
    private readonly Weapon _unique;

    public TypeDiscrepancyTests()
    {
        (_sword, _greatsword, _iron, _steel, _vendor) = (
            _skyrim.Keywords.AddNew("WeapTypeSword"), _skyrim.Keywords.AddNew("WeapTypeGreatsword"), _skyrim.Keywords.AddNew("WeapMaterialIron"),
            _skyrim.Keywords.AddNew("WeapMaterialSteel"), _skyrim.Keywords.AddNew("VendorItemWeapon"));
        (_rightHand, _bothHands) = (_skyrim.EquipTypes.AddNew("RightHand"), _skyrim.EquipTypes.AddNew("BothHands"));
        (_swingSword, _swingGreat) = (_skyrim.SoundDescriptors.AddNew("SwingSword"), _skyrim.SoundDescriptors.AddNew("SwingGreat"));

        // Greatswords that look like the base but are not generic: they come first, with the lowest ids, so a wrong filter picks them.
        var enchanted = Greatsword(_skyrim, "EnchantedGreatsword", 40);
        enchanted.ObjectEffect.SetTo(_skyrim.ObjectEffects.AddNew("EnchantFire"));
        Greatsword(_skyrim, "CutGreatsword", 41).MajorFlags = Weapon.MajorFlag.NonPlayable;
        Greatsword(_skyrim, "TemplatedGreatsword", 42).Template.SetTo(enchanted);
        var unplayable = Greatsword(_skyrim, "FlaggedGreatsword", 43);
        unplayable.Data!.Flags = WeaponData.Flag.NonPlayable;

        _ironGreatsword = Greatsword(_skyrim, "IronGreatsword", 17);
        _ironGreatsword.Data!.Flags = WeaponData.Flag.HideBackpack;
        _ironGreatsword.Critical = new CriticalData { Damage = 17 };

        _unique = NewWeapon(_skyrim, "UniqueBlade", WeaponAnimationType.OneHandSword, 1f, 1f, 8, 9f, _rightHand, _swingSword, _sword, _iron, _vendor);
        _unique.BasicStats!.Value = 100;
        _unique.Name = "Blade of Test";
        var statsBlade = _stats.Weapons.GetOrAddAsOverride(_unique);
        statsBlade.BasicStats!.Value = 500;
        statsBlade.Data!.Flags = WeaponData.Flag.CantDrop;
    }

    private Weapon Greatsword(SkyrimMod mod, string editorId, ushort damage, Keyword? material = null) =>
        NewWeapon(mod, editorId, WeaponAnimationType.TwoHandSword, 0.75f, 1.3f, damage, 16f, _bothHands, _swingGreat, _greatsword, material ?? _iron);

    private static Weapon NewWeapon(
        SkyrimMod mod, string editorId, WeaponAnimationType type, float speed, float reach, ushort damage, float weight, EquipType equip,
        SoundDescriptor attack, params Keyword[] keywords)
    {
        var weapon = mod.Weapons.AddNew(editorId);
        weapon.Name = editorId;
        weapon.Model = new Model { File = $@"Weapons\{editorId}.nif" };
        weapon.Data = new WeaponData { AnimationType = type, Speed = speed, Reach = reach };
        weapon.BasicStats = new WeaponBasicStats { Value = 10, Weight = weight, Damage = damage };
        weapon.EquipmentType.SetTo(equip);
        weapon.AttackSound.SetTo(attack);
        weapon.Keywords = [.. keywords.Select(keyword => (IFormLinkGetter<IKeywordGetter>)new FormLink<IKeywordGetter>(keyword.FormKey))];
        return weapon;
    }

    /// <summary>The look plugin's override of <paramref name="original"/>: a new model and animation type, and stats the merge must not take.</summary>
    private Weapon LookOverride(Weapon original, WeaponAnimationType type, params Keyword[] keywords)
    {
        var weapon = _look.Weapons.GetOrAddAsOverride(original);
        weapon.Model = new Model { File = $@"Weapons\Look\{original.EditorID}.nif" };
        weapon.Data = new WeaponData { AnimationType = type, Speed = 9f, Reach = 9f };
        weapon.BasicStats = new WeaponBasicStats { Value = 1, Weight = 1f, Damage = 99 };
        weapon.Keywords = [.. keywords.Select(keyword => (IFormLinkGetter<IKeywordGetter>)new FormLink<IKeywordGetter>(keyword.FormKey))];
        return weapon;
    }

    private IReadOnlyList<ISkyrimModGetter> LoadOrder => [_skyrim, _stats, _look];

    private MergeResult Merge(TypeDiscrepancyHandling handling, bool selfContained = false, IReadOnlyList<ISkyrimModGetter>? loadOrder = null) =>
        LookMerger.Merge(loadOrder ?? LoadOrder, _patch, [new LookSource(_look, "Look Mod")], forwardNames: false, selfContained, handling, _log);

    private Weapon PatchedWeapon(string editorId) => _patch.Weapons.Single(weapon => weapon.EditorID == editorId);

    private List<string> KeywordNames(IWeaponGetter weapon) =>
        weapon.Keywords!.Select(link => _skyrim.Keywords.Single(keyword => keyword.FormKey == link.FormKey).EditorID!).ToList();

    /// <summary>A plugin of the modlist overrides the weapon (without a stats override the look plugin's whole record would be forwarded).</summary>
    private Weapon WithStatsOverride(Weapon weapon)
    {
        _stats.Weapons.GetOrAddAsOverride(weapon).BasicStats!.Value = 11;
        return weapon;
    }

    [Fact]
    public void By_default_a_weapon_takes_the_type_bound_stats_of_the_generic_base_item_and_keeps_its_identity()
    {
        // The look's material keyword differs from the stats side's: the base follows the stats side's.
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _steel);
        Greatsword(_skyrim, "SteelGreatsword", 18, _steel);
        Greatsword(_look, "LookGreatsword", 99);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        var patched = PatchedWeapon("UniqueBlade");
        Assert.Equal(_unique.FormKey, patched.FormKey);
        Assert.Equal("Blade of Test", patched.Name?.String);
        Assert.Equal(@"Weapons\Look\UniqueBlade.nif", patched.Model?.File.GivenPath);
        // From the base item (IronGreatsword): data, damage and weight, critical data, equip slot, type and material keywords, sounds.
        Assert.Equal(WeaponAnimationType.TwoHandSword, patched.Data!.AnimationType);
        Assert.Equal(0.75f, patched.Data.Speed);
        Assert.Equal(1.3f, patched.Data.Reach);
        Assert.Equal((ushort)17, patched.BasicStats!.Damage);
        Assert.Equal(16f, patched.BasicStats.Weight);
        Assert.Equal((ushort)17, patched.Critical!.Damage);
        Assert.Equal(_bothHands.FormKey, patched.EquipmentType.FormKey);
        Assert.Equal(_swingGreat.FormKey, patched.AttackSound.FormKey);
        // Kept from the stats side: value, the item's own flags, keywords that are not weapon type or material.
        Assert.Equal(500u, patched.BasicStats.Value);
        Assert.True(patched.Data.Flags.HasFlag(WeaponData.Flag.CantDrop));
        Assert.True(patched.Data.Flags.HasFlag(WeaponData.Flag.HideBackpack));
        Assert.Equal(["VendorItemWeapon", "WeapTypeGreatsword", "WeapMaterialIron"], KeywordNames(patched));

        var change = result.Changes.Single(item => item.FormKey == _unique.FormKey);
        Assert.Equal(new BaseItem("IronGreatsword", _ironGreatsword.FormKey, _skyrim.ModKey), change.Base);
        Assert.Equal(_stats.ModKey, change.StatsPlugin);
        Assert.Contains("weapon type from IronGreatsword", string.Join("; ", change.Fields));
        Assert.Equal(0, result.TypeSkipped);
        Assert.Contains(_log.Verboses, line => line.Contains("UniqueBlade") && line.Contains("type differs") && line.Contains("IronGreatsword"));
    }

    [Fact]
    public void The_stats_sides_material_is_missing_so_the_look_records_material_picks_the_base()
    {
        var plain = WithStatsOverride(NewWeapon(_skyrim, "PlainBlade", WeaponAnimationType.OneHandSword, 1f, 1f, 5, 5f, _rightHand, _swingSword, _sword));
        LookOverride(plain, WeaponAnimationType.TwoHandSword, _greatsword, _steel);
        Greatsword(_skyrim, "SteelGreatsword", 18, _steel);

        Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal((ushort)18, PatchedWeapon("PlainBlade").BasicStats!.Damage);
        Assert.Equal(["WeapTypeGreatsword", "WeapMaterialSteel"], KeywordNames(PatchedWeapon("PlainBlade")));
    }

    [Fact]
    public void A_record_a_look_plugin_introduced_is_never_the_base_so_without_another_one_the_item_is_skipped_with_a_warning()
    {
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);
        Greatsword(_look, "LookGreatsword", 99);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Empty(_patch.Weapons);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.TypeSkipped);
        var warning = Assert.Single(_log.Warnings);
        Assert.Contains("UniqueBlade", warning);
        Assert.Contains("no generic base weapon", warning);
    }

    [Fact]
    public void Without_a_generic_weapon_of_the_look_records_type_and_the_stats_sides_material_the_item_is_skipped()
    {
        var glass = _skyrim.Keywords.AddNew("WeapMaterialGlass");
        var glassBlade = WithStatsOverride(NewWeapon(_skyrim, "GlassBlade", WeaponAnimationType.OneHandSword, 1f, 1f, 12, 6f, _rightHand, _swingSword, _sword, glass));
        LookOverride(glassBlade, WeaponAnimationType.TwoHandSword, _greatsword, glass);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Empty(_patch.Weapons);
        Assert.Equal(1, result.TypeSkipped);
        Assert.Contains("GlassBlade", Assert.Single(_log.Warnings));
    }

    [Fact]
    public void The_look_records_template_is_the_base_when_it_is_a_generic_weapon_that_matches_like_any_candidate()
    {
        var chosen = Greatsword(_skyrim, "ChosenGreatsword", 18);
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron).Template.SetTo(chosen);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal((ushort)18, PatchedWeapon("UniqueBlade").BasicStats!.Damage);
        Assert.Equal("ChosenGreatsword", result.Changes.Single().Base!.EditorID);
    }

    [Theory]
    [InlineData("SteelGreatsword")]
    [InlineData("EnchantedGreatsword")]
    [InlineData("TemplatedGreatsword")]
    [InlineData("FlaggedGreatsword")]
    public void A_template_the_candidate_rules_reject_is_ignored(string templateId)
    {
        Greatsword(_skyrim, "SteelGreatsword", 18, _steel);
        var template = _skyrim.Weapons.Single(weapon => weapon.EditorID == templateId);
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron).Template.SetTo(template);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal("IronGreatsword", result.Changes.Single().Base!.EditorID);
        Assert.Equal(["VendorItemWeapon", "WeapTypeGreatsword", "WeapMaterialIron"], KeywordNames(PatchedWeapon("UniqueBlade")));
    }

    [Fact]
    public void A_dragonbone_stats_side_keeps_its_material_and_takes_a_dragonbone_base()
    {
        var bone = _skyrim.Keywords.AddNew("DLC1WeapMaterialDragonbone");
        var boneSword = WithStatsOverride(NewWeapon(_skyrim, "BoneSword", WeaponAnimationType.OneHandSword, 1f, 1f, 20, 10f, _rightHand, _swingSword, _sword, bone));
        LookOverride(boneSword, WeaponAnimationType.TwoHandSword, _greatsword, _iron);
        Greatsword(_skyrim, "BoneGreatsword", 44, bone);

        Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal((ushort)44, PatchedWeapon("BoneSword").BasicStats!.Damage);
        Assert.Equal(["WeapTypeGreatsword", "DLC1WeapMaterialDragonbone"], KeywordNames(PatchedWeapon("BoneSword")));
    }

    [Theory]
    [InlineData(WeaponData.Flag.BoundWeapon)]
    [InlineData(WeaponData.Flag.PlayerOnly)]
    [InlineData(WeaponData.Flag.CantDrop)]
    [InlineData(WeaponData.Flag.NotUsedInNormalCombat)]
    public void A_bound_player_only_undroppable_or_non_combat_weapon_is_never_the_base(WeaponData.Flag flag)
    {
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        Greatsword(_skyrim, "FlaggedByType", 50).Data!.Flags = flag;
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal(1, result.TypeSkipped);
        Assert.Empty(_patch.Weapons);
        Assert.Contains("no generic base weapon", Assert.Single(_log.Warnings));
    }

    [Fact]
    public void Without_a_weapon_type_keyword_on_the_look_record_or_any_material_no_keywordless_weapon_is_taken_as_the_base()
    {
        Greatsword(_skyrim, "Plain", 60).Keywords = [];
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        // The look record names no weapon type.
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _iron);
        // Neither side names a material.
        var club = WithStatsOverride(NewWeapon(_skyrim, "Club", WeaponAnimationType.OneHandMace, 1f, 1f, 5, 5f, _rightHand, _swingSword));
        LookOverride(club, WeaponAnimationType.TwoHandSword, _greatsword);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Empty(_patch.Weapons);
        Assert.Equal(2, result.TypeSkipped);
        Assert.Equal(2, _log.Warnings.Count);
    }

    [Fact]
    public void Among_equal_candidates_one_without_scripts_wins_over_a_scripted_one_with_a_lower_id()
    {
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        var scripted = Greatsword(_skyrim, "ScriptedGreatsword", 70);
        scripted.VirtualMachineAdapter = new VirtualMachineAdapter();
        scripted.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "QuestWeaponScript" });
        Greatsword(_skyrim, "PlainGreatsword", 71);
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal("PlainGreatsword", result.Changes.Single().Base!.EditorID);
    }

    [Fact]
    public void An_item_this_run_retypes_never_becomes_the_base_of_another()
    {
        // A one-handed iron sword that a look turns into a greatsword and a greatsword that a look turns into a one-handed sword
        // would each be the other's base: both are items of this run, so neither is generic.
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        var falchion = WithStatsOverride(NewWeapon(_skyrim, "Falchion", WeaponAnimationType.OneHandSword, 1f, 1f, 9, 9f, _rightHand, _swingSword, _sword, _iron));
        LookOverride(falchion, WeaponAnimationType.TwoHandSword, _greatsword, _iron);
        var cleaver = WithStatsOverride(NewWeapon(_skyrim, "Cleaver", WeaponAnimationType.TwoHandSword, 1f, 1f, 9, 9f, _bothHands, _swingGreat, _greatsword, _iron));
        LookOverride(cleaver, WeaponAnimationType.OneHandSword, _sword, _iron);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Empty(_patch.Weapons);
        Assert.Equal(2, result.TypeSkipped);
    }

    [Fact]
    public void A_base_of_an_earlier_patcher_counts_as_it_was_before_the_run_and_its_stats_side_is_the_patchs_version()
    {
        _patch.Weapons.GetOrAddAsOverride(_unique).BasicStats!.Value = 777;
        _patch.Weapons.GetOrAddAsOverride(_ironGreatsword).BasicStats!.Damage = 50;
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        var patched = PatchedWeapon("UniqueBlade");
        Assert.Equal(777u, patched.BasicStats!.Value);
        Assert.Equal((ushort)50, patched.BasicStats.Damage);
        Assert.Equal(_skyrim.ModKey, result.Changes.Single(item => item.FormKey == _unique.FormKey).Base!.StatsPlugin);
    }

    [Fact]
    public void A_base_without_critical_data_gives_the_weapon_none_and_one_without_basic_stats_is_no_base()
    {
        _stats.Weapons.GetOrAddAsOverride(_unique).Critical = new CriticalData { Damage = 5 };
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        Greatsword(_skyrim, "NoCritGreatsword", 72);
        Greatsword(_skyrim, "NoStatsGreatsword", 73).BasicStats = null;
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        var patched = PatchedWeapon("UniqueBlade");
        Assert.Null(patched.Critical);
        Assert.Equal((ushort)72, patched.BasicStats!.Damage);
        Assert.Equal("NoCritGreatsword", result.Changes.Single().Base!.EditorID);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_template_of_a_look_plugin_or_of_another_animation_type_is_ignored(bool lookOrigin)
    {
        var template = lookOrigin
            ? Greatsword(_look, "LookGreatsword", 99)
            : NewWeapon(_skyrim, "IronSword", WeaponAnimationType.OneHandSword, 1f, 1f, 7, 9f, _rightHand, _swingSword, _sword, _iron);
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron).Template.SetTo(template);

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        Assert.Equal("IronGreatsword", result.Changes.Single().Base!.EditorID);
    }

    [Fact]
    public void Among_generic_candidates_a_vanilla_master_wins_over_an_earlier_mod_then_the_earliest_plugin_then_the_lowest_form_id()
    {
        _skyrim.Weapons.Remove(_ironGreatsword.FormKey);
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);
        var early = TestPlugins.NewMod("EarlyMod.esp");
        var late = TestPlugins.NewMod("LateMod.esp");
        var dragonborn = TestPlugins.NewMod("Dragonborn.esm");
        Greatsword(early, "EarlyGreatsword", 31);
        Greatsword(late, "LateGreatsword", 32);
        Greatsword(dragonborn, "DragonbornGreatsword", 33);
        ISkyrimModGetter[] order = [early, _skyrim, late, _stats, dragonborn, _look];

        Assert.Equal("DragonbornGreatsword", Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType, loadOrder: order).Changes.Single().Base!.EditorID);

        // Without the vanilla one, the plugin earliest in the load order.
        var secondPatch = TestPlugins.NewMod("Patch.esp");
        dragonborn.Weapons.Clear();
        var result = LookMerger.Merge(order, secondPatch, [new LookSource(_look, null)], false, false, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);
        Assert.Equal("EarlyGreatsword", result.Changes.Single().Base!.EditorID);

        // Within one plugin, the lowest FormID.
        early.Weapons.Clear();
        late.Weapons.Clear();
        Greatsword(late, "LateFirst", 34);
        Greatsword(late, "LateSecond", 35);
        var thirdPatch = TestPlugins.NewMod("Patch.esp");
        result = LookMerger.Merge(order, thirdPatch, [new LookSource(_look, null)], false, false, TypeDiscrepancyHandling.TakeVisualModWeaponType, _log);
        Assert.Equal("LateFirst", result.Changes.Single().Base!.EditorID);
    }

    [Fact]
    public void A_look_plugin_that_is_the_only_override_of_the_base_item_supplies_its_stats_and_is_credited()
    {
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);
        var lookGreatsword = _look.Weapons.GetOrAddAsOverride(_ironGreatsword);
        lookGreatsword.BasicStats = new WeaponBasicStats { Value = 10, Weight = 14f, Damage = 30 };

        var result = Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType);

        var patched = PatchedWeapon("UniqueBlade");
        Assert.Equal((ushort)30, patched.BasicStats!.Damage);
        Assert.Equal(14f, patched.BasicStats.Weight);
        Assert.Equal(new BaseItem("IronGreatsword", _ironGreatsword.FormKey, _look.ModKey), result.Changes.Single(item => item.FormKey == _unique.FormKey).Base);
    }

    [Fact]
    public void With_merging_on_the_patch_stays_self_contained_when_the_base_stats_point_into_the_look_plugin()
    {
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);
        var lookGreatsword = _look.Weapons.GetOrAddAsOverride(_ironGreatsword);
        lookGreatsword.AttackSound.SetTo(_look.SoundDescriptors.AddNew("LookSwing"));

        Merge(TypeDiscrepancyHandling.TakeVisualModWeaponType, selfContained: true);

        // The stats side's own sound is kept, the base's data otherwise taken.
        var patched = PatchedWeapon("UniqueBlade");
        Assert.Equal(_swingSword.FormKey, patched.AttackSound.FormKey);
        Assert.Equal(WeaponAnimationType.TwoHandSword, patched.Data!.AnimationType);
        Assert.Contains(_log.Warnings, warning => warning.Contains("UniqueBlade") && warning.Contains("AttackSound"));
        Assert.DoesNotContain(
            _patch.EnumerateMajorRecords().SelectMany(record => record.EnumerateFormLinks().Select(link => link.FormKey).Append(record.FormKey)),
            formKey => formKey.ModKey == _look.ModKey);
    }

    [Fact]
    public void Taking_the_modlist_type_keeps_the_stats_side_and_forwards_the_look_anyway()
    {
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);

        var result = Merge(TypeDiscrepancyHandling.TakeModlistItemType);

        var patched = PatchedWeapon("UniqueBlade");
        Assert.Equal(@"Weapons\Look\UniqueBlade.nif", patched.Model?.File.GivenPath);
        Assert.Equal(WeaponAnimationType.OneHandSword, patched.Data!.AnimationType);
        Assert.Equal(1f, patched.Data.Speed);
        Assert.Equal((ushort)8, patched.BasicStats!.Damage);
        Assert.Equal(["WeapTypeSword", "WeapMaterialIron", "VendorItemWeapon"], KeywordNames(patched));
        Assert.Null(result.Changes.Single().Base);
        Assert.Contains(_log.Verboses, line => line.Contains("UniqueBlade") && line.Contains("type differs"));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Skipping_leaves_the_item_unchanged_and_logs_one_info_line_naming_it_both_types_and_the_plugins()
    {
        LookOverride(_unique, WeaponAnimationType.TwoHandSword, _greatsword, _iron);

        var result = Merge(TypeDiscrepancyHandling.SkipMerging);

        Assert.Empty(_patch.Weapons);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.TypeSkipped);
        var line = Assert.Single(_log.Infos);
        Assert.Contains("UniqueBlade", line);
        Assert.Contains("OneHandSword", line);
        Assert.Contains("TwoHandSword", line);
        Assert.Contains("Look.esp", line);
        Assert.Contains("Stats.esp", line);
    }

    [Fact]
    public void A_look_that_only_changes_the_name_is_no_discrepancy()
    {
        var look = _look.Weapons.GetOrAddAsOverride(_unique);
        look.Name = "Renamed";
        look.Data = new WeaponData { AnimationType = WeaponAnimationType.Bow };

        var result = LookMerger.Merge(LoadOrder, _patch, [new LookSource(_look, null)], forwardNames: true, false, TypeDiscrepancyHandling.SkipMerging, _log);

        Assert.Equal("Renamed", PatchedWeapon("UniqueBlade").Name?.String);
        Assert.Equal(0, result.TypeSkipped);
    }

    [Fact]
    public void A_weapon_that_the_look_plugin_changes_to_the_same_type_is_merged_as_before()
    {
        var look = LookOverride(_unique, WeaponAnimationType.OneHandSword, _sword, _iron);
        look.Data!.Speed = 1f;

        var result = Merge(TypeDiscrepancyHandling.SkipMerging);

        Assert.Equal(@"Weapons\Look\UniqueBlade.nif", PatchedWeapon("UniqueBlade").Model?.File.GivenPath);
        Assert.Null(result.Changes.Single().Base);
        Assert.Empty(_log.Infos);
    }

    [Theory]
    [InlineData(TypeDiscrepancyHandling.TakeVisualModWeaponType)]
    [InlineData(TypeDiscrepancyHandling.TakeModlistItemType)]
    [InlineData(TypeDiscrepancyHandling.SkipMerging)]
    public void Wide_discrepancies_leave_every_offending_item_unchanged_with_an_error_each_whatever_the_setting(TypeDiscrepancyHandling handling)
    {
        // Melee to bow, arrow to bolt, body armor to shield, and a weapon overridden as armor.
        LookOverride(_unique, WeaponAnimationType.Bow, _iron);
        var arrow = _skyrim.Ammunitions.AddNew("IronArrow");
        arrow.Flags = Ammunition.Flag.NonBolt;
        arrow.Model = new Model { File = @"Ammo\arrow.nif" };
        _stats.Ammunitions.GetOrAddAsOverride(arrow).Value = 3;
        var lookArrow = _look.Ammunitions.GetOrAddAsOverride(arrow);
        lookArrow.Flags = 0;
        lookArrow.Model = new Model { File = @"Ammo\bolt.nif" };
        var cuirass = _skyrim.Armors.AddNew("Cuirass");
        cuirass.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.LightArmor };
        _stats.Armors.GetOrAddAsOverride(cuirass).ArmorRating = 30;
        var lookShield = _look.Armors.GetOrAddAsOverride(cuirass);
        lookShield.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Shield, ArmorType = ArmorType.LightArmor };
        lookShield.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = @"Armor\shield.nif" } }, null);
        _look.Armors.Add(new Armor(_ironGreatsword.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "NotAWeapon" });

        var result = Merge(handling);

        Assert.Empty(result.Changes);
        Assert.Empty(_patch.Weapons);
        Assert.Empty(_patch.Ammunitions);
        Assert.Empty(_patch.Armors);
        Assert.Equal(4, result.TypeSkipped);
        Assert.Equal(4, _log.Errors.Count);
        var weapon = Assert.Single(_log.Errors, line => line.Contains("WEAP UniqueBlade"));
        Assert.Contains("OneHandSword", weapon);
        Assert.Contains("Bow", weapon);
        Assert.Contains("Look.esp", weapon);
        Assert.Contains("Stats.esp", weapon);
        Assert.Single(_log.Errors, line => line.Contains("IronArrow") && line.Contains("Arrow") && line.Contains("Bolt"));
        Assert.Single(_log.Errors, line => line.Contains("Cuirass") && line.Contains("Body") && line.Contains("Shield"));
        Assert.Single(_log.Errors, line => line.Contains("defines it as a Weapon") && line.Contains("ARMO"));
    }

    [Theory]
    [InlineData(TypeDiscrepancyHandling.TakeVisualModWeaponType, true)]
    [InlineData(TypeDiscrepancyHandling.TakeModlistItemType, true)]
    [InlineData(TypeDiscrepancyHandling.SkipMerging, false)]
    public void An_armor_type_discrepancy_keeps_the_modlist_type_unless_the_item_is_skipped(TypeDiscrepancyHandling handling, bool forwarded)
    {
        var cuirass = _skyrim.Armors.AddNew("Cuirass");
        cuirass.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.LightArmor };
        cuirass.ArmorRating = 25;
        _stats.Armors.GetOrAddAsOverride(cuirass).ArmorRating = 30;
        var lookCuirass = _look.Armors.GetOrAddAsOverride(cuirass);
        lookCuirass.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.HeavyArmor };
        lookCuirass.WorldModel = new GenderedItem<ArmorModel?>(new ArmorModel { Model = new Model { File = @"Armor\Heavy\cuirass.nif" } }, null);

        var result = Merge(handling);

        Assert.Equal(forwarded, _patch.Armors.Any());
        Assert.Equal(forwarded ? 0 : 1, result.TypeSkipped);
        if (!forwarded) return;
        var patched = _patch.Armors.Single();
        Assert.Equal(ArmorType.LightArmor, patched.BodyTemplate!.ArmorType);
        Assert.Equal(@"Armor\Heavy\cuirass.nif", patched.WorldModel?.Male?.Model?.File.GivenPath);
        Assert.Null(result.Changes.Single().Base);
        // Never one Info line per armor: a count in the result, the item only with Verbose logging.
        Assert.Empty(_log.Infos);
        Assert.Equal(handling == TypeDiscrepancyHandling.TakeVisualModWeaponType ? 1 : 0, result.ArmorTypeKept);
        Assert.Contains(_log.Verboses, line => line.Contains("Cuirass") && line.Contains("Light") && line.Contains("Heavy"));
    }

    [Fact]
    public void A_whole_record_forward_has_no_stats_side_to_clash_with()
    {
        var mace = NewWeapon(_skyrim, "LoneMace", WeaponAnimationType.OneHandMace, 1f, 1f, 5, 5f, _rightHand, _swingSword, _iron);
        LookOverride(mace, WeaponAnimationType.Bow, _iron);

        var result = Merge(TypeDiscrepancyHandling.SkipMerging);

        Assert.True(result.Changes.Single().WholeRecord);
        Assert.Equal(WeaponAnimationType.Bow, PatchedWeapon("LoneMace").Data!.AnimationType);
    }
}
