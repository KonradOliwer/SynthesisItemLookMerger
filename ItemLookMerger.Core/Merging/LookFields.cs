using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Noggog;

namespace ItemLookMerger.Core.Merging;

/// <summary>
/// The look of an item: Model and Icons on every type, plus Armor.WorldModel and Armor.Armature, Weapon.ScopeModel and
/// Weapon.FirstPersonModel, Book.InventoryArt, and Name when names are forwarded. Everything else is stats and stays the winner's.
/// </summary>
internal static class LookFields
{
    /// <summary>The names of the look fields that differ; empty when both records look the same.</summary>
    public static IReadOnlyList<string> Differences(IMajorRecordGetter winner, IMajorRecordGetter source, bool forwardNames)
    {
        var differences = new List<string>();
        void Check(string field, bool same)
        {
            if (!same) differences.Add(field);
        }

        if (source is IModeledGetter sourceModeled && winner is IModeledGetter winnerModeled)
            Check("Model", SameModel(sourceModeled.Model, winnerModeled.Model));
        if (source is IHasIconsGetter sourceIcons && winner is IHasIconsGetter winnerIcons)
            Check("Icons", SameIcons(sourceIcons.Icons, winnerIcons.Icons));
        switch (source, winner)
        {
            case (IArmorGetter s, IArmorGetter w):
                Check("WorldModel", SameArmorModel(s.WorldModel?.Male, w.WorldModel?.Male) && SameArmorModel(s.WorldModel?.Female, w.WorldModel?.Female));
                Check("Armature", s.Armature.Select(link => link.FormKey).SequenceEqual(w.Armature.Select(link => link.FormKey)));
                break;
            case (IWeaponGetter s, IWeaponGetter w):
                Check("ScopeModel", SameModel(s.ScopeModel, w.ScopeModel));
                Check("FirstPersonModel", s.FirstPersonModel.FormKeyNullable == w.FirstPersonModel.FormKeyNullable);
                break;
            case (IBookGetter s, IBookGetter w):
                Check("InventoryArt", s.InventoryArt.FormKeyNullable == w.InventoryArt.FormKeyNullable);
                break;
        }
        if (forwardNames) Check("Name", NameOf(source)?.String == NameOf(winner)?.String);
        return differences;
    }

    /// <summary>Copies the look fields of <paramref name="source"/> onto <paramref name="target"/>; links keep their FormKeys.</summary>
    public static void Copy(IMajorRecord target, IMajorRecordGetter source, bool forwardNames)
    {
        if (target is IModeled targetModeled && source is IModeledGetter sourceModeled)
            targetModeled.Model = sourceModeled.Model?.DeepCopy();
        if (target is IHasIcons targetIcons && source is IHasIconsGetter sourceIcons)
            targetIcons.Icons = sourceIcons.Icons?.DeepCopy();
        switch (target, source)
        {
            case (IArmor t, IArmorGetter s):
                t.WorldModel = s.WorldModel == null
                    ? null
                    : new GenderedItem<ArmorModel?>(s.WorldModel.Male?.DeepCopy(), s.WorldModel.Female?.DeepCopy());
                t.Armature.SetTo(s.Armature.Select(link => (IFormLinkGetter<IArmorAddonGetter>)new FormLink<IArmorAddonGetter>(link.FormKey)));
                break;
            case (IWeapon t, IWeaponGetter s):
                t.ScopeModel = s.ScopeModel?.DeepCopy();
                t.FirstPersonModel.SetTo(s.FirstPersonModel.FormKeyNullable);
                break;
            case (IBook t, IBookGetter s):
                t.InventoryArt.SetTo(s.InventoryArt.FormKeyNullable);
                break;
        }
        if (!forwardNames) return;
        var name = NameOf(source);
        if (target is ITranslatedNamed named) named.Name = name == null ? null : CopyOf(name);
        else if (target is ITranslatedNamedRequired required && name != null) required.Name = CopyOf(name);
    }

    /// <summary>The asset paths written in the look fields themselves (meshes and icon textures).</summary>
    public static IEnumerable<IAssetLinkGetter> ListedAssetLinks(IMajorRecordGetter source)
    {
        var containers = new List<IAssetLinkContainerGetter?>();
        if (source is IModeledGetter modeled) containers.Add(modeled.Model);
        if (source is IHasIconsGetter icons) containers.Add(icons.Icons);
        if (source is IArmorGetter armor) containers.AddRange([armor.WorldModel?.Male, armor.WorldModel?.Female]);
        if (source is IWeaponGetter weapon) containers.Add(weapon.ScopeModel);
        return containers.WhereNotNull().SelectMany(container => container.EnumerateAssetLinks(AssetLinkQuery.Listed));
    }

    /// <summary>The record links in the look fields (alternate texture sets, armor addons, first person and inventory art statics).</summary>
    public static IEnumerable<IFormLinkGetter> FormLinks(IMajorRecordGetter source)
    {
        var containers = new List<IFormLinkContainerGetter?>();
        var links = new List<IFormLinkGetter>();
        if (source is IModeledGetter modeled) containers.Add(modeled.Model);
        switch (source)
        {
            case IArmorGetter armor:
                containers.AddRange([armor.WorldModel?.Male, armor.WorldModel?.Female]);
                links.AddRange(armor.Armature);
                break;
            case IWeaponGetter weapon:
                containers.Add(weapon.ScopeModel);
                links.Add(weapon.FirstPersonModel);
                break;
            case IBookGetter book:
                links.Add(book.InventoryArt);
                break;
        }
        return containers.WhereNotNull().SelectMany(container => container.EnumerateFormLinks()).Concat(links).Where(link => !link.IsNull);
    }

    /// <summary>
    /// Whether two models show the same: same mesh (ignoring case) and the same alternate textures. The MODT hash data is ignored,
    /// as different editors save different hashes for the same mesh.
    /// </summary>
    public static bool SameModel(IModelGetter? a, IModelGetter? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return SamePath(a.File, b.File)
            && (a.AlternateTextures ?? []).Select(texture => (texture.Name, texture.Index, texture.NewTexture.FormKey))
                .SequenceEqual((b.AlternateTextures ?? []).Select(texture => (texture.Name, texture.Index, texture.NewTexture.FormKey)));
    }

    public static bool SameIcons(IIconsGetter? a, IIconsGetter? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return SamePath(a.LargeIconFilename, b.LargeIconFilename) && SamePath(a.SmallIconFilename, b.SmallIconFilename);
    }

    private static bool SameArmorModel(IArmorModelGetter? a, IArmorModelGetter? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return SameModel(a.Model, b.Model) && SameIcons(a.Icons, b.Icons);
    }

    public static bool SamePath(IAssetLinkGetter? a, IAssetLinkGetter? b) =>
        string.Equals(a?.GivenPath ?? string.Empty, b?.GivenPath ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    public static ITranslatedStringGetter? NameOf(IMajorRecordGetter record) => record switch
    {
        ITranslatedNamedGetter named => named.Name,
        ITranslatedNamedRequiredGetter required => required.Name,
        _ => null,
    };

    private static TranslatedString CopyOf(ITranslatedStringGetter name) =>
        name is TranslatedString text ? text.DeepCopy() : new TranslatedString(name.TargetLanguage, name.String);
}
