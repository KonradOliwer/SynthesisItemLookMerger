namespace ItemLookMerger.Core;

/// <summary>
/// What to do with an item whose look plugin gives it another weapon type or armor type than its stats side has (a two-handed look on
/// a one-handed sword). The member names are what the settings file stores and what Synthesis's settings screen lists, so renaming
/// one resets the setting; the first member is the default.
/// </summary>
public enum TypeDiscrepancyHandling
{
    /// <summary>A weapon takes the type-bound stats of a generic base item of the look's type; armor keeps the modlist type.</summary>
    TakeVisualModWeaponType = 0,

    /// <summary>The stats side keeps its type and the look is forwarded anyway.</summary>
    TakeModlistItemType,

    /// <summary>The item is not changed at all.</summary>
    SkipMerging,
}
