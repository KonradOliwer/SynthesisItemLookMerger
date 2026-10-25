using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>One supported item record type, with typed access to its group in a plugin and in the patch.</summary>
internal sealed class ItemType
{
    private readonly Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> _find;
    private readonly Func<ISkyrimMod, IMajorRecordGetter, IMajorRecord> _getOrAddAsOverride;
    private readonly Func<ISkyrimMod, IMajorRecordGetter, IMajorRecord> _forwardWhole;

    private ItemType(
        string signature,
        Type getterType,
        Func<ISkyrimModGetter, IEnumerable<IMajorRecordGetter>> records,
        Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> find,
        Func<ISkyrimMod, IMajorRecordGetter, IMajorRecord> getOrAddAsOverride,
        Func<ISkyrimMod, IMajorRecordGetter, IMajorRecord> forwardWhole)
    {
        Signature = signature;
        GetterType = getterType;
        Records = records;
        _find = find;
        _getOrAddAsOverride = getOrAddAsOverride;
        _forwardWhole = forwardWhole;
    }

    /// <summary>The record signature, e.g. "ARMO".</summary>
    public string Signature { get; }

    public Type GetterType { get; }

    /// <summary>All records of this type in a plugin.</summary>
    public Func<ISkyrimModGetter, IEnumerable<IMajorRecordGetter>> Records { get; }

    public IMajorRecordGetter? Find(ISkyrimModGetter mod, FormKey formKey) => _find(mod, formKey);

    /// <summary>The patch's override of <paramref name="winner"/>, added as a copy of it when the patch has none yet.</summary>
    public IMajorRecord GetOrAddAsOverride(ISkyrimMod patch, IMajorRecordGetter winner) => _getOrAddAsOverride(patch, winner);

    /// <summary>Puts a full copy of <paramref name="source"/> into the patch, replacing any version the patch already has.</summary>
    public IMajorRecord ForwardWhole(ISkyrimMod patch, IMajorRecordGetter source) => _forwardWhole(patch, source);

    /// <summary>ARMO, WEAP, AMMO, MISC, BOOK, INGR, ALCH, SLGM, KEYM, in that order.</summary>
    public static readonly IReadOnlyList<ItemType> All =
    [
        Of<Armor, IArmorGetter>("ARMO", mod => mod.Armors, patch => patch.Armors),
        Of<Weapon, IWeaponGetter>("WEAP", mod => mod.Weapons, patch => patch.Weapons),
        Of<Ammunition, IAmmunitionGetter>("AMMO", mod => mod.Ammunitions, patch => patch.Ammunitions),
        Of<MiscItem, IMiscItemGetter>("MISC", mod => mod.MiscItems, patch => patch.MiscItems),
        Of<Book, IBookGetter>("BOOK", mod => mod.Books, patch => patch.Books),
        Of<Ingredient, IIngredientGetter>("INGR", mod => mod.Ingredients, patch => patch.Ingredients),
        Of<Ingestible, IIngestibleGetter>("ALCH", mod => mod.Ingestibles, patch => patch.Ingestibles),
        Of<SoulGem, ISoulGemGetter>("SLGM", mod => mod.SoulGems, patch => patch.SoulGems),
        Of<Key, IKeyGetter>("KEYM", mod => mod.Keys, patch => patch.Keys),
    ];

    /// <summary>Whether <paramref name="record"/> is one of the supported item types.</summary>
    public static bool IsItem(IMajorRecordGetter record) => All.Any(type => type.GetterType.IsInstanceOfType(record));

    private static ItemType Of<TMajor, TGetter>(
        string signature, Func<ISkyrimModGetter, IGroupGetter<TGetter>> group, Func<ISkyrimMod, IGroup<TMajor>> patchGroup)
        where TMajor : class, IMajorRecordInternal, TGetter
        where TGetter : class, IMajorRecordGetter =>
        new(
            signature,
            typeof(TGetter),
            mod => group(mod),
            (mod, formKey) => group(mod).TryGetValue(formKey, out var record) ? record : null,
            (patch, winner) => patchGroup(patch).GetOrAddAsOverride<TMajor, TGetter>((TGetter)winner),
            (patch, source) =>
            {
                var target = patchGroup(patch);
                target.Remove(source.FormKey);
                return target.GetOrAddAsOverride<TMajor, TGetter>((TGetter)source);
            });
}
