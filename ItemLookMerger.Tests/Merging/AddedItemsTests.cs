using ItemLookMerger.Core;
using ItemLookMerger.Core.Credits;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Tests.Support;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ItemLookMerger.Tests.Merging;

/// <summary>CopyAddedItems: the items a look plugin adds go into the patch as new records.</summary>
public class AddedItemsTests
{
    private readonly TestPlugins _plugins = new();
    private readonly TestLog _log = new();

    private MergeResult Merge(bool copyAddedItems, SkyrimMod look, bool selfContained = true) =>
        LookMerger.Merge(
            _plugins.LoadOrder(look), _plugins.Patch, [new LookSource(look, "Knight Mod")], forwardNames: false, selfContained,
            TypeDiscrepancyHandling.TakeVisualModWeaponType, _log, copyAddedItems);

    /// <summary>A look plugin that adds an armor with an addon, a texture set and a recipe, and overrides nothing.</summary>
    private static (SkyrimMod Look, Armor Armor, ArmorAddon Addon, TextureSet Texture) KnightPlugin()
    {
        var look = TestPlugins.NewMod("Knight.esp");
        var texture = look.TextureSets.AddNew("KnightTXST");
        texture.Diffuse = @"Armor\Knight\knight_d.dds";
        var addon = look.ArmorAddons.AddNew("KnightAA");
        addon.WorldModel = new GenderedItem<Model?>(
            new Model
            {
                File = @"Armor\Knight\knight_1.nif",
                AlternateTextures = [new AlternateTexture { Name = "Body", Index = 0, NewTexture = texture.ToLink() }],
            },
            null);
        var armor = look.Armors.AddNew("KnightArmor");
        armor.Name = "Knight Armor";
        armor.ArmorRating = 50;
        armor.Armature.Add(addon.ToLink());
        var recipe = look.ConstructibleObjects.AddNew("KnightRecipe");
        recipe.CreatedObject.SetTo(armor);
        return (look, armor, addon, texture);
    }

    [Fact]
    public void The_setting_is_off_by_default()
    {
        Assert.False(new MergeOptions([], false, false, true, null).CopyAddedItems);
        Assert.False(new Settings().ContentMerging.CopyAddedItems);
    }

    [Fact]
    public void Without_the_setting_an_added_armor_is_not_copied()
    {
        var (look, _, _, _) = KnightPlugin();

        var result = Merge(copyAddedItems: false, look);

        Assert.Empty(_plugins.Patch.EnumerateMajorRecords());
        Assert.Empty(result.AddedItems);
        Assert.Empty(_log.Warnings);
        Assert.Contains(_log.Infos, line => line.Contains("Knight.esp") && line.Contains("not copied because CopyAddedItems is off"));
    }

    [Fact]
    public void A_plugin_that_adds_and_overrides_nothing_still_gets_the_contributes_nothing_warning()
    {
        var look = TestPlugins.NewMod("Empty.esp");

        Merge(copyAddedItems: true, look);

        Assert.Contains(_log.Warnings, warning => warning.Contains("Empty.esp") && warning.Contains("contributes nothing"));
    }

    private MergeResult MergeAll(params SkyrimMod[] looks) =>
        LookMerger.Merge(
            _plugins.LoadOrder(looks), _plugins.Patch, looks.Select(look => new LookSource(look, null)).ToList(), forwardNames: false, selfContained: true,
            TypeDiscrepancyHandling.TakeVisualModWeaponType, _log, copyAddedItems: true);

    private static FormKey DefaultRace => new(ModKey.FromFileName("Skyrim.esm"), 0x000019);

    [Fact]
    public void A_keyword_and_a_race_list_entry_into_the_look_plugin_are_removed_not_nulled()
    {
        var (look, armor, addon, _) = KnightPlugin();
        var vanillaKeyword = _plugins.Base.Keywords.AddNew("ArmorMaterialIron");
        var lookKeyword = look.Keywords.AddNew("KnightKeyword");
        armor.Keywords = [vanillaKeyword.ToLink(), lookKeyword.ToLink()];
        addon.AdditionalRaces.Add(look.Races.AddNew("KnightRace").ToLink());

        MergeAll(look);

        Assert.Equal(vanillaKeyword.FormKey, Assert.Single(Assert.Single(_plugins.Patch.Armors).Keywords!).FormKey);
        Assert.Empty(Assert.Single(_plugins.Patch.ArmorAddons).AdditionalRaces);
        Assert.Contains(_log.Warnings, warning => warning.Contains("Keywords") && warning.Contains("removed"));
        Assert.Contains(_log.Warnings, warning => warning.Contains("AdditionalRaces") && warning.Contains("removed"));
    }

    [Fact]
    public void A_race_into_the_look_plugin_falls_back_to_the_default_race_on_armor_and_addon()
    {
        var (look, armor, addon, _) = KnightPlugin();
        var race = look.Races.AddNew("KnightRace");
        armor.Race.SetTo(race);
        addon.Race.SetTo(race);

        MergeAll(look);

        Assert.Equal(DefaultRace, Assert.Single(_plugins.Patch.Armors).Race.FormKey);
        Assert.Equal(DefaultRace, Assert.Single(_plugins.Patch.ArmorAddons).Race.FormKey);
        Assert.Contains(_log.Warnings, warning => warning.Contains("Race") && warning.Contains("DefaultRace"));
    }

    [Fact]
    public void An_effect_with_a_base_effect_of_the_look_plugin_is_removed_from_a_potion()
    {
        var look = TestPlugins.NewMod("Potion.esp");
        var vanillaEffect = _plugins.Base.MagicEffects.AddNew("RestoreHealth");
        var lookEffect = look.MagicEffects.AddNew("KnightEffect");
        var potion = look.Ingestibles.AddNew("KnightPotion");
        var kept = new Effect();
        kept.BaseEffect.SetTo(vanillaEffect);
        var dropped = new Effect();
        dropped.BaseEffect.SetTo(lookEffect);
        potion.Effects.Add(dropped);
        potion.Effects.Add(kept);

        MergeAll(look);

        var copy = Assert.Single(_plugins.Patch.Ingestibles);
        Assert.Equal(vanillaEffect.FormKey, Assert.Single(copy.Effects).BaseEffect.FormKey);
        Assert.Contains(_log.Warnings, warning => warning.Contains("Effects") && warning.Contains("removed"));
    }

    [Fact]
    public void A_potion_or_ingredient_whose_effects_all_point_at_the_look_plugin_is_not_copied()
    {
        var look = TestPlugins.NewMod("Potion.esp");
        var lookEffect = look.MagicEffects.AddNew("KnightEffect");
        var effect = new Effect();
        effect.BaseEffect.SetTo(lookEffect);
        look.Ingestibles.AddNew("KnightPotion").Effects.Add(effect);
        var ingredientEffect = new Effect();
        ingredientEffect.BaseEffect.SetTo(lookEffect);
        look.Ingredients.AddNew("KnightHerb").Effects.Add(ingredientEffect);

        var result = MergeAll(look);

        Assert.Empty(result.AddedItems);
        Assert.Empty(_plugins.Patch.Ingestibles);
        Assert.Empty(_plugins.Patch.Ingredients);
        Assert.Contains(_log.Errors, error => error.Contains("KnightPotion") && error.Contains("Effects"));
        Assert.Contains(_log.Errors, error => error.Contains("KnightHerb") && error.Contains("Effects"));
    }

    [Fact]
    public void A_cleared_weapon_enchantment_also_clears_the_enchantment_amount()
    {
        var look = TestPlugins.NewMod("Blade.esp");
        var weapon = look.Weapons.AddNew("KnightBlade");
        weapon.Model = new Model { File = @"Weapons\Knight\blade.nif" };
        weapon.ObjectEffect.SetTo(look.ObjectEffects.AddNew("KnightEnch"));
        weapon.EnchantmentAmount = 300;

        MergeAll(look);

        var copy = Assert.Single(_plugins.Patch.Weapons);
        Assert.True(copy.ObjectEffect.IsNull);
        Assert.Null(copy.EnchantmentAmount);
        Assert.Contains(_log.Warnings, warning => warning.Contains("ObjectEffect") && warning.Contains("enchantment amount"));
    }

    [Fact]
    public void A_cleared_armor_enchantment_also_clears_the_enchantment_amount()
    {
        var (look, armor, _, _) = KnightPlugin();
        armor.ObjectEffect.SetTo(look.ObjectEffects.AddNew("KnightEnch"));
        armor.EnchantmentAmount = 120;

        MergeAll(look);

        var copy = Assert.Single(_plugins.Patch.Armors);
        Assert.True(copy.ObjectEffect.IsNull);
        Assert.Null(copy.EnchantmentAmount);
    }

    [Fact]
    public void A_weapon_and_an_ammunition_added_by_a_look_plugin_get_their_impact_and_projectile_links_remapped()
    {
        var look = TestPlugins.NewMod("Bow.esp");
        var impacts = look.ImpactDataSets.AddNew("KnightImpacts");
        var weapon = look.Weapons.AddNew("KnightBlade");
        weapon.Model = new Model { File = @"Weapons\Knight\blade.nif" };
        weapon.ImpactDataSet.SetTo(impacts);
        var projectile = look.Projectiles.AddNew("KnightProjectile");
        projectile.Model = new Model { File = @"Weapons\Knight\arrow.nif" };
        var ammo = look.Ammunitions.AddNew("KnightArrow");
        ammo.Projectile.SetTo(projectile);

        var result = MergeAll(look);

        var weaponCopy = Assert.Single(_plugins.Patch.Weapons);
        var impactsCopy = Assert.Single(_plugins.Patch.ImpactDataSets);
        Assert.Equal(impactsCopy.FormKey, weaponCopy.ImpactDataSet.FormKey);
        var ammoCopy = Assert.Single(_plugins.Patch.Ammunitions);
        var projectileCopy = Assert.Single(_plugins.Patch.Projectiles);
        Assert.Equal(projectileCopy.FormKey, ammoCopy.Projectile.FormKey);
        Assert.Equal(2, result.AddedItems.Count);
        Assert.Contains(@"Meshes\Weapons\Knight\arrow.nif", result.Assets.Select(asset => asset.Path));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void An_ammunition_whose_projectile_the_patch_cannot_carry_is_not_copied_with_an_error()
    {
        var look = TestPlugins.NewMod("Bow.esp");
        var ammo = look.Ammunitions.AddNew("KnightArrow");
        ammo.Projectile.SetTo(new FormKey(look.ModKey, 0x999));

        var result = MergeAll(look);

        Assert.Empty(result.AddedItems);
        Assert.Empty(_plugins.Patch.Ammunitions);
        Assert.Contains(_log.Errors, error => error.Contains("KnightArrow") && error.Contains("Projectile") && error.Contains("not copied"));
    }

    [Fact]
    public void An_item_in_two_look_plugins_is_copied_once_from_the_later_one()
    {
        var (first, armor, _, _) = KnightPlugin();
        var second = TestPlugins.NewMod("Knight2.esp");
        second.Armors.GetOrAddAsOverride(armor).ArmorRating = 60;

        var result = MergeAll(first, second);

        var copy = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal(60f, copy.ArmorRating);
        var added = Assert.Single(result.AddedItems);
        Assert.Equal(second.ModKey, added.Supplier);
        Assert.Contains(first.ModKey, added.LookPlugins);
        Assert.Contains(_log.Infos, line => line.StartsWith("Knight2.esp") && line.Contains("carries 1 added item(s)"));
        Assert.DoesNotContain(_log.Warnings, warning => warning.Contains("contributes nothing"));
    }

    [Fact]
    public void Female_and_both_weight_meshes_of_an_added_armors_addon_are_collected()
    {
        var (look, _, addon, _) = KnightPlugin();
        addon.WorldModel = new GenderedItem<Model?>(
            new Model { File = @"Armor\Knight\knight_m_1.nif" }, new Model { File = @"Armor\Knight\knight_f_1.nif" });

        var result = MergeAll(look);

        var paths = result.Assets.Select(asset => asset.Path).ToList();
        Assert.Contains(@"Meshes\Armor\Knight\knight_m_1.nif", paths);
        Assert.Contains(@"Meshes\Armor\Knight\knight_f_1.nif", paths);
        Assert.Contains(@"Meshes\Armor\Knight\knight_m_0.nif", paths);
        Assert.Contains(@"Meshes\Armor\Knight\knight_f_0.nif", paths);
    }

    [Fact]
    public void A_copy_whose_name_would_be_the_still_enabled_originals_gets_the_clash_suffix()
    {
        var (look, armor, _, _) = KnightPlugin();
        armor.EditorID = "KnightArmor_ILM";

        MergeAll(look);

        var name = Assert.Single(_plugins.Patch.Armors).EditorID;
        Assert.NotEqual("KnightArmor_ILM", name);
        Assert.StartsWith("KnightArmor_ILM_Knight_", name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_winner_with_another_weapon_type_than_the_look_is_not_copied_when_the_change_is_wide()
    {
        var look = TestPlugins.NewMod("Blade.esp");
        var sword = look.Weapons.AddNew("KnightBlade");
        sword.Model = new Model { File = @"Weapons\Knight\blade.nif" };
        sword.Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword };
        var balance = TestPlugins.NewMod("Balance.esp");
        var winner = balance.Weapons.GetOrAddAsOverride(sword);
        winner.Data = new WeaponData { AnimationType = WeaponAnimationType.Bow };
        winner.Model = new Model { File = @"Weapons\Other\bow.nif" };

        var result = LookMerger.Merge(
            [_plugins.Base, _plugins.Stats, look, balance], _plugins.Patch, [new LookSource(look, null)], forwardNames: false, selfContained: true,
            TypeDiscrepancyHandling.TakeVisualModWeaponType, _log, copyAddedItems: true);

        Assert.Empty(result.AddedItems);
        Assert.Empty(_plugins.Patch.Weapons);
        Assert.Contains(_log.Errors, error => error.Contains("type differs"));
    }

    [Fact]
    public void An_earlier_patchers_version_of_an_added_item_is_the_stats_side_and_replaces_the_override()
    {
        var (look, armor, _, _) = KnightPlugin();
        var earlier = _plugins.Patch.Armors.GetOrAddAsOverride(armor);
        earlier.ArmorRating = 88;

        var result = MergeAll(look);

        var copy = Assert.Single(_plugins.Patch.Armors);
        Assert.NotEqual(armor.FormKey, copy.FormKey);
        Assert.Equal(88f, copy.ArmorRating);
        var added = Assert.Single(result.AddedItems);
        Assert.Null(added.Winner);
        Assert.True(added.StatsFromPatch);
        Assert.Contains(_log.Infos, line => line.Contains("the copy replaces that override"));
    }

    [Fact]
    public void An_earlier_patchers_override_stays_when_the_type_discrepancy_skips_the_item()
    {
        var look = TestPlugins.NewMod("Blade.esp");
        var sword = look.Weapons.AddNew("KnightBlade");
        sword.Model = new Model { File = @"Weapons\Knight\blade.nif" };
        sword.Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword };
        var earlier = _plugins.Patch.Weapons.GetOrAddAsOverride(sword);
        earlier.Data = new WeaponData { AnimationType = WeaponAnimationType.Bow };
        earlier.Model = new Model { File = @"Weapons\Other\bow.nif" };

        // The override is left as it is, so the self-contained check refuses the run: it names the record that still needs the plugin.
        Assert.Throws<RunFailedException>(() => MergeAll(look));

        Assert.Equal(sword.FormKey, Assert.Single(_plugins.Patch.Weapons).FormKey);
        Assert.Contains(_log.Errors, error => error.Contains("type differs"));
        Assert.DoesNotContain(_log.Infos, line => line.Contains("the copy replaces that override"));
    }

    [Fact]
    public void An_earlier_patchers_override_stays_when_a_required_link_skips_the_item()
    {
        var look = TestPlugins.NewMod("Bow.esp");
        var ammo = look.Ammunitions.AddNew("KnightArrow");
        ammo.Projectile.SetTo(new FormKey(look.ModKey, 0x999));
        _plugins.Patch.Ammunitions.GetOrAddAsOverride(ammo).Damage = 9;

        Assert.Throws<RunFailedException>(() => MergeAll(look));

        Assert.Equal(ammo.FormKey, Assert.Single(_plugins.Patch.Ammunitions).FormKey);
        Assert.Contains(_log.Errors, error => error.Contains("KnightArrow") && error.Contains("Projectile") && error.Contains("not copied"));
        Assert.DoesNotContain(_log.Infos, line => line.Contains("the copy replaces that override"));
    }

    [Fact]
    public void A_skipped_ammunition_does_not_keep_its_editor_id_for_the_next_copy()
    {
        var look = TestPlugins.NewMod("Bow.esp");
        var broken = look.Ammunitions.AddNew("KnightArrow");
        broken.Projectile.SetTo(new FormKey(look.ModKey, 0x999));
        var projectile = look.Projectiles.AddNew("KnightProjectile");
        projectile.Model = new Model { File = @"Weapons\Knight\arrow.nif" };
        var other = TestPlugins.NewMod("Bow2.esp");
        var good = other.Ammunitions.AddNew("KnightArrow");
        good.Projectile.SetTo(other.Projectiles.AddNew("OtherProjectile"));

        MergeAll(look, other);

        Assert.Equal("KnightArrow_ILM", Assert.Single(_plugins.Patch.Ammunitions).EditorID);
    }

    [Fact]
    public void A_winner_with_a_narrow_type_difference_is_not_copied_with_skip_merging()
    {
        var (look, armor, _, _) = KnightPlugin();
        armor.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.LightArmor };
        var balance = TestPlugins.NewMod("Balance.esp");
        var winner = balance.Armors.GetOrAddAsOverride(armor);
        winner.BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, ArmorType = ArmorType.HeavyArmor };
        winner.Armature.Clear();

        var result = LookMerger.Merge(
            [_plugins.Base, _plugins.Stats, look, balance], _plugins.Patch, [new LookSource(look, null)], forwardNames: false, selfContained: true,
            TypeDiscrepancyHandling.SkipMerging, _log, copyAddedItems: true);

        Assert.Empty(result.AddedItems);
        Assert.Empty(_plugins.Patch.Armors);
        Assert.Contains(_log.Infos, line => line.Contains("type differs") && line.Contains("not changed"));
    }

    [Fact]
    public void An_added_item_supplied_by_another_plugin_still_counts_as_carried_for_the_adding_plugin()
    {
        var first = TestPlugins.NewMod("Plain.esp");
        var armor = first.Armors.AddNew("PlainArmor");
        armor.ArmorRating = 10;
        var second = TestPlugins.NewMod("Plain2.esp");
        second.Armors.GetOrAddAsOverride(armor).ArmorRating = 20;

        var result = MergeAll(first, second);

        Assert.Equal(second.ModKey, Assert.Single(result.AddedItems).Supplier);
        Assert.Contains(_log.Infos, line => line.StartsWith("Plain.esp") && line.Contains("carries 1 added item(s)"));
        Assert.DoesNotContain(_log.Infos, line => line.Contains("none of its"));
    }

    [Fact]
    public void A_copy_never_takes_the_editor_id_of_the_original_when_the_winners_name_differs()
    {
        var (look, armor, _, _) = KnightPlugin();
        armor.EditorID = "KnightArmor_ILM";
        var balance = TestPlugins.NewMod("Balance.esp");
        var winner = balance.Armors.GetOrAddAsOverride(armor);
        winner.EditorID = "KnightArmor";
        winner.ArmorRating = 77;

        LookMerger.Merge(
            [_plugins.Base, _plugins.Stats, look, balance], _plugins.Patch, [new LookSource(look, null)], forwardNames: false, selfContained: true,
            TypeDiscrepancyHandling.TakeVisualModWeaponType, _log, copyAddedItems: true);

        var name = Assert.Single(_plugins.Patch.Armors).EditorID;
        Assert.NotEqual("KnightArmor_ILM", name);
        Assert.StartsWith("KnightArmor_ILM_", name, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ingredient_keeps_its_effects_of_vanilla_and_loses_those_of_the_look_plugin()
    {
        var look = TestPlugins.NewMod("Herb.esp");
        var vanillaEffect = _plugins.Base.MagicEffects.AddNew("RestoreHealth");
        var herb = look.Ingredients.AddNew("KnightHerb");
        var dropped = new Effect();
        dropped.BaseEffect.SetTo(look.MagicEffects.AddNew("KnightEffect"));
        var kept = new Effect();
        kept.BaseEffect.SetTo(vanillaEffect);
        herb.Effects.Add(dropped);
        herb.Effects.Add(kept);

        MergeAll(look);

        Assert.Equal(vanillaEffect.FormKey, Assert.Single(Assert.Single(_plugins.Patch.Ingredients).Effects).BaseEffect.FormKey);
        Assert.Contains(_log.Warnings, warning => warning.Contains("Effects") && warning.Contains("removed"));
    }

    [Fact]
    public void Miscellaneous_items_books_soul_gems_and_keys_are_copied()
    {
        var look = TestPlugins.NewMod("Things.esp");
        look.MiscItems.AddNew("KnightCoin");
        look.Books.AddNew("KnightBook");
        look.SoulGems.AddNew("KnightGem");
        look.Keys.AddNew("KnightKey");

        var result = MergeAll(look);

        Assert.Equal(4, result.AddedItems.Count);
        Assert.Equal("KnightCoin_ILM", Assert.Single(_plugins.Patch.MiscItems).EditorID);
        Assert.Equal("KnightBook_ILM", Assert.Single(_plugins.Patch.Books).EditorID);
        Assert.Equal("KnightGem_ILM", Assert.Single(_plugins.Patch.SoulGems).EditorID);
        Assert.Equal("KnightKey_ILM", Assert.Single(_plugins.Patch.Keys).EditorID);
    }

    [Fact]
    public void An_added_armor_is_copied_with_its_addon_and_texture_set_and_the_links_point_at_the_copies()
    {
        var (look, armor, addon, texture) = KnightPlugin();

        var result = Merge(copyAddedItems: true, look);

        var armorCopy = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal(_plugins.Patch.ModKey, armorCopy.FormKey.ModKey);
        Assert.NotEqual(armor.FormKey, armorCopy.FormKey);
        Assert.Equal("KnightArmor_ILM", armorCopy.EditorID);
        Assert.Equal(50f, armorCopy.ArmorRating);
        var addonCopy = Assert.Single(_plugins.Patch.ArmorAddons);
        Assert.Equal(addonCopy.FormKey, Assert.Single(armorCopy.Armature).FormKey);
        var textureCopy = Assert.Single(_plugins.Patch.TextureSets);
        Assert.Equal(textureCopy.FormKey, Assert.Single(addonCopy.WorldModel!.Male!.AlternateTextures!).NewTexture.FormKey);
        Assert.NotEqual(addon.FormKey, addonCopy.FormKey);
        Assert.NotEqual(texture.FormKey, textureCopy.FormKey);

        // The recipe is not an item: it is not copied and is named among what disabling the plugin drops.
        Assert.Equal(3, _plugins.Patch.EnumerateMajorRecords().Count());
        var added = Assert.Single(result.AddedItems);
        Assert.Equal((armor.FormKey, armorCopy.FormKey, (ModKey?)null), (added.Original, added.Copy, added.Winner));
        Assert.Contains(_log.Infos, line => line.Contains("KnightArmor") && line.Contains(armor.FormKey.ToString()) && line.EndsWith($"copied as {armorCopy.FormKey}."));
        Assert.Contains(_log.Infos, line => line.Contains("carries 1 added item(s)") && line.Contains("1 ConstructibleObject"));
        Assert.Contains(@"Meshes\Armor\Knight\knight_1.nif", result.Assets.Select(asset => asset.Path));
        Assert.Contains(@"Textures\Armor\Knight\knight_d.dds", result.Assets.Select(asset => asset.Path));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void With_merging_off_the_setting_is_ignored_and_nothing_is_copied()
    {
        var (look, _, _, _) = KnightPlugin();

        var result = Merge(copyAddedItems: true, look, selfContained: false);

        Assert.Empty(result.AddedItems);
        Assert.Empty(_plugins.Patch.Armors);
    }

    [Fact]
    public void A_template_link_between_two_added_armors_points_at_the_copy()
    {
        var (look, armor, _, _) = KnightPlugin();
        var enchanted = look.Armors.AddNew("KnightArmorEnchanted");
        enchanted.TemplateArmor.SetTo(armor);

        Merge(copyAddedItems: true, look);

        var copy = Assert.Single(_plugins.Patch.Armors, record => record.EditorID == "KnightArmorEnchanted_ILM");
        var template = Assert.Single(_plugins.Patch.Armors, record => record.EditorID == "KnightArmor_ILM");
        Assert.Equal(template.FormKey, copy.TemplateArmor.FormKey);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void A_link_to_an_enchantment_the_look_plugin_adds_is_cleared_with_a_warning()
    {
        var (look, armor, _, _) = KnightPlugin();
        var enchantment = look.ObjectEffects.AddNew("KnightEnch");
        armor.ObjectEffect.SetTo(enchantment);

        Merge(copyAddedItems: true, look);

        var copy = Assert.Single(_plugins.Patch.Armors);
        Assert.True(copy.ObjectEffect.IsNull);
        Assert.Contains(_log.Warnings, warning => warning.Contains("ObjectEffect") && warning.Contains("cleared"));
        // The final check passed: nothing in the patch points at the look plugin.
        Assert.DoesNotContain(
            _plugins.Patch.EnumerateMajorRecords().SelectMany(record => record.EnumerateFormLinks().Select(link => link.FormKey).Append(record.FormKey)),
            formKey => formKey.ModKey == look.ModKey);
    }

    [Fact]
    public void An_added_armor_overridden_by_a_stats_plugin_takes_the_winners_stats_and_warns()
    {
        var (look, armor, _, _) = KnightPlugin();
        var balance = TestPlugins.NewMod("Balance.esp");
        var winner = balance.Armors.GetOrAddAsOverride(armor);
        winner.ArmorRating = 77;
        winner.Value = 900;
        // The balance plugin masters the look plugin, so it is loaded after it.
        var result = LookMerger.Merge(
            [_plugins.Base, _plugins.Stats, look, balance], _plugins.Patch, [new LookSource(look, null)], forwardNames: false, selfContained: true,
            TypeDiscrepancyHandling.TakeVisualModWeaponType, _log, copyAddedItems: true);

        var copy = Assert.Single(_plugins.Patch.Armors);
        Assert.Equal(77f, copy.ArmorRating);
        Assert.Equal(900u, copy.Value);
        Assert.Equal(Assert.Single(_plugins.Patch.ArmorAddons).FormKey, Assert.Single(copy.Armature).FormKey);
        Assert.Equal(balance.ModKey, Assert.Single(result.AddedItems).Winner);
        Assert.Contains(_log.Warnings, warning => warning.Contains("Balance.esp") && warning.Contains("still needs Knight.esp as a master"));
    }

    [Fact]
    public void Credits_name_an_earlier_patcher_as_the_stats_source_when_its_version_was_copied()
    {
        var item = new AddedItem(
            "ARMO", new FormKey(ModKey.FromFileName("Knight.esp"), 0x800), new FormKey(ModKey.FromFileName("Patch.esp"), 0x801),
            "KnightArmor_ILM", "Knight Armor", [ModKey.FromFileName("Knight.esp")], null, StatsFromPatch: true);

        var text = CreditsWriter.Build([], plugin => new PluginOrigin(plugin, null, null), [item]);

        Assert.Contains("| Knight.esp | earlier patcher |", text);
    }

    [Fact]
    public void Credits_list_the_added_items_in_their_own_table()
    {
        var item = new AddedItem(
            "ARMO", new FormKey(ModKey.FromFileName("Knight.esp"), 0x800), new FormKey(ModKey.FromFileName("Patch.esp"), 0x801),
            "KnightArmor_ILM", "Knight Armor", [ModKey.FromFileName("Knight.esp")], null);

        var text = CreditsWriter.Build([], plugin => new PluginOrigin(plugin, null, null), [item]);

        Assert.Contains("## Added items", text);
        Assert.Contains("| KnightArmor_ILM | Knight Armor | `000800:Knight.esp` | `000801:Patch.esp` | Knight.esp | look plugin |", text);
    }
}
