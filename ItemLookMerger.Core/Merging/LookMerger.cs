using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>An item whose look changed in the patch: its own look fields, the forms its look depends on, or both.</summary>
/// <param name="LookPlugins">The look plugins the changes come from: the item's own look first, then those of its dependency forms.</param>
/// <param name="StatsPlugin">The stats side: the winning non-look plugin, or the record's origin master when no non-look plugin overrides it.</param>
/// <param name="Fields">The look fields that differed, then one entry per dependency form copied for the item ("ARMA IronCuirassAA").</param>
/// <param name="WholeRecord">
/// No non-look plugin overrides the record (the stats side is its origin master), so the look plugin's whole item was forwarded.
/// </param>
/// <param name="Base">The generic base weapon whose type-bound stats the item took, when the look gave it another weapon type.</param>
public sealed record ChangedItem(
    string Signature, FormKey FormKey, string? EditorID, string? Name, IReadOnlyList<ModKey> LookPlugins, ModKey StatsPlugin,
    IReadOnlyList<string> Fields, bool WholeRecord = false, BaseItem? Base = null)
{
    /// <summary>The look plugin whose version of the item's own look fields went into the patch; null when only dependency forms changed.</summary>
    public ModKey? LookSupplier { get; init; }
}

/// <summary>The base item an item took its type-bound stats from, and the plugin those stats come from.</summary>
public sealed record BaseItem(string? EditorID, FormKey FormKey, ModKey StatsPlugin);

/// <summary>An asset a forwarded look needs, from its look source's mod; <paramref name="Item"/> names the item for warnings.</summary>
public sealed record AssetNeed(string Path, string Item, LookSource Source);

/// <summary>A form the look depends on that went into the patch as a new record.</summary>
/// <param name="Original">The form it was made from: a look plugin's own record, or the shared record a look plugin overrides.</param>
/// <param name="AddedByLook">A look plugin added the form (the copy is the look plugin's version); else the copy is the winner's version with the look's visual fields.</param>
public sealed record CopiedForm(string Signature, FormKey Original, FormKey Copy, ModKey LookPlugin, bool AddedByLook);

/// <summary>An item a look plugin added, copied into the patch as a new record (CopyAddedItems).</summary>
/// <param name="Original">The item's FormKey in the look plugin that added it.</param>
/// <param name="Copy">The new record's FormKey in the patch.</param>
/// <param name="EditorID">The copy's EditorID.</param>
/// <param name="LookPlugins">The look plugin that added the item first, then the one that supplied its version (when another), then those of the forms its look depends on.</param>
/// <param name="Winner">The non-look plugin that overrides the item (the copy takes its stats); null when the look plugin's own version was copied.</param>
/// <param name="Supplier">The look plugin whose version of the item was copied (the latest one that contains it); null counts as the adding plugin.</param>
/// <param name="StatsFromPatch">True when the copy took its stats from an earlier patcher's version in the patch (the plugin of that patcher is not known).</param>
public sealed record AddedItem(
    string Signature, FormKey Original, FormKey Copy, string? EditorID, string? Name, IReadOnlyList<ModKey> LookPlugins, ModKey? Winner,
    ModKey? Supplier = null, bool StatsFromPatch = false);

/// <param name="DuplicatedRecords">Dependency forms a look plugin added, copied into the patch.</param>
/// <param name="ForwardedLookRecords">Dependency forms a look plugin overrides (shared records), copied into the patch with the look's visual fields.</param>
/// <param name="TypeSkipped">
/// Items left unchanged because of a type discrepancy (a wide one, the setting says skip, or no base weapon was found).
/// </param>
/// <param name="ArmorTypeKept">Armor forwarded with the modlist's armor type kept, under the default setting.</param>
public sealed record MergeResult(
    IReadOnlyList<ChangedItem> Changes, int IdenticalSkipped, int DuplicatedRecords, int ForwardedLookRecords, IReadOnlyList<AssetNeed> Assets,
    int TypeSkipped = 0, int ArmorTypeKept = 0)
{
    /// <summary>Every dependency form copied into the patch.</summary>
    public IReadOnlyList<CopiedForm> Copies { get; init; } = [];

    /// <summary>Every item a look plugin added that was copied into the patch (empty unless CopyAddedItems took effect).</summary>
    public IReadOnlyList<AddedItem> AddedItems { get; init; } = [];
}

/// <summary>
/// Steps 2 to 5: picks the item overrides of the sources, compares their look with the stats side's and forwards the differing looks
/// into the patch. Then walks the forms the look links to (armor addons, texture sets, statics, projectiles, impact data, ...) as far
/// as the look plugins contain them, and puts a new record into the patch for every one that carries the look (the shared original is
/// never changed). For a self-contained patch it also makes the patch independent of the look plugins.
/// </summary>
internal static class LookMerger
{
    /// <param name="loadOrder">The load order without the patch, lowest priority first. Every source is in it.</param>
    /// <param name="patch">The patch; records an earlier patcher of the Synthesis group put there count as the stats side.</param>
    /// <param name="sources">The look sources; between them, the one later in the load order wins.</param>
    /// <param name="selfContained">
    /// Make the patch independent of the look plugins (content merging on): keep the stats side's value of any field that would
    /// point at a look plugin's record the patch does not carry, and fail if a link to a look plugin is left.
    /// </param>
    /// <param name="onDiscrepancy">
    /// What to do when a look gives an item another weapon or armor type than its stats side has. A wider change (melee to bow, arrow
    /// to bolt, body armor to shield, another record type) leaves the item unchanged, with an error in the log, whatever this says.
    /// </param>
    /// <param name="copyAddedItems">
    /// Also copy the items the look plugins add themselves into the patch as new records. Only with <paramref name="selfContained"/>;
    /// ignored otherwise (the look plugin stays, so the items would be there twice).
    /// </param>
    public static MergeResult Merge(
        IReadOnlyList<ISkyrimModGetter> loadOrder, ISkyrimMod patch, IReadOnlyList<LookSource> sources, bool forwardNames, bool selfContained,
        TypeDiscrepancyHandling onDiscrepancy, ILog log, bool copyAddedItems = false) =>
        new Run(loadOrder, patch, sources, forwardNames, selfContained, onDiscrepancy, log, copyAddedItems && selfContained).Execute();

    private sealed record Pick(LookSource Source, IMajorRecordGetter Record);

    /// <summary>The stats side of a record: the version to keep the stats of, and the plugin credited for it.</summary>
    private sealed record StatsSide(ModKey Plugin, IMajorRecordGetter Version, bool InPatch);

    /// <summary>A record the run wrote into the patch, with a copy of its stats side's version from before the look went onto it.</summary>
    /// <param name="Fallback">The stats side's version as it was when the look went onto it (after a base weapon's type stats).</param>
    /// <param name="Origin">The stats side's own version from before those type stats; the second choice when the fallback points at a look plugin too.</param>
    private sealed record Written(IMajorRecord Target, IMajorRecordGetter? Fallback, string Label, IMajorRecordGetter? Origin = null);

    /// <summary>A form a look plugin contains that the look of a forwarded item depends on.</summary>
    private sealed class Node(FormKey key, LookRecordType? type, IMajorRecordGetter version, LookSource source, bool added)
    {
        public FormKey Key { get; } = key;

        /// <summary>Null when the form's type has no visual fields: it is neither copied nor followed.</summary>
        public LookRecordType? Type { get; } = type;

        /// <summary>The version of the latest look plugin that contains the form.</summary>
        public IMajorRecordGetter Version { get; } = version;

        public LookSource Source { get; } = source;

        /// <summary>A look plugin added the form (its origin is a look plugin).</summary>
        public bool Added { get; } = added;

        /// <summary>The forms its visual fields link to that a look plugin contains.</summary>
        public List<Node> Children { get; } = [];

        public FormKey? CopyKey { get; set; }

        /// <summary>For a shared record a look plugin overrides: its stats side, and the visual fields where the look differs from it.</summary>
        public StatsSide? Stats { get; set; }

        public IReadOnlyList<string> Differences { get; set; } = [];

        /// <summary>The form carries a look (or a form it points at does), so it goes into the patch as a new record.</summary>
        public bool NeedsCopy { get; set; }

        public string Label => $"{Type?.Signature ?? "record"} {LookMerger.Label(Version)}";
    }

    private sealed class Run
    {
        private readonly IReadOnlyList<ISkyrimModGetter> _loadOrder;
        private readonly ISkyrimMod _patch;
        private readonly IReadOnlyList<LookSource> _sources;
        private readonly bool _forwardNames;
        private readonly bool _selfContained;
        private readonly TypeDiscrepancyHandling _onDiscrepancy;
        private readonly ILog _log;
        private readonly bool _copyAddedItems;
        private readonly List<AddedItem> _addedItems = [];
        private readonly HashSet<FormKey> _addedCopied = [];
        private readonly Dictionary<ModKey, int> _overridesWon = [];
        private readonly HashSet<ModKey> _sourceKeys;
        private readonly Dictionary<ModKey, ISkyrimModGetter> _loaded;
        private readonly IReadOnlyList<LookSource> _weakestFirst;
        private readonly IReadOnlyList<LookSource> _strongestFirst;
        private readonly Dictionary<ModKey, ILinkCache<ISkyrimMod, ISkyrimModGetter>> _sourceCaches = [];
        private readonly Dictionary<FormKey, Node?> _nodes = [];
        private readonly Dictionary<FormKey, FormKey> _copyMap = [];
        private readonly List<(Node Node, IMajorRecord Target)> _copies = [];
        private readonly List<IMajorRecord> _itemTargets = [];
        private readonly List<string> _unresolved = [];
        private Dictionary<FormKey, Pick> _weaponPicks = [];
        private WeaponBases? _weaponBases;
        private readonly Dictionary<FormKey, IWeaponGetter> _patchWeaponsAtStart;
        private int _typeSkipped;
        private int _armorTypeKept;
        private readonly List<AssetNeed> _assets = [];
        private readonly List<Written> _written = [];
        private readonly List<Node> _fresh = [];
        private readonly HashSet<string> _editorIDs;
        private readonly HashSet<FormKey> _patchKeys;
        private readonly Dictionary<ModKey, Dictionary<FormKey, IMajorRecordGetter>> _masterIndex = [];

        public Run(
            IReadOnlyList<ISkyrimModGetter> loadOrder, ISkyrimMod patch, IReadOnlyList<LookSource> sources, bool forwardNames, bool selfContained,
            TypeDiscrepancyHandling onDiscrepancy, ILog log, bool copyAddedItems)
        {
            _copyAddedItems = copyAddedItems;
            _loadOrder = loadOrder;
            _patch = patch;
            _sources = sources;
            _forwardNames = forwardNames;
            _selfContained = selfContained;
            _onDiscrepancy = onDiscrepancy;
            _log = log;
            _sourceKeys = sources.Select(source => source.ModKey).ToHashSet();
            _loaded = loadOrder.ToDictionary(mod => mod.ModKey);
            _weakestFirst = InPrecedenceOrder(sources, loadOrder);
            _strongestFirst = _weakestFirst.Reverse().ToList();
            // Base weapons never come from what this run writes into the patch, so the weapons are copied before anything changes.
            _patchWeaponsAtStart = patch.Weapons.ToDictionary(weapon => weapon.FormKey, weapon => (IWeaponGetter)weapon.DeepCopy());
            // What the patch holds already (an earlier patcher's records): the copies must not take their EditorIDs or FormKeys.
            var existing = patch.EnumerateMajorRecords().ToList();
            _editorIDs = existing.Select(record => record.EditorID).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            _patchKeys = existing.Select(record => record.FormKey).ToHashSet();
        }

        public MergeResult Execute()
        {
            var changes = new List<ChangedItem>();
            var identical = 0;
            foreach (var type in ItemType.All)
            {
                var itemPicks = PickOverrides(type.Records);
                if (type.GetterType == typeof(IWeaponGetter)) _weaponPicks = itemPicks;
                foreach (var (formKey, pick) in itemPicks)
                {
                    var change = HandleItem(type, formKey, pick, out var wasIdentical);
                    if (change != null) changes.Add(change);
                    if (wasIdentical) identical++;
                }
            }
            if (_copyAddedItems)
            {
                foreach (var type in ItemType.All) CopyAddedItems(type);
            }
            if (_unresolved.Count > 0)
                throw new RunFailedException(
                    "These look plugins override records the load order does not have (a missing master or record):\n  "
                    + string.Join("\n  ", _unresolved));

            // The copies point at each other and the items at the copies; all of them exist now.
            foreach (var (node, target) in _copies) LookRecordType.RemapFields(target, node.Type!.Fields, _copyMap);
            foreach (var target in _itemTargets) LookRecordType.RemapFields(target, LookFields.LinkFieldNames, _copyMap);
            if (_selfContained) MakeSelfContained();

            var copies = _copies
                .Select(copy => new CopiedForm(copy.Node.Type!.Signature, copy.Node.Key, copy.Target.FormKey, copy.Node.Source.ModKey, copy.Node.Added))
                .ToList();
            return new MergeResult(
                changes, identical, copies.Count(copy => copy.AddedByLook), copies.Count(copy => !copy.AddedByLook), _assets, _typeSkipped, _armorTypeKept)
            {
                Copies = copies,
                AddedItems = _addedItems,
            };
        }

        /// <summary>
        /// CopyAddedItems: every item of this type a look plugin added (its FormKey belongs to a look plugin) goes into the patch as a
        /// new record, like the forms its look depends on. The copy is the latest look plugin's version of the item; when a non-look
        /// plugin overrides the item, the winner's version with the look's visual fields instead (the stats stay the winner's). Links
        /// to other copied added items are pointed at their copies and the dependency walk copies the item's visual forms.
        /// </summary>
        private void CopyAddedItems(ItemType type)
        {
            var added = new Dictionary<FormKey, (LookSource Source, IMajorRecordGetter Record)>();
            foreach (var source in _weakestFirst)
            {
                foreach (var record in type.Records(source.Mod))
                {
                    if (record.FormKey.ModKey != source.ModKey) continue;
                    // Only the adding plugin's own record is stored; a later look plugin's override of it is skipped by the check
                    // above and found per item below, as the version of the latest look plugin that contains the item.
                    added[record.FormKey] = (source, record);
                }
            }
            foreach (var (formKey, (origin, first)) in added)
            {
                var pick = _strongestFirst
                    .Select(source => type.Find(source.Mod, formKey) is { } found ? new Pick(source, found) : null)
                    .First(candidate => candidate != null)!;
                var label = $"{type.Signature} {Label(first)}";

                // The stats side: a non-look plugin that overrides the item, or an earlier patcher's version already in the patch.
                var stats = StatsSideOf(formKey, (mod, key) => type.Find(mod, key));
                var loadOrderWinner = stats != null && stats.Plugin != formKey.ModKey;
                var winner = stats == null || !(loadOrderWinner || stats.InPatch) ? null : loadOrderWinner ? stats : stats with { Plugin = _patch.ModKey };

                BaseWeapon? baseItem = null;
                if (winner != null
                    && LookFields.Differences(winner.Version, pick.Record, _forwardNames).Any(field => field != "Name")
                    && TypeDiscrepancies.Find(winner.Version, pick.Record) is { } discrepancy
                    && !Accept(label, winner, pick, discrepancy, out baseItem))
                    continue;

                IMajorRecord copy;
                if (winner != null)
                {
                    copy = NewCopy(type.AddToPatch, winner.Version, winner.Version.EditorID, formKey, first.EditorID);
                    if (baseItem != null) EnsureWeaponBases().Apply((IWeapon)copy, baseItem.Version);
                    LookFields.Copy(copy, pick.Record, _forwardNames);
                }
                else
                {
                    copy = NewCopy(type.AddToPatch, pick.Record, pick.Record.EditorID, formKey, first.EditorID);
                }
                if (UncarriedRequiredLink(copy, label) is { } required)
                {
                    // The copy is made before the check (the link can only be read from a record with the look on it), so it is taken out
                    // again with its EditorID and patch FormKey; the allocator may keep the FormKey for that EditorID, but the patch holds
                    // no record of it. An earlier patcher's override of the item is left as it is.
                    ReleaseCopy(type, copy);
                    _log.Error($"{label}: its {required} points at a form of a look plugin that the patch cannot carry and cannot be left empty; the item is not copied.");
                    continue;
                }
                if (winner is { InPatch: true })
                {
                    // The patch's override of the look plugin's item would need that plugin as a master; the copy replaces it.
                    type.RemoveFromPatch(_patch, formKey);
                    _log.Info($"{label}: an earlier patcher's version in the patch is the stats side; the copy replaces that override.");
                }
                if (winner != null)
                {
                    AddAssets(LookFields.ListedAssetLinks(pick.Record), pick.Source, label);
                    if (loadOrderWinner)
                        _log.Warning(
                            $"{label}: added by {origin.ModKey} and overridden by {winner.Plugin}; the copy takes the stats of {winner.Plugin} and the look of "
                            + $"{pick.Source.ModKey}, but {winner.Plugin} still needs {origin.ModKey} as a master, so it must stay enabled.");
                }
                else
                {
                    AddAssets(pick.Record.EnumerateAssetLinks(AssetLinkQuery.Listed), pick.Source, label);
                }
                _copyMap[formKey] = copy.FormKey;
                _addedCopied.Add(formKey);
                _itemTargets.Add(copy);
                // No stats side to fall back on: a link into a look plugin that is not carried is cleared (or falls back).
                _written.Add(new Written(copy, null, $"{label} (copied from {origin.ModKey})"));

                var start = LookFields.FormLinks(pick.Record).Concat(LookFields.DependencyLinks(copy));
                var lookPlugins = new[] { origin.ModKey, pick.Source.ModKey }
                    .Concat(Walk(start, label).Where(node => node.CopyKey != null).Select(node => node.Source.ModKey))
                    .Distinct()
                    .ToList();
                _addedItems.Add(new AddedItem(
                    type.Signature, formKey, copy.FormKey, copy.EditorID, LookFields.NameOf(copy)?.String, lookPlugins,
                    loadOrderWinner ? winner!.Plugin : (ModKey?)null, pick.Source.ModKey, winner is { InPatch: true }));
                _log.Info($"{origin.ModKey}: added {label} copied as {copy.FormKey}.");
            }
        }

        /// <summary>
        /// The name of a required link of the item (one the record cannot be without) that points at a look plugin's form the patch
        /// cannot carry, or "Effects" for a potion or ingredient whose effects all point at one; null when there is none. Such an item is not copied.
        /// A required link is a single link whose Mutagen type is not nullable (an arrow's projectile).
        /// </summary>
        private string? UncarriedRequiredLink(IMajorRecordGetter record, string label)
        {
            foreach (var property in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0 || !typeof(IFormLinkGetter).IsAssignableFrom(property.PropertyType)) continue;
                if (property.PropertyType.Name.Contains("Nullable", StringComparison.Ordinal)) continue;
                if (property.GetValue(record) is IFormLinkGetter { IsNull: false } link && _sourceKeys.Contains(link.FormKey.ModKey)
                    && NodeFor(link, label) is not { Type: not null })
                    return property.Name;
            }
            var effects = record switch
            {
                IIngestibleGetter potion => potion.Effects,
                IIngredientGetter ingredient => ingredient.Effects,
                _ => null,
            };
            return effects is { Count: > 0 } && effects.All(effect => effect.BaseEffect.FormKeyNullable is { } key && _sourceKeys.Contains(key.ModKey))
                ? "Effects"
                : null;
        }

        private ChangedItem? HandleItem(ItemType type, FormKey formKey, Pick pick, out bool wasIdentical)
        {
            wasIdentical = false;
            var stats = StatsSideOf(formKey, (mod, key) => type.Find(mod, key));
            if (stats == null)
            {
                ReportMissing(pick.Source, type, formKey);
                return null;
            }

            // No non-look plugin (and no earlier patcher) overrides the record: the look plugin is the only mod changing it, so its
            // whole record goes in.
            var whole = !stats.InPatch && stats.Plugin == formKey.ModKey;
            var itemDifferences = LookFields.Differences(stats.Version, pick.Record, _forwardNames);
            var lookVersion = itemDifferences.Count > 0 ? pick.Record : stats.Version;
            var label = $"{type.Signature} {Label(lookVersion)}";

            // Only a changed model, icon or addon is a look that could clash with the stats side's type; a name alone cannot.
            BaseWeapon? baseItem = null;
            if (!whole && itemDifferences.Any(field => field != "Name")
                && TypeDiscrepancies.Find(stats.Version, pick.Record) is { } discrepancy
                && !Accept(label, stats, pick, discrepancy, out baseItem))
                return null;

            IMajorRecord? target = null;
            IMajorRecordGetter? fallback = null;
            IMajorRecordGetter? origin = null;
            if (itemDifferences.Count > 0)
            {
                if (whole)
                {
                    fallback = Snapshot(stats.Version);
                    target = type.ForwardWhole(_patch, pick.Record);
                }
                else
                {
                    if (baseItem != null) origin = Snapshot(stats.Version);
                    target = type.GetOrAddAsOverride(_patch, stats.Version);
                    if (baseItem != null) EnsureWeaponBases().Apply((IWeapon)target, baseItem.Version);
                    // The fallback is the stats side as it is after the base weapon's type stats, before the look goes on.
                    fallback = Snapshot(target);
                    LookFields.Copy(target, pick.Record, _forwardNames);
                }
                AddAssets(whole ? pick.Record.EnumerateAssetLinks(AssetLinkQuery.Listed) : LookFields.ListedAssetLinks(pick.Record), pick.Source, label);
            }

            // The forms the item's look depends on: its look links, and the visual records it keeps from its stats side.
            var start = LookFields.FormLinks(pick.Record).Concat(LookFields.DependencyLinks(target ?? stats.Version));
            var copied = Walk(start, label).Where(node => node.CopyKey != null).ToList();
            if (target == null && copied.Count > 0 && ItemLinks(stats.Version).Any(link => _copyMap.ContainsKey(link.FormKey)))
            {
                // The look is the stats side's own, but a form it uses has a copy now: the item has to point at it.
                fallback = Snapshot(stats.Version);
                target = type.GetOrAddAsOverride(_patch, stats.Version);
            }
            if (target == null)
            {
                wasIdentical = true;
                _log.Verbose($"{label}: look of {pick.Source.ModKey} equals the stats side {stats.Plugin}; skipped.");
                return null;
            }
            _itemTargets.Add(target);
            if (_selfContained) _written.Add(new Written(target, fallback, label, origin));

            var lookPlugins = (itemDifferences.Count > 0 ? [pick.Source.ModKey] : Array.Empty<ModKey>())
                .Concat(copied.Select(node => node.Source.ModKey))
                .Distinct()
                .ToList();
            var fields = itemDifferences.Concat(copied.Select(node => node.Label)).ToList();
            if (baseItem != null) fields.Add($"weapon type from {Label(baseItem.Version)}");
            _log.Verbose(
                $"{label}: look from {string.Join(", ", lookPlugins)}, stats from {stats.Plugin} ({string.Join(", ", fields)})"
                + (whole && itemDifferences.Count > 0 ? "; no stats override, the whole record is forwarded." : "."));
            if (itemDifferences.Count > 0) _overridesWon[pick.Source.ModKey] = _overridesWon.GetValueOrDefault(pick.Source.ModKey) + 1;
            return new ChangedItem(
                type.Signature, formKey, target.EditorID, LookFields.NameOf(target)?.String, lookPlugins, stats.Plugin, fields,
                whole && itemDifferences.Count > 0,
                baseItem == null ? null : new BaseItem(baseItem.Version.EditorID, baseItem.Version.FormKey, baseItem.StatsPlugin))
            {
                LookSupplier = itemDifferences.Count > 0 ? pick.Source.ModKey : (ModKey?)null,
            };
        }

        private static IEnumerable<IFormLinkGetter> ItemLinks(IMajorRecordGetter item) =>
            LookFields.FormLinks(item).Concat(LookFields.DependencyLinks(item));

        /// <summary>
        /// Decides what a type discrepancy means for the item. False: leave the item unchanged (too wide, with an error in the log;
        /// skipped by the setting; or no base weapon). True: merge on, taking <paramref name="baseItem"/>'s type-bound stats when set.
        /// </summary>
        private bool Accept(string label, StatsSide stats, Pick pick, TypeDiscrepancy discrepancy, out BaseWeapon? baseItem)
        {
            baseItem = null;
            var what = $"{label}: type differs ({discrepancy}; look from {pick.Source.ModKey}, stats from {stats.Plugin})";
            if (discrepancy.Wide)
            {
                _log.Error($"{what}; the type change is too wide to merge, so the item is not changed.");
                _typeSkipped++;
                return false;
            }
            switch (_onDiscrepancy)
            {
                case TypeDiscrepancyHandling.SkipMerging:
                    _log.Info($"{what}; the item is not changed.");
                    _typeSkipped++;
                    return false;
                case TypeDiscrepancyHandling.TakeModlistItemType:
                    _log.Verbose($"{what}; the modlist's type is kept and the look is forwarded.");
                    return true;
            }

            if (stats.Version is not IWeaponGetter statsWeapon || pick.Record is not IWeaponGetter lookWeapon)
            {
                _armorTypeKept++;
                _log.Verbose($"{what}; the modlist's type is kept and the look is forwarded.");
                return true;
            }
            baseItem = EnsureWeaponBases().Find(stats.Version.FormKey, statsWeapon, lookWeapon);
            if (baseItem == null)
            {
                _log.Warning($"{what}: no generic base weapon of the look's type and the stats side's material was found; the item is not changed.");
                _typeSkipped++;
                return false;
            }
            _log.Verbose($"{what}; the type-bound stats come from {Label(baseItem.Version)} of {baseItem.StatsPlugin}.");
            return true;
        }

        private WeaponBases EnsureWeaponBases() => _weaponBases ??= new WeaponBases(
            _loadOrder, _sourceKeys, _patchWeaponsAtStart,
            formKey => _weaponPicks.TryGetValue(formKey, out var pick) && pick.Record is IWeaponGetter weapon ? (pick.Source.ModKey, weapon) : null);

        /// <summary>
        /// The dependency walk: from the given links through the visual fields of every form a look plugin contains. A form no look
        /// plugin contains ends the walk there. Returns every form reached, each once.
        /// </summary>
        private List<Node> Walk(IEnumerable<IFormLinkGetter> start, string item)
        {
            var reached = new List<Node>();
            var seen = new HashSet<FormKey>();
            var pending = new Queue<Node>();
            void Visit(Node? node)
            {
                if (node == null || !seen.Add(node.Key)) return;
                reached.Add(node);
                pending.Enqueue(node);
            }
            foreach (var link in start) Visit(NodeFor(link, item));
            while (pending.TryDequeue(out var node))
            {
                foreach (var child in node.Children) Visit(child);
            }
            return reached;
        }

        /// <summary>
        /// The form a link leads to, as the latest look plugin that contains it has it (added or overridden); null when no look plugin
        /// contains it. The forms it reaches are decided once, together, when the whole graph below the link is known.
        /// </summary>
        private Node? NodeFor(IFormLinkGetter link, string item)
        {
            var node = Resolve(link, item);
            if (_fresh.Count > 0) DecideFresh(item);
            return node;
        }

        private Node? Resolve(IFormLinkGetter link, string item)
        {
            if (link.IsNull) return null;
            if (_nodes.TryGetValue(link.FormKey, out var known)) return known;

            LookSource? from = null;
            IMajorRecordGetter? record = null;
            foreach (var source in _strongestFirst)
            {
                if (SourceCache(source).TryResolve(link.FormKey, link.Type, out record))
                {
                    from = source;
                    break;
                }
            }
            if (from == null || record == null)
            {
                _nodes[link.FormKey] = null;
                return null;
            }

            var type = LookRecordType.For(record);
            var node = new Node(link.FormKey, type, record, from, _sourceKeys.Contains(link.FormKey.ModKey));
            _nodes[link.FormKey] = node;
            if (type == null)
            {
                _log.Verbose($"{item}: {node.Label} of {from.ModKey} has no visual fields; not carried.");
                return node;
            }
            node.Children.AddRange(type.VisualLinks(record).Select(child => Resolve(child, item)).OfType<Node>());
            _fresh.Add(node);
            return node;
        }

        /// <summary>
        /// Decides which of the forms found since the last call go into the patch as new records, once their links are all known.
        /// A form a look plugin added: always (its version). A shared record a look plugin overrides: when its visual fields differ
        /// from the stats side's, or a form it points at is copied (so the copy can point at it). The latter is settled by repeating
        /// until nothing changes, so a cycle (a form list inside another, A to B to A) gives the same answer whichever form is reached
        /// first. The original is never touched.
        /// </summary>
        private void DecideFresh(string item)
        {
            var fresh = _fresh.ToList();
            _fresh.Clear();
            foreach (var node in fresh)
            {
                if (node.Added)
                {
                    node.NeedsCopy = true;
                    continue;
                }
                var type = node.Type!;
                var stats = StatsSideOf(node.Key, type.Find);
                if (stats == null)
                {
                    ReportUnresolved(node.Source, type.Signature, node.Key);
                    continue;
                }
                node.Stats = stats;
                node.Differences = type.Differences(stats.Version, node.Version);
                node.NeedsCopy = node.Differences.Count > 0;
            }
            for (var changed = true; changed;)
            {
                changed = false;
                foreach (var node in fresh.Where(node => !node.Added && node.Stats != null && !node.NeedsCopy))
                {
                    if (!node.Children.Any(child => child.NeedsCopy)) continue;
                    node.NeedsCopy = true;
                    changed = true;
                }
            }
            foreach (var node in fresh)
            {
                if (node.NeedsCopy) MakeCopy(node, item);
                else if (node.Stats != null)
                    _log.Verbose($"{item}: {node.Label}: look of {node.Source.ModKey} equals the stats side {node.Stats.Plugin}; no copy.");
            }
        }

        private void MakeCopy(Node node, string item)
        {
            var type = node.Type!;
            var label = node.Label;
            IMajorRecord copy;
            if (node.Added)
            {
                copy = NewCopy(type.AddToPatch, node.Version, node.Version.EditorID, node.Key);
                if (_selfContained) _written.Add(new Written(copy, null, $"{label} (copied from {node.Source.ModKey})"));
                AddAssets(LookRecordType.AssetLinks(node.Version), node.Source, item);
                _log.Verbose($"{item}: {label} is added by {node.Source.ModKey}; copied as {copy.FormKey}.");
            }
            else
            {
                var stats = node.Stats!;
                var fallback = Snapshot(stats.Version);
                copy = NewCopy(type.AddToPatch, stats.Version, stats.Version.EditorID, node.Key);
                if (node.Differences.Count > 0)
                {
                    type.Copy(copy, node.Version);
                    // Only what the look changes: the paths the stats side already uses are its own to provide.
                    var winnerPaths = type.VisualAssetLinks(stats.Version).Select(TryPath).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    AddAssets(
                        type.VisualAssetLinks(node.Version).Where(link => TryPath(link) is not { } path || !winnerPaths.Contains(path)),
                        node.Source, item);
                }
                if (_selfContained) _written.Add(new Written(copy, fallback, label));
                _log.Verbose(
                    node.Differences.Count > 0
                        ? $"{item}: {label}: copied as {copy.FormKey} with the look of {node.Source.ModKey}, the rest from {stats.Plugin} ({string.Join(", ", node.Differences)})."
                        : $"{item}: {label}: copied as {copy.FormKey} to point at the copies of the forms it uses.");
            }
            node.CopyKey = copy.FormKey;
            _copyMap[node.Key] = copy.FormKey;
            _copies.Add((node, copy));
        }

        private static string? TryPath(IAssetLinkGetter link)
        {
            try
            {
                return link.DataRelativePath.Path;
            }
            catch (AssetPathMisalignedException)
            {
                return null;
            }
        }

        /// <summary>Appended to the EditorID of every copy, so the copy and its original can be told apart.</summary>
        private const string CopySuffix = "_ILM";

        /// <summary>
        /// A new record in the patch with an EditorID and a FormKey of its own. The EditorID is the source's plus the suffix; when
        /// that is taken (two forms with the same EditorID), the source's plugin and FormID are added, so the name is the same on every
        /// run. A persistent allocator hands out one FormKey per EditorID, so no EditorID is asked for twice.
        /// </summary>
        private IMajorRecord NewCopy(Action<ISkyrimMod, IMajorRecord> addToPatch, IMajorRecordGetter source, string? editorID, FormKey original, string? originalEditorID = null)
        {
            string? name = null;
            if (!string.IsNullOrEmpty(editorID))
            {
                var baseName = editorID.EndsWith(CopySuffix, StringComparison.Ordinal) ? editorID : editorID + CopySuffix;
                name = baseName;
                // The name is also taken when it is the source's own or the original's (its EditorID already ends in the suffix, or the
                // winner's differs from the original's): the original stays enabled and a copy never shares its EditorID.
                // When the name turns out to be the source's or the original's, it stays reserved on purpose: that EditorID belongs to an
                // existing record, so no later copy may take it either.
                var taken = !_editorIDs.Add(name);
                if (taken || string.Equals(name, editorID, StringComparison.Ordinal) || string.Equals(name, originalEditorID, StringComparison.Ordinal))
                {
                    var origin = new string($"{original.ModKey.Name}_{original.ID:X6}".Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
                    name = $"{baseName}_{origin}";
                    for (var number = 2; !_editorIDs.Add(name); number++) name = $"{baseName}_{origin}_{number}";
                }
            }
            var formKey = _patch.GetNextFormKey(name);
            while (!_patchKeys.Add(formKey)) formKey = _patch.GetNextFormKey();
            var copy = (IMajorRecord)source.Duplicate(formKey);
            copy.EditorID = name;
            addToPatch(_patch, copy);
            return copy;
        }

        /// <summary>Takes a copy made by <see cref="NewCopy"/> out of the patch again and frees its EditorID and FormKey for the next copy.</summary>
        private void ReleaseCopy(ItemType type, IMajorRecord copy)
        {
            type.RemoveFromPatch(_patch, copy.FormKey);
            _patchKeys.Remove(copy.FormKey);
            if (copy.EditorID != null) _editorIDs.Remove(copy.EditorID);
        }

        /// <summary>
        /// The stats side of a record: the patch's version when an earlier patcher of the group already overrides it, else the winning
        /// version among the load order's non-look plugins, else (no non-look plugin has it) the origin master's version.
        /// Null when the load order lacks the record.
        /// </summary>
        private StatsSide? StatsSideOf(FormKey formKey, Func<ISkyrimModGetter, FormKey, IMajorRecordGetter?> find)
        {
            ModKey? credit = null;
            IMajorRecordGetter? version = null;
            for (var i = _loadOrder.Count - 1; i >= 0 && version == null; i--)
            {
                var mod = _loadOrder[i];
                if (_sourceKeys.Contains(mod.ModKey)) continue;
                if (find(mod, formKey) is { } record) (credit, version) = (mod.ModKey, record);
            }
            if (version == null && _loaded.TryGetValue(formKey.ModKey, out var origin) && find(origin, formKey) is { } original)
                (credit, version) = (origin.ModKey, original);
            if (version == null || credit == null) return null;
            return find(_patch, formKey) is { } patched ? new StatsSide(credit.Value, patched, InPatch: true) : new StatsSide(credit.Value, version, InPatch: false);
        }

        /// <summary>
        /// A copy of the stats side's version to fall back on, taken before the look goes onto it (only a self-contained patch needs it).
        /// </summary>
        private IMajorRecordGetter? Snapshot(IMajorRecordGetter statsVersion) =>
            _selfContained ? statsVersion.Duplicate(statsVersion.FormKey) : null;

        /// <summary>Step 4: a look plugin's override of a record the load order lacks fails the run; all of them are listed at once.</summary>
        private void ReportUnresolved(LookSource source, string signature, FormKey formKey)
        {
            var why = _loaded.ContainsKey(formKey.ModKey)
                ? $"its master {formKey.ModKey} has no such record"
                : $"its master {formKey.ModKey} is not in the load order";
            _unresolved.Add($"{source.ModKey}: {signature} {formKey} ({why})");
        }

        /// <summary>
        /// A look plugin's item override the stats side lacks. When its master defines that FormKey as another kind of record (a weapon
        /// overridden as armor), the type change is too wide to merge: an error in the log, the item is not changed. Otherwise the record
        /// is missing from the load order.
        /// </summary>
        private void ReportMissing(LookSource source, ItemType type, FormKey formKey)
        {
            IMajorRecordGetter? defined = null;
            if (_loaded.TryGetValue(formKey.ModKey, out var origin))
            {
                // One pass over the master for all the items it lacks, not one pass per item.
                if (!_masterIndex.TryGetValue(origin.ModKey, out var index))
                {
                    index = [];
                    foreach (var record in origin.EnumerateMajorRecords()) index.TryAdd(record.FormKey, record);
                    _masterIndex[origin.ModKey] = index;
                }
                index.TryGetValue(formKey, out defined);
            }
            if (defined == null)
                ReportUnresolved(source, type.Signature, formKey);
            else
            {
                _log.Error(
                    $"{source.ModKey}: {type.Signature} {formKey} (its master {formKey.ModKey} defines it as a {RecordTypeName(defined)}): "
                    + "the record type change is too wide to merge, so the item is not changed.");
                _typeSkipped++;
            }
        }

        /// <summary>
        /// Step 2: per FormKey, the record of the strongest source that overrides it. Records a source adds itself are left out;
        /// they only come into the patch through a forwarded look that uses them, or as added items (CopyAddedItems), which also
        /// takes the overrides of them by another look plugin.
        /// </summary>
        private Dictionary<FormKey, Pick> PickOverrides(Func<ISkyrimModGetter, IEnumerable<IMajorRecordGetter>> records)
        {
            var picked = new Dictionary<FormKey, Pick>();
            foreach (var source in _weakestFirst)
            {
                foreach (var record in records(source.Mod))
                {
                    if (record.FormKey.ModKey == source.ModKey || (_copyAddedItems && _sourceKeys.Contains(record.FormKey.ModKey))) continue;
                    picked[record.FormKey] = new Pick(source, record);
                }
            }
            return picked;
        }

        /// <summary>The asset paths a forwarded look uses, from the look source that provides the record. Never the stats side's assets.</summary>
        private void AddAssets(IEnumerable<IAssetLinkGetter> assetLinks, LookSource source, string item)
        {
            _assets.AddRange(assetLinks
                .Select(link => PathOf(link, source, item))
                .OfType<string>()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new AssetNeed(path, item, source)));
        }

        /// <summary>The data-relative path, or null (with a warning) for a path Mutagen cannot place in Data, such as "C:\work\x.nif".</summary>
        private string? PathOf(IAssetLinkGetter link, LookSource source, string item)
        {
            try
            {
                return link.DataRelativePath.Path;
            }
            catch (AssetPathMisalignedException)
            {
                _log.Warning($"{item}: {source.ModKey} names the asset \"{link.GivenPath}\", which is not a path inside Data; it is not copied.");
                return null;
            }
        }

        private ILinkCache<ISkyrimMod, ISkyrimModGetter> SourceCache(LookSource source)
        {
            if (!_sourceCaches.TryGetValue(source.ModKey, out var cache))
            {
                cache = source.Mod.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
                _sourceCaches[source.ModKey] = cache;
            }
            return cache;
        }

        /// <summary>
        /// Step 5 (content merging on): makes the patch independent of the look plugins. The forms a look plugin added are already
        /// copies (the walk made them) and the links to them already point at the copies. Any other link to a look plugin that is
        /// left in a record the run wrote (keywords, enchantments, scripts, races, a form without visual fields, ...) is not carried:
        /// the field gets the stats side's value back, with a warning, and in a copied look-added record (which has no stats side)
        /// the link is cleared, with a warning. Afterwards no link in the patch may point at a look plugin; if one does, the run fails.
        /// </summary>
        private void MakeSelfContained()
        {
            foreach (var entry in _written)
            {
                // A copy of a look-added record has no stats side: a link to a form that has a copy now points at it.
                if (entry.Fallback == null) entry.Target.RemapLinks(_copyMap);
                var cleared = new HashSet<FormKey>();
                foreach (var property in LinkProperties(entry.Target.GetType()))
                {
                    var lookLinks = LookRecordType.LinksIn(property.GetValue(entry.Target))
                        .Select(link => link.FormKey)
                        .Where(formKey => _sourceKeys.Contains(formKey.ModKey))
                        .Distinct()
                        .ToList();
                    if (lookLinks.Count == 0) continue;

                    var targets = string.Join(", ", lookLinks);
                    if (entry.Fallback != null)
                    {
                        RestoreLinks(entry, property, targets);
                    }
                    else
                    {
                        var lookKeys = lookLinks.ToHashSet();
                        cleared.UnionWith(lookLinks);
                        _log.Warning(
                            $"{entry.Label}: its {property.Name} points at {targets}, which is not a form the patch can carry; "
                            + $"{ClearLinks(entry.Target, property, lookKeys)}.");
                    }
                }
                if (cleared.Count > 0) entry.Target.RemapLinks(cleared.ToDictionary(formKey => formKey, _ => FormKey.Null));
            }

            var leftovers = _patch.EnumerateMajorRecords()
                .SelectMany(record => record.EnumerateFormLinks()
                    .Where(link => _sourceKeys.Contains(link.FormKey.ModKey))
                    .Select(link => $"{Label(record)} -> {link.FormKey}")
                    .Prepend(_sourceKeys.Contains(record.FormKey.ModKey) ? $"{Label(record)} is a record of {record.FormKey.ModKey}" : null))
                .OfType<string>()
                .Distinct()
                .ToList();
            if (leftovers.Count > 0)
                throw new RunFailedException(
                    "With content merging on, the patch must not need a look plugin as a master (so the look mods can be disabled), "
                    + "but these records still point at one: " + string.Join("; ", leftovers.Take(10))
                    + (leftovers.Count > 10 ? $" (and {leftovers.Count - 10} more)" : "")
                    + ". Turn content merging off to keep the look plugins as masters.");

            foreach (var source in _sources) ReportCarriedContent(source);
        }

        /// <summary>The vanilla DefaultRace (Skyrim.esm 000019), which an armor or armor addon falls back to when its race cannot be carried.</summary>
        private static readonly FormKey DefaultRace = new(ModKey.FromFileName("Skyrim.esm"), 0x000019);

        /// <summary>
        /// Clears the links of a property of a copied look-added record that point at <paramref name="cleared"/>, so the record stays valid:
        /// list entries are removed (keywords, races, armor addons; a potion's or ingredient's whole effect when its base effect is
        /// cleared), a cleared enchantment also clears the enchantment amount, a race falls back to the DefaultRace, and any other
        /// single link is set to null. Returns what happened, for the warning.
        /// </summary>
        private static string ClearLinks(IMajorRecord target, PropertyInfo property, IReadOnlySet<FormKey> cleared)
        {
            if (property.GetValue(target) is IList list)
            {
                var removed = 0;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var drop = list[i] switch
                    {
                        IFormLinkGetter link => link.FormKeyNullable is { } key && cleared.Contains(key),
                        IEffectGetter effect => effect.BaseEffect.FormKeyNullable is { } baseEffect && cleared.Contains(baseEffect),
                        _ => false,
                    };
                    if (!drop) continue;
                    list.RemoveAt(i);
                    removed++;
                }
                if (removed > 0) return removed == 1 ? "the entry is removed" : $"the {removed} entries are removed";
                return "the link is cleared";
            }
            switch (property.Name, target)
            {
                case ("Race", IArmor armor):
                    armor.Race.SetTo(DefaultRace);
                    return "the race falls back to the vanilla DefaultRace (Skyrim.esm 000019)";
                case ("Race", IArmorAddon addon):
                    addon.Race.SetTo(DefaultRace);
                    return "the race falls back to the vanilla DefaultRace (Skyrim.esm 000019)";
                case ("ObjectEffect", IArmor armor):
                    armor.EnchantmentAmount = null;
                    return "the link and the enchantment amount are cleared";
                case ("ObjectEffect", IWeapon weapon):
                    weapon.EnchantmentAmount = null;
                    return "the link and the enchantment amount are cleared";
            }
            return "the link is cleared";
        }

        /// <summary>
        /// Takes the stats side's value back for the links of a property that point at a look plugin. Only those links are replaced
        /// when the property has the same shape as the stats side's (the rest of the look in it stays); else the whole property is.
        /// A value that points at a look plugin itself (a base weapon's) is passed over for the stats side's own.
        /// </summary>
        private void RestoreLinks(Written entry, PropertyInfo property, string targets)
        {
            var candidates = new[] { entry.Fallback, entry.Origin }.OfType<IMajorRecordGetter>().ToList();
            bool Clean() => !LookRecordType.LinksIn(property.GetValue(entry.Target)).Any(link => _sourceKeys.Contains(link.FormKey.ModKey));
            foreach (var fallback in candidates)
            {
                var current = LookRecordType.LinksIn(property.GetValue(entry.Target)).ToList();
                var original = LookRecordType.LinksIn(property.GetValue(fallback)).ToList();
                var restored = current.Count == original.Count;
                var replacements = new Dictionary<FormKey, FormKey>();
                for (var i = 0; restored && i < current.Count; i++)
                {
                    if (!_sourceKeys.Contains(current[i].FormKey.ModKey)) continue;
                    // One stats side link per link into the look plugin; two different ones for the same form cannot be told apart.
                    var replacement = original[i].FormKeyNullable ?? FormKey.Null;
                    restored = replacements.TryAdd(current[i].FormKey, replacement) || replacements[current[i].FormKey] == replacement;
                }
                if (restored) LookRecordType.RemapFields(entry.Target, [property.Name], replacements);
                if (restored && Clean())
                {
                    _log.Warning(
                        $"{entry.Label}: its {property.Name} points at {targets} of a look plugin, which is not a form the patch can carry; "
                        + "that link takes the stats side's value.");
                    return;
                }
            }
            foreach (var fallback in candidates)
            {
                property.SetValue(entry.Target, property.GetValue(fallback));
                if (Clean()) break;
            }
            _log.Warning(
                $"{entry.Label}: its {property.Name} points at {targets} of a look plugin, which is not a form the patch can carry; "
                + "the stats side's whole value is kept, so the look in it is dropped too.");
        }

        /// <summary>
        /// What the patch carries of a look plugin (the looks of the items it overrides, the items it added when they are copied) and
        /// the records it doesn't carry, which are lost when that plugin is disabled. A plugin that contributes nothing gets a warning.
        /// </summary>
        private void ReportCarriedContent(LookSource source)
        {
            bool IsCarriedItem(IMajorRecordGetter record) =>
                ItemType.IsItem(record)
                && (record.FormKey.ModKey != source.ModKey && !_sourceKeys.Contains(record.FormKey.ModKey) || _addedCopied.Contains(record.FormKey));
            var lost = source.Mod.EnumerateMajorRecords()
                .Where(record => !IsCarriedItem(record) && !(_nodes.GetValueOrDefault(record.FormKey) is { Type: not null }))
                .GroupBy(RecordTypeName)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Count()} {group.Key}")
                .ToList();
            var dropped = lost.Count > 0 ? $" Disabling it also drops its other content: {string.Join(", ", lost)}." : "";

            // What the plugin supplied: the item looks it won, the added items whose copied version is its, the forms whose version is its.
            var overridden = _overridesWon.GetValueOrDefault(source.ModKey);
            // An added item counts for the plugin that added it as well as for the one whose version was copied.
            var added = _addedItems.Count(item => item.Original.ModKey == source.ModKey || (item.Supplier ?? item.Original.ModKey) == source.ModKey);
            var copiedForms = _copies.Count(copy => copy.Node.Source.ModKey == source.ModKey);
            var carried = new List<string>();
            if (overridden > 0) carried.Add($"the looks of {overridden} overridden item(s)");
            if (added > 0) carried.Add($"{added} added item(s) it copied");
            if (copiedForms > 0) carried.Add($"{copiedForms} form(s) the looks depend on");
            if (carried.Count > 0)
            {
                _log.Info($"{source.ModKey}: the patch carries {string.Join(" and ", carried)}.{dropped}");
                return;
            }

            var ownItems = ItemType.All.Sum(type => type.Records(source.Mod).Count(record => record.FormKey.ModKey == source.ModKey));
            if (ownItems > 0)
            {
                // Plain information: with the setting off its added items are not part of what the patch does.
                _log.Info(
                    _copyAddedItems
                        ? $"{source.ModKey}: the patch carries nothing of it: none of its {ownItems} added item(s) could be copied (see the errors).{dropped}"
                        : $"{source.ModKey}: the patch carries its item looks only; its {ownItems} added item(s) are not copied because CopyAddedItems is off.{dropped}");
                return;
            }

            var overrides = ItemType.All.Sum(type => type.Records(source.Mod).Count(record => record.FormKey.ModKey != source.ModKey));
            // An override of an item another look plugin added counts as carried above only when that item was copied; say so when it wasn't.
            var ofNotCopied = ItemType.All.Sum(type => type.Records(source.Mod)
                .Count(record => record.FormKey.ModKey != source.ModKey && _sourceKeys.Contains(record.FormKey.ModKey) && !_addedCopied.Contains(record.FormKey)));
            var why = overrides == 0
                ? "it overrides no items and adds none"
                : ofNotCopied == overrides
                    ? $"it overrides only items that another look plugin added and the patch did not copy ({overrides})"
                    : $"none of the {overrides} items it overrides has a look the patch needs";
            _log.Warning($"{source.ModKey}: contributes nothing: {why}.{dropped}");
        }
    }

    /// <summary>The settable properties of a record type whose values can hold form links.</summary>
    private static IEnumerable<PropertyInfo> LinkProperties(Type recordType) =>
        recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .Where(property => property.PropertyType != typeof(string) && !property.PropertyType.IsPrimitive && !property.PropertyType.IsEnum);

    /// <summary>The sources from weakest to strongest: by load order position (later wins), then by settings list order.</summary>
    private static IReadOnlyList<LookSource> InPrecedenceOrder(IReadOnlyList<LookSource> sources, IReadOnlyList<ISkyrimModGetter> loadOrder)
    {
        var positions = loadOrder.Select((mod, index) => (mod.ModKey, index)).ToDictionary(pair => pair.ModKey, pair => pair.index);
        return sources
            .Select((source, listIndex) => (source, listIndex))
            .OrderBy(entry => positions.TryGetValue(entry.source.ModKey, out var position) ? position : -1)
            .ThenBy(entry => entry.listIndex)
            .Select(entry => entry.source)
            .ToList();
    }

    internal static string Label(IMajorRecordGetter record) =>
        string.IsNullOrEmpty(record.EditorID) ? record.FormKey.ToString() : $"{record.EditorID} [{record.FormKey}]";

    private static string RecordTypeName(IMajorRecordGetter record)
    {
        var name = record.GetType().Name;
        const string overlay = "BinaryOverlay";
        return name.EndsWith(overlay, StringComparison.Ordinal) ? name[..^overlay.Length] : name;
    }
}
