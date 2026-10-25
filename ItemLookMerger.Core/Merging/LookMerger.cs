using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>An item whose look changed in the patch: its own look fields, the look records it uses (ARMA, TXST, STAT, PROJ), or both.</summary>
/// <param name="LookPlugins">The look plugins the changes come from: the item's own look first, then those of its look records.</param>
/// <param name="StatsPlugin">The stats side: the winning non-look plugin, or the record's origin master when no non-look plugin overrides it.</param>
/// <param name="Fields">The look fields that differed, then one entry per changed look record ("ARMA IronCuirassAA").</param>
/// <param name="WholeRecord">
/// No non-look plugin overrides the record (the stats side is its origin master), so the look plugin's whole record was forwarded.
/// </param>
public sealed record ChangedItem(
    string Signature, FormKey FormKey, string? EditorID, string? Name, IReadOnlyList<ModKey> LookPlugins, ModKey StatsPlugin,
    IReadOnlyList<string> Fields, bool WholeRecord = false);

/// <summary>An asset a forwarded look needs, from its look source's mod; <paramref name="Item"/> names the item for warnings.</summary>
public sealed record AssetNeed(string Path, string Item, LookSource Source);

public sealed record MergeResult(
    IReadOnlyList<ChangedItem> Changes, int IdenticalSkipped, int DuplicatedRecords, int ForwardedLookRecords, IReadOnlyList<AssetNeed> Assets);

/// <summary>
/// Steps 2 to 5: picks the item overrides of the sources and the look records (ARMA, TXST, STAT, PROJ) they override, compares
/// their look with the stats side's, forwards the differing looks into the patch and copies the records a merged-in source's
/// looks depend on.
/// </summary>
internal static class LookMerger
{
    /// <param name="loadOrder">The load order without the patch, lowest priority first.</param>
    /// <param name="patch">The patch; records an earlier patcher of the Synthesis group put there count as the stats side.</param>
    /// <param name="sources">The look sources; between them, the one whose MO2 mod has the higher priority wins.</param>
    public static MergeResult Merge(
        IReadOnlyList<ISkyrimModGetter> loadOrder, ISkyrimMod patch, IReadOnlyList<LookSource> sources, bool forwardNames, ILog log) =>
        new Run(loadOrder, patch, sources, forwardNames, log).Execute();

    private sealed record Pick(LookSource Source, IMajorRecordGetter Record);

    /// <summary>The stats side of a record: the version to keep the stats of, and the plugin credited for it.</summary>
    private sealed record StatsSide(ModKey Plugin, IMajorRecordGetter Version, bool InPatch);

    /// <summary>What became of one look record: the version whose links are followed further, and the source it was forwarded from.</summary>
    private sealed record LookRecordDecision(IMajorRecordGetter? Effective, LookSource? ForwardedFrom, string Label);

    private sealed class Run
    {
        private readonly IReadOnlyList<ISkyrimModGetter> _loadOrder;
        private readonly ISkyrimMod _patch;
        private readonly IReadOnlyList<LookSource> _sources;
        private readonly bool _forwardNames;
        private readonly ILog _log;
        private readonly HashSet<ModKey> _sourceKeys;
        private readonly Dictionary<ModKey, ISkyrimModGetter> _loaded;
        private readonly Dictionary<ModKey, LookSource> _mergedSources;
        private readonly IReadOnlyList<LookSource> _weakestFirst;
        private readonly Dictionary<ModKey, ILinkCache<ISkyrimMod, ISkyrimModGetter>> _sourceCaches = [];
        private readonly Dictionary<FormKey, (LookRecordType Type, Pick Pick)> _lookPicks = [];
        private readonly Dictionary<FormKey, LookRecordDecision> _lookDecisions = [];
        private readonly Dictionary<FormKey, bool> _reaches = [];
        private readonly List<string> _unresolvedInLoadOrder = [];
        private readonly List<AssetNeed> _assets = [];
        private readonly HashSet<FormKey> _forwardedLookRecords = [];

        public Run(IReadOnlyList<ISkyrimModGetter> loadOrder, ISkyrimMod patch, IReadOnlyList<LookSource> sources, bool forwardNames, ILog log)
        {
            _loadOrder = loadOrder;
            _patch = patch;
            _sources = sources;
            _forwardNames = forwardNames;
            _log = log;
            _sourceKeys = sources.Select(source => source.ModKey).ToHashSet();
            _loaded = loadOrder.ToDictionary(mod => mod.ModKey);
            _mergedSources = sources.Where(source => !source.InLoadOrder).ToDictionary(source => source.ModKey);
            _weakestFirst = InPrecedenceOrder(sources, loadOrder);
        }

        public MergeResult Execute()
        {
            foreach (var type in LookRecordType.All)
            {
                foreach (var (formKey, pick) in PickOverrides(type.Records)) _lookPicks[formKey] = (type, pick);
            }

            var changes = new List<ChangedItem>();
            var identical = 0;
            foreach (var type in ItemType.All)
            {
                var itemPicks = PickOverrides(type.Records);
                var candidates = itemPicks.Keys.ToList();
                // Items the look sources do not override can still use an ARMA, TXST, STAT or PROJ they override.
                if (_lookPicks.Count > 0) candidates.AddRange(ItemsReachingLookPicks(type).Where(formKey => !itemPicks.ContainsKey(formKey)));
                foreach (var formKey in candidates)
                {
                    var change = HandleItem(type, formKey, itemPicks.GetValueOrDefault(formKey), out var wasIdentical);
                    if (change != null) changes.Add(change);
                    if (wasIdentical) identical++;
                }
            }
            if (_unresolvedInLoadOrder.Count > 0)
                throw new RunFailedException(
                    "These enabled look plugins override records the load order does not have (a missing master or record):\n  "
                    + string.Join("\n  ", _unresolvedInLoadOrder));

            var duplicated = _mergedSources.Count == 0 ? 0 : CopyDependentRecords();
            return new MergeResult(changes, identical, duplicated, _forwardedLookRecords.Count, _assets);
        }

        private ChangedItem? HandleItem(ItemType type, FormKey formKey, Pick? pick, out bool wasIdentical)
        {
            wasIdentical = false;
            var stats = StatsSideOf(formKey, (mod, key) => type.Find(mod, key));
            if (stats == null)
            {
                if (pick != null) ReportUnresolved(pick.Source, type.Signature, formKey);
                return null;
            }

            // No non-look plugin (and no earlier patcher) overrides the record: the look plugin is the only mod changing it, so its
            // whole record goes in.
            var whole = pick != null && !stats.InPatch && stats.Plugin == formKey.ModKey;
            var itemDifferences = pick == null ? [] : LookFields.Differences(stats.Version, pick.Record, _forwardNames);
            var lookVersion = pick != null && itemDifferences.Count > 0 ? pick.Record : stats.Version;
            var label = $"{type.Signature} {Label(lookVersion)}";

            // The look records the item ends up using, followed through their own look links.
            var startLinks = (whole ? pick!.Record.EnumerateFormLinks() : LookFields.FormLinks(lookVersion)).ToList();
            if ((whole ? pick!.Record : stats.Version) is IAmmunitionGetter ammo && !ammo.Projectile.IsNull) startLinks.Add(ammo.Projectile);
            var changedLookRecords = new List<LookRecordDecision>();
            var seen = new HashSet<FormKey>();
            var pending = new Queue<IFormLinkGetter>(startLinks);
            while (pending.TryDequeue(out var link))
            {
                if (LookRecordType.ForLink(link) is not { } lookType || !seen.Add(link.FormKey)) continue;
                var decision = Decide(lookType, link.FormKey, label);
                if (decision.ForwardedFrom != null) changedLookRecords.Add(decision);
                if (decision.Effective != null)
                {
                    foreach (var next in LookRecordType.LookLinks(decision.Effective)) pending.Enqueue(next);
                }
            }

            if (itemDifferences.Count == 0 && changedLookRecords.Count == 0)
            {
                wasIdentical = pick != null;
                if (pick != null)
                    _log.Verbose($"{label}: look of {pick.Source.ModKey} equals the stats side {stats.Plugin}; skipped.");
                return null;
            }

            IMajorRecordGetter credited = stats.Version;
            if (itemDifferences.Count > 0)
            {
                IMajorRecord target;
                if (whole)
                {
                    target = type.ForwardWhole(_patch, pick!.Record);
                }
                else
                {
                    target = type.GetOrAddAsOverride(_patch, stats.Version);
                    LookFields.Copy(target, pick!.Record, _forwardNames);
                }
                credited = target;
                var assetLinks = whole ? pick.Record.EnumerateAssetLinks(AssetLinkQuery.Listed) : LookFields.ListedAssetLinks(pick.Record);
                CollectAssets(assetLinks, startLinks, pick.Source, label);
            }

            var lookPlugins = (itemDifferences.Count > 0 ? [pick!.Source.ModKey] : Array.Empty<ModKey>())
                .Concat(changedLookRecords.Select(decision => decision.ForwardedFrom!.ModKey))
                .Distinct()
                .ToList();
            var fields = itemDifferences.Concat(changedLookRecords.Select(decision => decision.Label)).ToList();
            _log.Verbose(
                $"{label}: look from {string.Join(", ", lookPlugins)}, stats from {stats.Plugin} ({string.Join(", ", fields)})"
                + (whole && itemDifferences.Count > 0 ? "; no stats override, the whole record is forwarded." : "."));
            return new ChangedItem(
                type.Signature, formKey, credited.EditorID, LookFields.NameOf(credited)?.String, lookPlugins, stats.Plugin, fields,
                whole && itemDifferences.Count > 0);
        }

        /// <summary>
        /// Decides a look record once: when the strongest look source overrides it with a different look, its look fields go onto the
        /// stats side's version in the patch.
        /// </summary>
        private LookRecordDecision Decide(LookRecordType type, FormKey formKey, string item)
        {
            if (_lookDecisions.TryGetValue(formKey, out var known)) return known;
            LookRecordDecision decision;
            if (!_lookPicks.TryGetValue(formKey, out var picked) || picked.Type != type)
            {
                decision = new LookRecordDecision(EffectiveVersion(type, formKey), null, formKey.ToString());
            }
            else
            {
                var pick = picked.Pick;
                var label = $"{type.Signature} {Label(pick.Record)}";
                var stats = StatsSideOf(formKey, (mod, key) => type.Find(mod, key));
                if (stats == null)
                {
                    ReportUnresolved(pick.Source, type.Signature, formKey);
                    decision = new LookRecordDecision(pick.Record, null, label);
                }
                else if (LookRecordType.Differences(stats.Version, pick.Record) is { Count: > 0 } differences)
                {
                    var target = type.GetOrAddAsOverride(_patch, stats.Version);
                    LookRecordType.Copy(target, pick.Record);
                    _forwardedLookRecords.Add(formKey);
                    CollectAssets(LookRecordType.AssetLinks(pick.Record), LookRecordType.LookLinks(pick.Record), pick.Source, item);
                    _log.Verbose($"{label}: look from {pick.Source.ModKey}, the rest from {stats.Plugin} ({string.Join(", ", differences)}).");
                    decision = new LookRecordDecision(pick.Record, pick.Source, label);
                }
                else
                {
                    decision = new LookRecordDecision(pick.Record, null, label);
                }
            }
            _lookDecisions[formKey] = decision;
            return decision;
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

        /// <summary>Step 4 (A/B): a disabled source's override of a record the load order lacks is skipped; an enabled one's fails the run.</summary>
        private void ReportUnresolved(LookSource source, string signature, FormKey formKey)
        {
            var why = _loaded.ContainsKey(formKey.ModKey)
                ? $"its master {formKey.ModKey} has no such record"
                : $"its master {formKey.ModKey} is not in the load order";
            if (source.InLoadOrder) _unresolvedInLoadOrder.Add($"{source.ModKey}: {signature} {formKey} ({why})");
            else _log.Warning($"{source.ModKey} overrides {signature} {formKey}, but {why}; that record is skipped.");
        }

        /// <summary>
        /// The version of a look record no look source overrides: the patch's, else the load order winner's, else (a record a merged-in
        /// source adds) the source's own.
        /// </summary>
        private IMajorRecordGetter? EffectiveVersion(LookRecordType type, FormKey formKey)
        {
            if (type.Find(_patch, formKey) is { } patched) return patched;
            for (var i = _loadOrder.Count - 1; i >= 0; i--)
            {
                if (type.Find(_loadOrder[i], formKey) is { } record) return record;
            }
            return _mergedSources.TryGetValue(formKey.ModKey, out var source) ? type.Find(source.Mod, formKey) : null;
        }

        /// <summary>The FormKeys of the load order's items of this type whose look reaches a look record a look source overrides.</summary>
        private IEnumerable<FormKey> ItemsReachingLookPicks(ItemType type)
        {
            var seen = new HashSet<FormKey>();
            foreach (var mod in _loadOrder.Reverse())
            {
                foreach (var record in type.Records(mod))
                {
                    if (!seen.Add(record.FormKey)) continue;
                    var version = type.Find(_patch, record.FormKey) ?? record;
                    var links = LookFields.FormLinks(version).ToList();
                    if (version is IAmmunitionGetter ammo && !ammo.Projectile.IsNull) links.Add(ammo.Projectile);
                    if (links.Any(Reaches)) yield return record.FormKey;
                }
            }
        }

        /// <summary>Whether the look record (or one its look links lead to) is overridden by a look source. Cycles count as no.</summary>
        private bool Reaches(IFormLinkGetter link)
        {
            if (LookRecordType.ForLink(link) is not { } type) return false;
            if (_lookPicks.ContainsKey(link.FormKey)) return true;
            if (_reaches.TryGetValue(link.FormKey, out var known)) return known;
            _reaches[link.FormKey] = false;
            var version = EffectiveVersion(type, link.FormKey);
            var result = version != null && LookRecordType.LookLinks(version).Any(Reaches);
            _reaches[link.FormKey] = result;
            return result;
        }

        /// <summary>
        /// Step 2: per FormKey, the record of the strongest source that overrides it. Records a source adds itself are left out;
        /// they are only copied when a forwarded look references them.
        /// </summary>
        private Dictionary<FormKey, Pick> PickOverrides(Func<ISkyrimModGetter, IEnumerable<IMajorRecordGetter>> records)
        {
            var picked = new Dictionary<FormKey, Pick>();
            foreach (var source in _weakestFirst)
            {
                foreach (var record in records(source.Mod))
                {
                    if (record.FormKey.ModKey != source.ModKey) picked[record.FormKey] = new Pick(source, record);
                }
            }
            return picked;
        }

        /// <summary>
        /// The asset paths a forwarded look uses: the given ones, plus those of the look source's own records the look links lead to
        /// (followed through further links into the same source). Never the stats side's assets.
        /// </summary>
        private void CollectAssets(IEnumerable<IAssetLinkGetter> assetLinks, IEnumerable<IFormLinkGetter> links, LookSource source, string item)
        {
            var sourceCache = SourceCache(source);
            var paths = assetLinks.Select(link => PathOf(link, source, item)).ToList();
            var seen = new HashSet<FormKey>();
            var pending = new Queue<IFormLinkGetter>(links);
            while (pending.TryDequeue(out var link))
            {
                if (link.FormKey.ModKey != source.ModKey || !seen.Add(link.FormKey)) continue;
                if (!sourceCache.TryResolve(link.FormKey, link.Type, out var linked)) continue;
                paths.AddRange(LookRecordType.AssetLinks(linked).Select(asset => PathOf(asset, source, item)));
                foreach (var next in linked.EnumerateFormLinks()) pending.Enqueue(next);
            }
            _assets.AddRange(paths
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
        /// Step 5: the records of merged-in sources that the patch now links to (armor addons, texture sets, statics, ...) are copied
        /// into the patch as new records and every link is remapped. A link to a record the source does not have (a dangling reference)
        /// is cleared with a warning. Afterwards no patch record may link to such a source, as it would become a master the load order
        /// does not have.
        /// </summary>
        private int CopyDependentRecords()
        {
            var copied = 0;
            foreach (var source in _mergedSources.Values)
            {
                var sourceCache = SourceCache(source);
                var mapping = new Dictionary<FormKey, FormKey>();
                var dangling = new Dictionary<FormKey, string>();
                var pending = new Queue<(IFormLinkGetter Link, string Owner)>(
                    _patch.EnumerateMajorRecords().SelectMany(record => record.EnumerateFormLinks().Select(link => (link, Label(record)))));
                while (pending.TryDequeue(out var entry))
                {
                    var (link, owner) = entry;
                    if (link.FormKey.ModKey != source.ModKey || mapping.ContainsKey(link.FormKey) || dangling.ContainsKey(link.FormKey)) continue;
                    if (!sourceCache.TryResolveContext(link.FormKey, link.Type, out var context))
                    {
                        dangling[link.FormKey] = owner;
                        continue;
                    }
                    var duplicate = context.DuplicateIntoAsNewRecord(_patch, context.Record.EditorID);
                    mapping[link.FormKey] = duplicate.FormKey;
                    _log.Verbose($"Copied {link.FormKey} from {source.ModKey} into the patch as {duplicate.FormKey}.");
                    foreach (var next in duplicate.EnumerateFormLinks()) pending.Enqueue((next, Label(duplicate)));
                }
                _patch.RemapLinks(mapping);
                if (dangling.Count > 0)
                {
                    _log.Warning(
                        $"{source.ModKey} links to records it does not contain; those links are cleared in the patch: "
                        + string.Join("; ", dangling.Select(pair => $"{pair.Value} -> {pair.Key}")));
                    _patch.RemapLinks(dangling.Keys.ToDictionary(formKey => formKey, _ => FormKey.Null));
                }
                copied += mapping.Count;
                WarnAboutLostContent(source, mapping);
            }

            var mergedKeys = _mergedSources.Keys.ToHashSet();
            var leftovers = _patch.EnumerateMajorRecords()
                .SelectMany(record => record.EnumerateFormLinks().Select(link => (record, link)))
                .Where(pair => mergedKeys.Contains(pair.link.FormKey.ModKey))
                .Select(pair => $"{Label(pair.record)} -> {pair.link.FormKey}")
                .Distinct()
                .ToList();
            if (leftovers.Count > 0)
                throw new RunFailedException(
                    "The patch still links to records of plugins outside the load order, which would make them masters: "
                    + string.Join("; ", leftovers.Take(10)) + (leftovers.Count > 10 ? $" (and {leftovers.Count - 10} more)" : ""));
            return copied;
        }

        /// <summary>A disabled source's other records are not merged in; disabling that plugin loses them.</summary>
        private void WarnAboutLostContent(LookSource source, IReadOnlyDictionary<FormKey, FormKey> copied)
        {
            var lost = source.Mod.EnumerateMajorRecords()
                .Where(record => !ItemType.IsItem(record) && !copied.ContainsKey(record.FormKey) && !_lookDecisions.ContainsKey(record.FormKey))
                .GroupBy(RecordTypeName)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Count()} {group.Key}")
                .ToList();
            if (lost.Count > 0)
                _log.Warning($"{source.ModKey} is not in the load order, and only its item looks are merged in. Its other content is lost: {string.Join(", ", lost)}.");
        }
    }

    /// <summary>
    /// The sources from weakest to strongest: by their MO2 mod's priority, and within one mod by load order position
    /// (a plugin outside the load order counts as lowest), then by settings list order.
    /// </summary>
    private static IReadOnlyList<LookSource> InPrecedenceOrder(IReadOnlyList<LookSource> sources, IReadOnlyList<ISkyrimModGetter> loadOrder)
    {
        var positions = loadOrder.Select((mod, index) => (mod.ModKey, index)).ToDictionary(pair => pair.ModKey, pair => pair.index);
        return sources
            .Select((source, listIndex) => (source, listIndex))
            .OrderBy(entry => entry.source.ModPriority)
            .ThenBy(entry => entry.source.InLoadOrder && positions.TryGetValue(entry.source.ModKey, out var position) ? position : -1)
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
