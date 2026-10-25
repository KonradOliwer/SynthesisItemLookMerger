using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>A difference in gameplay type between a look plugin's record and its stats side's record of the same FormKey.</summary>
/// <param name="Wide">
/// The two are too far apart for any merge (melee, bow, crossbow and staff; arrow and bolt; body armor, shield and jewelry): the item
/// is left unchanged and logged as an error whatever the setting says. Otherwise the setting decides.
/// </param>
internal sealed record TypeDiscrepancy(bool Wide, string StatsType, string LookType)
{
    public override string ToString() => $"stats side {StatsType}, look {LookType}";
}

/// <summary>Compares the gameplay type of two versions of an item: weapon animation type, armor type, arrow or bolt.</summary>
internal static class TypeDiscrepancies
{
    private enum WeaponClass { Melee, Bow, Crossbow, Staff }

    private enum ArmorClass { Body, Shield, Jewelry }

    /// <summary>The slots that make an armor a piece worn on the body, head, hands or feet.</summary>
    private const BipedObjectFlag BodySlots = BipedObjectFlag.Head | BipedObjectFlag.Hair | BipedObjectFlag.Body | BipedObjectFlag.Hands
        | BipedObjectFlag.Forearms | BipedObjectFlag.Feet | BipedObjectFlag.Calves | BipedObjectFlag.LongHair;

    private const BipedObjectFlag JewelrySlots = BipedObjectFlag.Amulet | BipedObjectFlag.Ring | BipedObjectFlag.Circlet | BipedObjectFlag.Ears;

    /// <summary>The discrepancy between the two versions, or null when their types agree (or the record has no type to compare).</summary>
    public static TypeDiscrepancy? Find(IMajorRecordGetter stats, IMajorRecordGetter look) => (stats, look) switch
    {
        (IWeaponGetter s, IWeaponGetter l) => Weapons(s, l),
        (IArmorGetter s, IArmorGetter l) => Armors(s, l),
        (IAmmunitionGetter s, IAmmunitionGetter l) => Ammunitions(s, l),
        _ => null,
    };

    /// <summary>A different animation type is a discrepancy (sword to axe, one hand to two hands); another weapon class is a wide one.</summary>
    private static TypeDiscrepancy? Weapons(IWeaponGetter stats, IWeaponGetter look)
    {
        if (stats.Data is not { } statsData || look.Data is not { } lookData || statsData.AnimationType == lookData.AnimationType) return null;
        return new TypeDiscrepancy(
            ClassOf(statsData.AnimationType) != ClassOf(lookData.AnimationType), statsData.AnimationType.ToString(), lookData.AnimationType.ToString());
    }

    private static WeaponClass ClassOf(WeaponAnimationType type) => type switch
    {
        WeaponAnimationType.Bow => WeaponClass.Bow,
        WeaponAnimationType.Crossbow => WeaponClass.Crossbow,
        WeaponAnimationType.Staff => WeaponClass.Staff,
        _ => WeaponClass.Melee,
    };

    /// <summary>A different equip class is a wide discrepancy; light, heavy and clothing within one class is the setting's to decide.</summary>
    private static TypeDiscrepancy? Armors(IArmorGetter stats, IArmorGetter look)
    {
        // Without a body template there are no slots and no armor type to compare.
        if (stats.BodyTemplate is not { } statsBody || look.BodyTemplate is not { } lookBody) return null;
        var (statsClass, lookClass) = (ClassOf(statsBody), ClassOf(lookBody));
        if (statsClass != lookClass) return new TypeDiscrepancy(true, statsClass.ToString(), lookClass.ToString());
        if (statsBody.ArmorType == lookBody.ArmorType) return null;
        return new TypeDiscrepancy(false, statsBody.ArmorType.ToString(), lookBody.ArmorType.ToString());
    }

    /// <summary>
    /// From the biped slots: the shield slot makes a shield; a piece that uses only amulet, ring, circlet or ears slots is jewelry;
    /// anything else (body, head, hands, feet, tail, no slot) is worn armor or clothing.
    /// </summary>
    private static ArmorClass ClassOf(IBodyTemplateGetter body)
    {
        var slots = body.FirstPersonFlags;
        if (slots.HasFlag(BipedObjectFlag.Shield)) return ArmorClass.Shield;
        return (slots & JewelrySlots) != 0 && (slots & BodySlots) == 0 ? ArmorClass.Jewelry : ArmorClass.Body;
    }

    private static TypeDiscrepancy? Ammunitions(IAmmunitionGetter stats, IAmmunitionGetter look)
    {
        var (statsIsArrow, lookIsArrow) = (stats.Flags.HasFlag(Ammunition.Flag.NonBolt), look.Flags.HasFlag(Ammunition.Flag.NonBolt));
        return statsIsArrow == lookIsArrow ? null : new TypeDiscrepancy(true, statsIsArrow ? "Arrow" : "Bolt", lookIsArrow ? "Arrow" : "Bolt");
    }
}
