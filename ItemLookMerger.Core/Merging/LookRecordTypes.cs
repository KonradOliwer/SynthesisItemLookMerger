using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ItemLookMerger.Core.Merging;

/// <summary>
/// A record type with visual fields: a form of this type that an item's look depends on is carried into the patch as a copy with the
/// look plugin's visual fields (see <see cref="Fields"/>). The fields are named after the Mutagen properties of the record, so
/// they are read, compared and copied by name. Sounds, gameplay data and everything that decides what a record does stay out.
/// </summary>
internal sealed class LookRecordType
{
    private readonly Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> _find;
    private readonly Func<ISkyrimMod, IGroup> _patchGroup;

    private LookRecordType(
        string signature,
        Type getterType,
        Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> find,
        Func<ISkyrimMod, IGroup> patchGroup,
        IReadOnlyList<string> fields,
        IReadOnlyList<string> copyOnly)
    {
        Signature = signature;
        GetterType = getterType;
        _find = find;
        _patchGroup = patchGroup;
        Fields = fields;
        _copyOnly = copyOnly;
    }

    /// <summary>Fields that go along with the visual ones but are never compared: hashes and data-length markers the plugin that saved them chose.</summary>
    private readonly IReadOnlyList<string> _copyOnly;

    public string Signature { get; }

    public Type GetterType { get; }

    /// <summary>The names of the visual fields.</summary>
    public IReadOnlyList<string> Fields { get; }

    public IMajorRecordGetter? Find(ISkyrimModGetter mod, FormKey formKey) => _find(mod, formKey);

    public void AddToPatch(ISkyrimMod patch, IMajorRecord record) => _patchGroup(patch).AddUntyped(record);

    /// <summary>
    /// ARMA: models, weight slider, skin texture, texture swap lists, art object. TXST: all texture paths, decal data, flags. STAT:
    /// bounds, model, LOD, material, flags. MSTT: model. PROJ: model, muzzle flash model and lights, decal (not the explosion, which
    /// is gameplay). LIGH: model and the light's look (not the time, not the can-be-carried flag). IPCT: model, decal, texture sets.
    /// IPDS: its impacts. ARTO: model, type. EFSH, MATO: everything but sounds. DEBR: its models. FLST: its entries (the texture
    /// swap lists of armor addons).
    /// </summary>
    public static readonly IReadOnlyList<LookRecordType> All =
    [
        Of<ArmorAddon, IArmorAddonGetter>(
            "ARMA", mod => mod.ArmorAddons, patch => patch.ArmorAddons,
            "WorldModel", "FirstPersonModel", "WeightSliderEnabled", "SkinTexture", "TextureSwapList", "ArtObject"),
        Of<TextureSet, ITextureSetGetter>(
            "TXST", mod => mod.TextureSets, patch => patch.TextureSets,
            "Diffuse", "NormalOrGloss", "EnvironmentMaskOrSubsurfaceTint", "GlowOrDetailMap", "Height", "Environment", "Multilayer",
            "BacklightMaskOrSpecular", "Decal", "Flags"),
        Of<Static, IStaticGetter>(
            "STAT", mod => mod.Statics, patch => patch.Statics, "ObjectBounds", "Model", "MaxAngle", "Material", "Flags", "Lod"),
        Of<MoveableStatic, IMoveableStaticGetter>("MSTT", mod => mod.MoveableStatics, patch => patch.MoveableStatics, "Model"),
        Of<Projectile, IProjectileGetter>(
            "PROJ", mod => mod.Projectiles, patch => patch.Projectiles,
            ["Model", "MuzzleFlashModel", "Light", "MuzzleFlash", "DecalData"], ["TextureFilesHashes"]),
        Of<Light, ILightGetter>(
            "LIGH", mod => mod.Lights, patch => patch.Lights,
            "Model", "Radius", "Color", "Flags", "FalloffExponent", "FOV", "NearClip", "FlickerPeriod", "FlickerIntensityAmplitude",
            "FlickerMovementAmplitude", "FadeValue", "Lens"),
        Of<Impact, IImpactGetter>(
            "IPCT", mod => mod.Impacts, patch => patch.Impacts,
            "Model", "Decal", "TextureSet", "SecondaryTextureSet", "Duration", "Orientation", "AngleThreshold", "PlacementRadius", "NoDecalData"),
        Of<ImpactDataSet, IImpactDataSetGetter>("IPDS", mod => mod.ImpactDataSets, patch => patch.ImpactDataSets, "Impacts"),
        Of<ArtObject, IArtObjectGetter>("ARTO", mod => mod.ArtObjects, patch => patch.ArtObjects, "Model", "Type"),
        Of<EffectShader, IEffectShaderGetter>(
            "EFSH", mod => mod.EffectShaders, patch => patch.EffectShaders,
            AllFieldsExcept(typeof(IEffectShaderGetter), "AmbientSound"), DataTypeStates(typeof(IEffectShaderGetter))),
        Of<MaterialObject, IMaterialObjectGetter>(
            "MATO", mod => mod.MaterialObjects, patch => patch.MaterialObjects,
            AllFieldsExcept(typeof(IMaterialObjectGetter)), DataTypeStates(typeof(IMaterialObjectGetter))),
        Of<Debris, IDebrisGetter>("DEBR", mod => mod.Debris, patch => patch.Debris, "Models"),
        Of<FormList, IFormListGetter>("FLST", mod => mod.FormLists, patch => patch.FormLists, "Items"),
    ];

    /// <summary>The visual record type of a record, or null when its type has no visual fields (items included).</summary>
    public static LookRecordType? For(IMajorRecordGetter record) => All.FirstOrDefault(type => type.GetterType.IsInstanceOfType(record));

    /// <summary>The visual fields that differ between the stats side's and the look plugin's version; empty when they look the same.</summary>
    public IReadOnlyList<string> Differences(IMajorRecordGetter winner, IMajorRecordGetter look) =>
        Fields.Where(field => !SameField(winner, look, field)).ToList();

    private static bool SameField(IMajorRecordGetter winner, IMajorRecordGetter look, string field) =>
        // Whether a light can be carried is gameplay, not look.
        field == "Flags" && winner is ILightGetter winnerLight && look is ILightGetter lookLight
            ? (winnerLight.Flags & ~Light.Flag.CanBeCarried) == (lookLight.Flags & ~Light.Flag.CanBeCarried)
            : SameValue(ValueOf(look, field), ValueOf(winner, field));

    /// <summary>Copies the visual fields of <paramref name="look"/> onto <paramref name="target"/>; links keep their FormKeys.</summary>
    public void Copy(IMajorRecord target, IMajorRecordGetter look)
    {
        var carried = (target as ILight)?.Flags & Light.Flag.CanBeCarried;
        // The values come from a deep copy, so the look plugin's record is never shared with the patch.
        var clone = look.Duplicate(look.FormKey);
        foreach (var field in Fields.Concat(_copyOnly)) SetValue(target, field, ValueOf(clone, field));
        if (target is ILight light && carried != null) light.Flags = (light.Flags & ~Light.Flag.CanBeCarried) | carried.Value;
    }

    /// <summary>The links in the visual fields: the forms the look depends on.</summary>
    public IEnumerable<IFormLinkGetter> VisualLinks(IMajorRecordGetter record) =>
        Fields.SelectMany(field => LinksIn(ValueOf(record, field))).Where(link => !link.IsNull);

    /// <summary>
    /// The asset paths in the record. For an armor addon also the inferred ones, which add the other weight's mesh
    /// (an addon lists only "_1.nif" or "_0.nif", the game loads both).
    /// </summary>
    public static IEnumerable<IAssetLinkGetter> AssetLinks(IMajorRecordGetter record) =>
        record.EnumerateAssetLinks(record is IArmorAddonGetter ? AssetLinkQuery.Listed | AssetLinkQuery.Inferred : AssetLinkQuery.Listed);

    /// <summary>
    /// The asset paths in the visual fields only (not a destructible's debris model, say). For an armor addon, which has no other
    /// assets, the same as <see cref="AssetLinks"/>, so the other weight's mesh is there.
    /// </summary>
    public IEnumerable<IAssetLinkGetter> VisualAssetLinks(IMajorRecordGetter record) =>
        record is IArmorAddonGetter ? AssetLinks(record) : Fields.SelectMany(field => AssetsIn(ValueOf(record, field)));

    private static IEnumerable<IAssetLinkGetter> AssetsIn(object? value) => value switch
    {
        null or string => [],
        IAssetLinkGetter link => [link],
        IAssetLinkContainerGetter container => container.EnumerateAssetLinks(AssetLinkQuery.Listed),
        IEnumerable items => items.Cast<object?>().SelectMany(AssetsIn),
        _ => [],
    };

    /// <summary>Points the links in the named fields of <paramref name="target"/> at the copies in <paramref name="mapping"/>.</summary>
    public static void RemapFields(IMajorRecord target, IEnumerable<string> fields, IReadOnlyDictionary<FormKey, FormKey> mapping)
    {
        foreach (var field in fields) RemapValue(ValueOf(target, field), mapping);
    }

    /// <summary>The form links inside a property value: a link, a link container (models, scripts, effects, ...) or a list of either.</summary>
    public static IEnumerable<IFormLinkGetter> LinksIn(object? value) => value switch
    {
        null or string => [],
        IFormLinkGetter link => [link],
        IFormLinkContainerGetter container => container.EnumerateFormLinks(),
        IEnumerable items => items.Cast<object?>().SelectMany(LinksIn),
        _ => [],
    };

    /// <summary>The value of a property by name, read through the record's own type or its interfaces (binary overlays); null if there is none.</summary>
    public static object? ValueOf(object record, string name) => FindProperty(record.GetType(), name)?.GetValue(record);

    private static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(type.GetInterfaces().SelectMany(contract => contract.GetProperties()))
            .FirstOrDefault(property => property.Name == name && property.GetIndexParameters().Length == 0);

    private static void SetValue(IMajorRecord target, string name, object? value)
    {
        var writable = target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property => property.Name == name && property.CanWrite && property.GetIndexParameters().Length == 0);
        if (writable != null)
        {
            writable.SetValue(target, value);
            return;
        }
        // Get-only members (lists, links, asset paths) are filled in place.
        switch (ValueOf(target, name), value)
        {
            case (IList list, IEnumerable items):
                list.Clear();
                foreach (var item in items) list.Add(item);
                break;
            case ({ } link, IFormLinkGetter from) when link is IFormLinkGetter:
                SetLinkKey(link, from.FormKeyNullable);
                break;
            case ({ } asset, IAssetLinkGetter from) when asset is IAssetLinkGetter:
                SetMember(asset, "GivenPath", from.GivenPath);
                break;
        }
    }

    private static void SetLinkKey(object link, FormKey? formKey) =>
        link.GetType().GetMethod(nameof(IFormLink<IMajorRecordGetter>.SetTo), [typeof(FormKey?)])!.Invoke(link, [formKey]);

    private static void SetMember(object target, string name, object? value) =>
        target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .First(property => property.Name == name && property.CanWrite)
            .SetValue(target, value);

    private static void RemapValue(object? value, IReadOnlyDictionary<FormKey, FormKey> mapping)
    {
        switch (value)
        {
            case null or string:
                break;
            case IFormLinkGetter link:
                if (link.FormKeyNullable is { } formKey && mapping.TryGetValue(formKey, out var copy)) SetLinkKey(link, copy);
                break;
            case IFormLinkContainer container:
                container.RemapLinks(mapping);
                break;
            case IEnumerable items:
                foreach (var item in items) RemapValue(item, mapping);
                break;
        }
    }

    /// <summary>
    /// Whether two values look the same: models by mesh and alternate textures (the MODT hash data is ignored), asset paths ignoring
    /// case, links by FormKey, lists and gendered values entry by entry, anything else by value.
    /// </summary>
    private static bool SameValue(object? a, object? b)
    {
        switch (a, b)
        {
            case (null, null):
                return true;
            case (null, _) or (_, null):
                return false;
            case (IModelGetter x, IModelGetter y):
                return LookFields.SameModel(x, y);
            case (IAssetLinkGetter x, IAssetLinkGetter y):
                return LookFields.SamePath(x, y);
            case (IFormLinkGetter x, IFormLinkGetter y):
                return x.FormKeyNullable == y.FormKeyNullable;
            case (string, string):
                return Equals(a, b);
            case (ReadOnlyMemorySlice<byte> x, ReadOnlyMemorySlice<byte> y):
                return x.Span.SequenceEqual(y.Span);
            case (MemorySlice<byte> x, MemorySlice<byte> y):
                return x.Span.SequenceEqual(y.Span);
            case (IEnumerable x, IEnumerable y):
                var left = x.Cast<object?>().ToList();
                var right = y.Cast<object?>().ToList();
                return left.Count == right.Count && left.Zip(right).All(pair => SameValue(pair.First, pair.Second));
            default:
                return Equals(a, b);
        }
    }

    /// <summary>The properties a record type declares itself (its own fields, not the major record header), less <paramref name="except"/>.</summary>
    private static string[] AllFieldsExcept(Type getterType, params string[] except)
    {
        var header = typeof(ISkyrimMajorRecordGetter).GetInterfaces().Append(typeof(ISkyrimMajorRecordGetter))
            .SelectMany(contract => contract.GetProperties())
            .Select(property => property.Name)
            .ToHashSet();
        return getterType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .Where(name => !header.Contains(name) && !except.Contains(name) && !IsDataTypeState(name))
            .ToArray();
    }

    /// <summary>Which DATA variant (length) a plugin wrote says nothing about the look; it goes along with the fields but is not compared.</summary>
    private static bool IsDataTypeState(string name) => name.EndsWith("DataTypeState", StringComparison.Ordinal);

    private static string[] DataTypeStates(Type getterType) =>
        getterType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .Where(IsDataTypeState)
            .ToArray();

    private static LookRecordType Of<TMajor, TGetter>(
        string signature, Func<ISkyrimModGetter, IGroupGetter<TGetter>> group, Func<ISkyrimMod, IGroup<TMajor>> patchGroup, params string[] fields)
        where TMajor : class, IMajorRecordInternal, TGetter
        where TGetter : class, IMajorRecordGetter =>
        Of<TMajor, TGetter>(signature, group, patchGroup, fields, []);

    private static LookRecordType Of<TMajor, TGetter>(
        string signature, Func<ISkyrimModGetter, IGroupGetter<TGetter>> group, Func<ISkyrimMod, IGroup<TMajor>> patchGroup, string[] fields,
        string[] copyOnly)
        where TMajor : class, IMajorRecordInternal, TGetter
        where TGetter : class, IMajorRecordGetter =>
        new(
            signature,
            typeof(TGetter),
            (mod, formKey) => group(mod).TryGetValue(formKey, out var record) ? record : null,
            patch => patchGroup(patch),
            fields,
            copyOnly);
}
