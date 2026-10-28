using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace ItemLookMerger.Core.Merging;

/// <summary>A base item's version as its stats side resolves it, and the plugin that version comes from.</summary>
internal sealed record BaseWeapon(IWeaponGetter Version, ModKey StatsPlugin);

/// <summary>
/// Finds the generic base weapon whose type-bound stats a weapon takes when a look plugin gives it another animation type (a
/// two-handed look on a one-handed sword), and puts those stats onto the merged record.
/// </summary>
internal sealed partial class WeaponBases
{
    private static readonly ModKey[] VanillaMasters =
        new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(name => ModKey.FromFileName(name)).ToArray();

    /// <summary>
    /// The weapon flags that describe the item and not its type; the merged record keeps its stats side's. The rest (automatic, scope,
    /// hide backpack, embedded, first and third person animations, ...) belongs to the type and comes from the base item.
    /// </summary>
    private const WeaponData.Flag ItemFlags = WeaponData.Flag.IgnoresNormalWeaponResistance | WeaponData.Flag.CantDrop | WeaponData.Flag.NonPlayable
        | WeaponData.Flag.PlayerOnly | WeaponData.Flag.MinorCrime | WeaponData.Flag.NonHostile | WeaponData.Flag.BoundWeapon;

    /// <summary>The weapon flags of items that are not generic: not for the player's normal use, or tied to a quest, a spell or a player.</summary>
    private const WeaponData.Flag NonGenericFlags = WeaponData.Flag.NonPlayable | WeaponData.Flag.PlayerOnly | WeaponData.Flag.BoundWeapon
        | WeaponData.Flag.CantDrop | WeaponData.Flag.NotUsedInNormalCombat;

    /// <summary>The material keywords of vanilla and its DLCs: WeapMaterial*, DLC1WeapMaterial*, DLC2WeaponMaterial*.</summary>
    [GeneratedRegex(@"^(DLC\d+)?Weap(on)?Material", RegexOptions.IgnoreCase)]
    private static partial Regex MaterialKeywordName();

    /// <summary>
    /// The weapon-type keywords (EditorIDs, lower case) a weapon of each animation type carries in vanilla. A crossbow has none of its
    /// own, so it is not listed and never checked.
    /// </summary>
    private static readonly Dictionary<WeaponAnimationType, string[]> ExpectedTypeKeywords = new()
    {
        [WeaponAnimationType.OneHandSword] = ["weaptypesword"],
        [WeaponAnimationType.OneHandDagger] = ["weaptypedagger"],
        [WeaponAnimationType.OneHandAxe] = ["weaptypewaraxe"],
        [WeaponAnimationType.OneHandMace] = ["weaptypemace"],
        [WeaponAnimationType.TwoHandSword] = ["weaptypegreatsword"],
        [WeaponAnimationType.TwoHandAxe] = ["weaptypebattleaxe", "weaptypewarhammer"],
        [WeaponAnimationType.Bow] = ["weaptypebow"],
        [WeaponAnimationType.Staff] = ["weaptypestaff"],
    };

    private static readonly HashSet<string> KnownTypeKeywords = ExpectedTypeKeywords.Values.SelectMany(names => names).ToHashSet();

    /// <summary>A base candidate with its animation type, weapon-type keywords and material keywords (lower case), worked out once.</summary>
    private sealed record Candidate(BaseWeapon Weapon, WeaponAnimationType Animation, string[] Types, string[] Materials);

    private readonly IReadOnlyList<ISkyrimModGetter> _loadOrder;
    private readonly IReadOnlySet<ModKey> _lookKeys;
    private readonly IReadOnlyDictionary<FormKey, IWeaponGetter> _patchAtStart;
    private readonly Func<FormKey, (ModKey Source, IWeaponGetter Record)?> _lookOverride;
    private readonly Dictionary<ModKey, int> _positions;
    private readonly Lazy<Dictionary<FormKey, string>> _keywordIds;
    private Dictionary<FormKey, Candidate>? _byFormKey;
    private Dictionary<WeaponAnimationType, List<Candidate>>? _index;

    /// <param name="loadOrder">The load order without the patch, lowest priority first.</param>
    /// <param name="lookKeys">The look plugins: a record one of them introduced is never a base item.</param>
    /// <param name="patchAtStart">
    /// Copies of the weapons the patch held before this run touched it (an earlier patcher's): the only patch versions a base may
    /// come from, so the result never depends on what this run has written since.
    /// </param>
    /// <param name="lookOverride">
    /// A look plugin's override of a weapon, or null: it stands in for a base item no other plugin overrides (as for an item that is
    /// forwarded whole).
    /// </param>
    /// <param name="patchKeywords">
    /// The EditorIDs of the keywords the patch held before this run (an earlier patcher's), so a keyword only they define resolves.
    /// </param>
    public WeaponBases(
        IReadOnlyList<ISkyrimModGetter> loadOrder, IReadOnlySet<ModKey> lookKeys, IReadOnlyDictionary<FormKey, IWeaponGetter> patchAtStart,
        Func<FormKey, (ModKey Source, IWeaponGetter Record)?> lookOverride, IReadOnlyDictionary<FormKey, string> patchKeywords)
    {
        _loadOrder = loadOrder;
        _lookKeys = lookKeys;
        _patchAtStart = patchAtStart;
        _lookOverride = lookOverride;
        _positions = loadOrder.Select((mod, index) => (mod.ModKey, index)).ToDictionary(pair => pair.ModKey, pair => pair.index);
        _keywordIds = new Lazy<Dictionary<FormKey, string>>(() =>
        {
            var ids = new Dictionary<FormKey, string>(patchKeywords);
            foreach (var mod in loadOrder)
            {
                foreach (var keyword in mod.Keywords)
                {
                    if (!string.IsNullOrEmpty(keyword.EditorID)) ids[keyword.FormKey] = keyword.EditorID;
                }
            }
            return ids;
        });
    }

    /// <summary>
    /// The known vanilla weapon-type keywords of <paramref name="look"/> when its animation type contradicts them: it has at least
    /// one and none is expected for its animation type. Custom ones (WeapTypeRapier, ...) are ignored. Null when they agree, when it
    /// has no known one, when its animation type has no expected keyword, or when one of its keywords is unresolved (it might be the
    /// expected one, so the look is not called inconsistent).
    /// </summary>
    public string[]? Contradiction(IWeaponGetter look)
    {
        if (look.Data is not { } data || !ExpectedTypeKeywords.TryGetValue(data.AnimationType, out var expected)) return null;
        var links = look.Keywords ?? [];
        if (links.Any(link => NameOf(link).Length == 0)) return null;
        var found = links.Select(NameOf).Where(name => KnownTypeKeywords.Contains(name.ToLowerInvariant())).Distinct().ToArray();
        return found.Length > 0 && !found.Any(name => expected.Contains(name.ToLowerInvariant())) ? found : null;
    }

    /// <summary>
    /// The base item for <paramref name="item"/>, a weapon whose look record is <paramref name="look"/> and whose stats side is
    /// <paramref name="stats"/>: a generic weapon of the look's animation type that shares a weapon-type keyword with the look and a
    /// material keyword with the stats side (the look's when the stats side has none); more shared materials win. The look record's
    /// template is preferred when it is such a weapon itself.
    /// Null when the look has no weapon-type keyword, no material is known, or no such weapon exists.
    /// </summary>
    public BaseWeapon? Find(FormKey item, IWeaponGetter stats, IWeaponGetter look)
    {
        var types = Names(look, IsTypeKeyword);
        var materials = Names(stats, IsMaterialKeyword);
        if (materials.Length == 0) materials = Names(look, IsMaterialKeyword);
        if (types.Length == 0 || materials.Length == 0) return null;

        EnsureBuilt();
        var animation = look.Data!.AnimationType;
        bool Matches(Candidate candidate) =>
            candidate.Animation == animation && candidate.Types.Intersect(types).Any() && candidate.Materials.Intersect(materials).Any();
        if (look.Template.FormKeyNullable is { } template && template != item
            && _byFormKey!.TryGetValue(template, out var templated) && Matches(templated))
            return templated.Weapon;
        // The index is best first and the sorts are stable, so more shared materials and types win, then the tie-break order.
        return _index!.GetValueOrDefault(animation)?
            .Where(candidate => candidate.Weapon.Version.FormKey != item && Matches(candidate))
            .OrderByDescending(candidate => candidate.Materials.Intersect(materials).Count())
            .ThenByDescending(candidate => candidate.Types.Intersect(types).Count())
            .FirstOrDefault()?.Weapon;
    }

    /// <summary>
    /// Indexes every generic weapon once by animation type, best first. Each weapon is
    /// resolved by the stats-side rule from the load order (and the patch as it was before this run), never from what the run wrote.
    /// </summary>
    private void EnsureBuilt()
    {
        if (_byFormKey != null) return;
        var winners = new Dictionary<FormKey, (ModKey Plugin, IWeaponGetter Weapon)>();
        foreach (var mod in _loadOrder)
        {
            if (_lookKeys.Contains(mod.ModKey)) continue;
            foreach (var weapon in mod.Weapons) winners[weapon.FormKey] = (mod.ModKey, weapon);
        }

        _byFormKey = [];
        var all = new List<Candidate>();
        foreach (var (formKey, (plugin, weapon)) in winners)
        {
            if (_lookKeys.Contains(formKey.ModKey)) continue;
            var resolved = new BaseWeapon(weapon, plugin);
            if (_patchAtStart.TryGetValue(formKey, out var patched)) resolved = resolved with { Version = patched };
            else if (plugin == formKey.ModKey && _lookOverride(formKey) is { } lookOnly) resolved = new BaseWeapon(lookOnly.Record, lookOnly.Source);
            if (ToCandidate(resolved) is not { } candidate) continue;
            _byFormKey[formKey] = candidate;
            all.Add(candidate);
        }

        _index = all
            .OrderBy(candidate => Array.IndexOf(VanillaMasters, candidate.Weapon.Version.FormKey.ModKey) >= 0 ? 0 : 1)
            .ThenBy(candidate => candidate.Weapon.Version.VirtualMachineAdapter?.Scripts.Count > 0 ? 1 : 0)
            .ThenBy(candidate => _positions.GetValueOrDefault(candidate.Weapon.Version.FormKey.ModKey, int.MaxValue))
            .ThenBy(candidate => candidate.Weapon.Version.FormKey.ID)
            .GroupBy(candidate => candidate.Animation)
            .ToDictionary(group => group.Key, group => group.ToList());
    }

    /// <summary>
    /// The weapon as a candidate when it is generic: with weapon data, damage and weight, playable, not bound, player-only, undroppable
    /// or enchanted, without a template, with a weapon-type and a material keyword, and not itself retyped by a look plugin.
    /// </summary>
    private Candidate? ToCandidate(BaseWeapon resolved)
    {
        var weapon = resolved.Version;
        if (weapon.Data is not { } data || weapon.BasicStats == null) return null;
        if ((data.Flags & NonGenericFlags) != 0 || weapon.MajorFlags.HasFlag(Weapon.MajorFlag.NonPlayable)) return null;
        if (!weapon.ObjectEffect.IsNull || !weapon.Template.IsNull) return null;
        // An item a look plugin retypes is an item of this run, not a generic one.
        if (_lookOverride(weapon.FormKey) is { } over && over.Record.Data is { } overData && overData.AnimationType != data.AnimationType) return null;
        var (types, materials) = (Names(weapon, IsTypeKeyword), Names(weapon, IsMaterialKeyword));
        return types.Length == 0 || materials.Length == 0 ? null : new Candidate(resolved, data.AnimationType, types, materials);
    }

    /// <summary>
    /// Puts the type-bound stats of <paramref name="baseItem"/> (a candidate from <see cref="Find"/>, so with weapon data and basic
    /// stats) onto <paramref name="target"/>, which still holds its stats side's values: weapon data (animation type, speed, reach,
    /// stagger, range, skill, type flags), damage and weight, critical data (none when the base has none), equip slot, block and
    /// impact data, sounds, detection level, and the weapon-type and material keywords.
    /// Name, value, enchantment, scripts, description, other keywords, destructible and template stay.
    /// </summary>
    public void Apply(IWeapon target, IWeaponGetter baseItem)
    {
        var data = baseItem.Data!.DeepCopy();
        var statsData = target.Data;
        data.Flags = (data.Flags & ~ItemFlags) | ((statsData?.Flags ?? 0) & ItemFlags);
        if (statsData != null) data.Resist = statsData.Resist;
        target.Data = data;
        var basic = baseItem.BasicStats!;
        target.BasicStats = new WeaponBasicStats { Value = target.BasicStats?.Value ?? 0, Weight = basic.Weight, Damage = basic.Damage };
        target.Critical = baseItem.Critical?.DeepCopy();

        target.EquipmentType.SetTo(baseItem.EquipmentType.FormKeyNullable);
        target.BlockBashImpact.SetTo(baseItem.BlockBashImpact.FormKeyNullable);
        target.AlternateBlockMaterial.SetTo(baseItem.AlternateBlockMaterial.FormKeyNullable);
        target.ImpactDataSet.SetTo(baseItem.ImpactDataSet.FormKeyNullable);
        target.PickUpSound.SetTo(baseItem.PickUpSound.FormKeyNullable);
        target.PutDownSound.SetTo(baseItem.PutDownSound.FormKeyNullable);
        target.AttackSound.SetTo(baseItem.AttackSound.FormKeyNullable);
        target.AttackSound2D.SetTo(baseItem.AttackSound2D.FormKeyNullable);
        target.AttackLoopSound.SetTo(baseItem.AttackLoopSound.FormKeyNullable);
        target.AttackFailSound.SetTo(baseItem.AttackFailSound.FormKeyNullable);
        target.IdleSound.SetTo(baseItem.IdleSound.FormKeyNullable);
        target.EquipSound.SetTo(baseItem.EquipSound.FormKeyNullable);
        target.UnequipSound.SetTo(baseItem.UnequipSound.FormKeyNullable);
        target.DetectionSoundLevel = baseItem.DetectionSoundLevel;

        var typeBound = (IFormLinkGetter<IKeywordGetter> link) => IsTypeBound(NameOf(link));
        target.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>>(
            (target.Keywords ?? []).Where(link => !typeBound(link))
                .Concat((baseItem.Keywords ?? []).Where(typeBound))
                .DistinctBy(link => link.FormKey)
                .Select(link => (IFormLinkGetter<IKeywordGetter>)new FormLink<IKeywordGetter>(link.FormKey)));
    }

    private static bool IsTypeKeyword(string name) => name.StartsWith("WeapType", StringComparison.OrdinalIgnoreCase);

    private static bool IsMaterialKeyword(string name) => MaterialKeywordName().IsMatch(name);

    private static bool IsTypeBound(string name) => IsTypeKeyword(name) || IsMaterialKeyword(name);

    private string NameOf(IFormLinkGetter<IKeywordGetter> link) => _keywordIds.Value.GetValueOrDefault(link.FormKey, string.Empty);

    /// <summary>The weapon's keyword EditorIDs (lower case, distinct) that <paramref name="pick"/> accepts.</summary>
    private string[] Names(IWeaponGetter weapon, Func<string, bool> pick) =>
        (weapon.Keywords ?? []).Select(NameOf).Where(name => name.Length > 0 && pick(name))
            .Select(name => name.ToLowerInvariant()).Distinct().ToArray();
}
