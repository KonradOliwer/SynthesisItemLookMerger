using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;

namespace ItemLookMerger.Core.Merging;

/// <summary>
/// A record type that holds look content for items: armor addons (ARMA), texture sets (TXST), statics (STAT, first person and
/// inventory art) and projectiles (PROJ, the flying arrow). A look plugin's override of such a record is forwarded like an item's look:
/// its look fields go onto the stats side's version, the rest (ARMA races and priorities, projectile speed, ...) stays.
/// </summary>
internal sealed class LookRecordType
{
    private readonly Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> _find;
    private readonly Func<ISkyrimMod, IMajorRecordGetter, IMajorRecord> _getOrAddAsOverride;

    private LookRecordType(
        string signature,
        Type getterType,
        Func<ISkyrimModGetter, IEnumerable<IMajorRecordGetter>> records,
        Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> find,
        Func<ISkyrimMod, IMajorRecordGetter, IMajorRecord> getOrAddAsOverride)
    {
        Signature = signature;
        GetterType = getterType;
        Records = records;
        _find = find;
        _getOrAddAsOverride = getOrAddAsOverride;
    }

    public string Signature { get; }

    public Type GetterType { get; }

    public Func<ISkyrimModGetter, IEnumerable<IMajorRecordGetter>> Records { get; }

    public IMajorRecordGetter? Find(ISkyrimModGetter mod, FormKey formKey) => _find(mod, formKey);

    public IMajorRecord GetOrAddAsOverride(ISkyrimMod patch, IMajorRecordGetter winner) => _getOrAddAsOverride(patch, winner);

    public static readonly IReadOnlyList<LookRecordType> All =
    [
        Of<ArmorAddon, IArmorAddonGetter>("ARMA", mod => mod.ArmorAddons, patch => patch.ArmorAddons),
        Of<TextureSet, ITextureSetGetter>("TXST", mod => mod.TextureSets, patch => patch.TextureSets),
        Of<Static, IStaticGetter>("STAT", mod => mod.Statics, patch => patch.Statics),
        Of<Projectile, IProjectileGetter>("PROJ", mod => mod.Projectiles, patch => patch.Projectiles),
    ];

    /// <summary>The look record type a link points to, or null for any other type (look links are typed exactly).</summary>
    public static LookRecordType? ForLink(IFormLinkGetter link) => All.FirstOrDefault(type => type.GetterType == link.Type);

    /// <summary>The look fields that differ between the stats side's and the look source's version; empty when they look the same.</summary>
    public static IReadOnlyList<string> Differences(IMajorRecordGetter winner, IMajorRecordGetter source)
    {
        var differences = new List<string>();
        void Check(string field, bool same)
        {
            if (!same) differences.Add(field);
        }
        switch (source, winner)
        {
            case (IArmorAddonGetter s, IArmorAddonGetter w):
                Check("WorldModel", SameGenderedModels(s.WorldModel, w.WorldModel));
                Check("FirstPersonModel", SameGenderedModels(s.FirstPersonModel, w.FirstPersonModel));
                Check("SkinTexture", s.SkinTexture?.Male.FormKeyNullable == w.SkinTexture?.Male.FormKeyNullable
                    && s.SkinTexture?.Female.FormKeyNullable == w.SkinTexture?.Female.FormKeyNullable);
                Check("TextureSwapList", s.TextureSwapList?.Male.FormKeyNullable == w.TextureSwapList?.Male.FormKeyNullable
                    && s.TextureSwapList?.Female.FormKeyNullable == w.TextureSwapList?.Female.FormKeyNullable);
                break;
            case (ITextureSetGetter s, ITextureSetGetter w):
                Check("Textures", TexturePaths(s).SequenceEqual(TexturePaths(w), StringComparer.OrdinalIgnoreCase));
                Check("Decal", Equals(s.Decal, w.Decal));
                Check("Flags", s.Flags == w.Flags);
                break;
            case (IStaticGetter s, IStaticGetter w):
                Check("Model", LookFields.SameModel(s.Model, w.Model));
                Check("Lod", Equals(s.Lod, w.Lod));
                break;
            case (IProjectileGetter s, IProjectileGetter w):
                Check("Model", LookFields.SameModel(s.Model, w.Model));
                Check("MuzzleFlashModel", LookFields.SamePath(s.MuzzleFlashModel, w.MuzzleFlashModel));
                break;
        }
        return differences;
    }

    /// <summary>Copies the look fields of <paramref name="source"/> onto <paramref name="target"/>; links keep their FormKeys.</summary>
    public static void Copy(IMajorRecord target, IMajorRecordGetter source)
    {
        switch (target, source)
        {
            case (IArmorAddon t, IArmorAddonGetter s):
                t.WorldModel = CopyGenderedModels(s.WorldModel);
                t.FirstPersonModel = CopyGenderedModels(s.FirstPersonModel);
                t.SkinTexture = s.SkinTexture == null
                    ? null
                    : new GenderedItem<IFormLinkNullableGetter<ITextureSetGetter>>(
                        new FormLinkNullable<ITextureSetGetter>(s.SkinTexture.Male.FormKeyNullable),
                        new FormLinkNullable<ITextureSetGetter>(s.SkinTexture.Female.FormKeyNullable));
                t.TextureSwapList = s.TextureSwapList == null
                    ? null
                    : new GenderedItem<IFormLinkNullableGetter<IFormListGetter>>(
                        new FormLinkNullable<IFormListGetter>(s.TextureSwapList.Male.FormKeyNullable),
                        new FormLinkNullable<IFormListGetter>(s.TextureSwapList.Female.FormKeyNullable));
                break;
            case (ITextureSet t, ITextureSetGetter s):
                t.Diffuse = Texture(s.Diffuse);
                t.NormalOrGloss = Texture(s.NormalOrGloss);
                t.EnvironmentMaskOrSubsurfaceTint = Texture(s.EnvironmentMaskOrSubsurfaceTint);
                t.GlowOrDetailMap = Texture(s.GlowOrDetailMap);
                t.Height = Texture(s.Height);
                t.Environment = Texture(s.Environment);
                t.Multilayer = Texture(s.Multilayer);
                t.BacklightMaskOrSpecular = Texture(s.BacklightMaskOrSpecular);
                t.Decal = s.Decal?.DeepCopy();
                t.Flags = s.Flags;
                break;
            case (IStatic t, IStaticGetter s):
                t.Model = s.Model?.DeepCopy();
                t.Lod = s.Lod?.DeepCopy();
                break;
            case (IProjectile t, IProjectileGetter s):
                t.Model = s.Model?.DeepCopy();
                t.MuzzleFlashModel.GivenPath = s.MuzzleFlashModel.GivenPath;
                break;
        }
    }

    /// <summary>
    /// The asset paths in the record. For an armor addon also the inferred ones, which add the other weight's mesh
    /// (an addon lists only "_1.nif" or "_0.nif", the game loads both).
    /// </summary>
    public static IEnumerable<IAssetLinkGetter> AssetLinks(IMajorRecordGetter record) =>
        record.EnumerateAssetLinks(record is IArmorAddonGetter ? AssetLinkQuery.Listed | AssetLinkQuery.Inferred : AssetLinkQuery.Listed);

    /// <summary>The look links of the record to follow further: the texture sets of its models and its skin texture.</summary>
    public static IEnumerable<IFormLinkGetter> LookLinks(IMajorRecordGetter record)
    {
        var containers = new List<IFormLinkContainerGetter?>();
        var links = new List<IFormLinkGetter>();
        switch (record)
        {
            case IArmorAddonGetter addon:
                containers.AddRange([addon.WorldModel?.Male, addon.WorldModel?.Female, addon.FirstPersonModel?.Male, addon.FirstPersonModel?.Female]);
                if (addon.SkinTexture != null) links.AddRange([addon.SkinTexture.Male, addon.SkinTexture.Female]);
                break;
            case IStaticGetter stat:
                containers.Add(stat.Model);
                break;
            case IProjectileGetter projectile:
                containers.Add(projectile.Model);
                break;
        }
        return containers.WhereNotNull().SelectMany(container => container.EnumerateFormLinks()).Concat(links).Where(link => !link.IsNull);
    }

    private static IEnumerable<string> TexturePaths(ITextureSetGetter set) =>
        new[] { set.Diffuse, set.NormalOrGloss, set.EnvironmentMaskOrSubsurfaceTint, set.GlowOrDetailMap, set.Height, set.Environment, set.Multilayer, set.BacklightMaskOrSpecular }
            .Select(texture => texture?.GivenPath ?? string.Empty);

    private static AssetLink<SkyrimTextureAssetType>? Texture(IAssetLinkGetter<SkyrimTextureAssetType>? texture) =>
        texture == null ? null : new AssetLink<SkyrimTextureAssetType>(texture.GivenPath);

    private static bool SameGenderedModels(IGenderedItemGetter<IModelGetter?>? a, IGenderedItemGetter<IModelGetter?>? b) =>
        LookFields.SameModel(a?.Male, b?.Male) && LookFields.SameModel(a?.Female, b?.Female);

    private static GenderedItem<Model?>? CopyGenderedModels(IGenderedItemGetter<IModelGetter?>? models) =>
        models == null ? null : new GenderedItem<Model?>(models.Male?.DeepCopy(), models.Female?.DeepCopy());

    private static LookRecordType Of<TMajor, TGetter>(
        string signature, Func<ISkyrimModGetter, IGroupGetter<TGetter>> group, Func<ISkyrimMod, IGroup<TMajor>> patchGroup)
        where TMajor : class, IMajorRecordInternal, TGetter
        where TGetter : class, IMajorRecordGetter =>
        new(
            signature,
            typeof(TGetter),
            mod => group(mod),
            (mod, formKey) => group(mod).TryGetValue(formKey, out var record) ? record : null,
            (patch, winner) => patchGroup(patch).GetOrAddAsOverride<TMajor, TGetter>((TGetter)winner));
}
