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

/// <param name="DuplicatedRecords">Look records of the look plugins copied into the patch as new records (self-contained patch only).</param>
public sealed record MergeResult(
    IReadOnlyList<ChangedItem> Changes, int IdenticalSkipped, int DuplicatedRecords, int ForwardedLookRecords, IReadOnlyList<AssetNeed> Assets);

/// <summary>
/// Steps 2 to 5: picks the item overrides of the sources and the look records (ARMA, TXST, STAT, PROJ) they override, compares
/// their look with the stats side's and forwards the differing looks into the patch. For a self-contained patch it then copies the
/// look records the forwarded looks use out of the look plugins, so the patch needs none of them as a master.
/// </summary>
internal static class LookMerger
{
    /// <summary>
    /// The fields that hold an item's or a look record's look. Links in them to look records (ARMA, TXST, STAT, PROJ) of a look
    /// plugin are copied into a self-contained patch; any other link to a look plugin is not.
    /// </summary>
    private static readonly HashSet<string> VisualFields =
    [
        "Model", "Icons", "WorldModel", "Armature", "ScopeModel", "FirstPersonModel", "InventoryArt",
        "SkinTexture", "TextureSwapList", "Lod", "MuzzleFlashModel",
    ];

    /// <param name="loadOrder">The load order without the patch, lowest priority first. Every source is in it.</param>
    /// <param name="patch">The patch; records an earlier patcher of the Synthesis group put there count as the stats side.</param>
    /// <param name="sources">The look sources; between them, the one later in the load order wins.</param>
    /// <param name="selfContained">
    /// Make the patch independent of the look plugins (content merging on): copy the look records it uses, keep the stats side's
    /// value of any other field that would point at a look plugin, and fail if a link to a look plugin is left.
    /// </param>
    public static MergeResult Merge(
        IReadOnlyList<ISkyrimModGetter> loadOrder, ISkyrimMod patch, IReadOnlyList<LookSource> sources, bool forwardNames, bool selfContained, ILog log) =>
        new Run(loadOrder, patch, sources, forwardNames, selfContained, log).Execute();

    private sealed record Pick(LookSource Source, IMajorRecordGetter Record);

    /// <summary>The stats side of a record: the version to keep the stats of, and the plugin credited for it.</summary>
    private sealed record StatsSide(ModKey Plugin, IMajorRecordGetter Version, bool InPatch);

    /// <summary>What became of one look record: the version whose links are followed further, and the source it was forwarded from.</summary>
    private sealed record LookRecordDecision(IMajorRecordGetter? Effective, LookSource? ForwardedFrom, string Label);

    /// <summary>A record the run wrote into the patch, with a copy of its stats side's version from before the look went onto it.</summary>
    private sealed record Written(IMajorRecord Target, IMajorRecordGetter? Fallback, string Label);

    private sealed class Run
    {
        private readonly IReadOnlyList<ISkyrimModGetter> _loadOrder;
        private readonly ISkyrimMod _patch;
        private readonly IReadOnlyList<LookSource> _sources;
        private readonly bool _forwardNames;
        private readonly bool _selfContained;
        private readonly ILog _log;
        private readonly HashSet<ModKey> _sourceKeys;
        private readonly Dictionary<ModKey, ISkyrimModGetter> _loaded;
        private readonly IReadOnlyList<LookSource> _weakestFirst;
        private readonly Dictionary<ModKey, ILinkCache<ISkyrimMod, ISkyrimModGetter>> _sourceCaches = [];
        private readonly Dictionary<FormKey, (LookRecordType Type, Pick Pick)> _lookPicks = [];
        private readonly Dictionary<FormKey, LookRecordDecision> _lookDecisions = [];
        private readonly Dictionary<FormKey, bool> _reaches = [];
        private readonly List<string> _unresolved = [];
        private readonly List<AssetNeed> _assets = [];
        private readonly HashSet<FormKey> _forwardedLookRecords = [];
        private readonly List<Written> _written = [];
        private readonly Dictionary<FormKey, IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>?> _lookRecordContexts = [];

        public Run(IReadOnlyList<ISkyrimModGetter> loadOrder, ISkyrimMod patch, IReadOnlyList<LookSource> sources, bool forwardNames, bool selfContained, ILog log)
        {
            _loadOrder = loadOrder;
            _patch = patch;
            _sources = sources;
            _forwardNames = forwardNames;
            _selfContained = selfContained;
            _log = log;
            _sourceKeys = sources.Select(source => source.ModKey).ToHashSet();
            _loaded = loadOrder.ToDictionary(mod => mod.ModKey);
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
            if (_unresolved.Count > 0)
                throw new RunFailedException(
                    "These look plugins override records the load order does not have (a missing master or record):\n  "
                    + string.Join("\n  ", _unresolved));

            var duplicated = _selfContained ? MakeSelfContained() : 0;
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
                var fallback = Snapshot(stats.Version);
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
                if (_selfContained) _written.Add(new Written(target, fallback, label));
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
                    var fallback = Snapshot(stats.Version);
                    var target = type.GetOrAddAsOverride(_patch, stats.Version);
                    LookRecordType.Copy(target, pick.Record);
                    if (_selfContained) _written.Add(new Written(target, fallback, label));
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

        /// <summary>The version of a look record no look source overrides: the patch's, else the load order winner's.</summary>
        private IMajorRecordGetter? EffectiveVersion(LookRecordType type, FormKey formKey)
        {
            if (type.Find(_patch, formKey) is { } patched) return patched;
            for (var i = _loadOrder.Count - 1; i >= 0; i--)
            {
                if (type.Find(_loadOrder[i], formKey) is { } record) return record;
            }
            return null;
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
        /// they only come into the patch through a forwarded look that uses them.
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
        /// Step 5 (content merging on): makes the patch independent of the look plugins. In every record the run wrote, a link to a
        /// look plugin's own look record (ARMA, TXST, STAT, PROJ) in a look field is kept by copying that record into the patch as a new
        /// record, and the copies are handled the same way, so their look links are followed too. Any other link to a look plugin
        /// (keywords, enchantments, scripts, races, ...) is not followed: the field gets the stats side's value back, with a warning,
        /// and in a copied look record (which has no stats side) the link is cleared, with a warning. Afterwards no link in the patch
        /// may point at a look plugin; if one does, the run fails.
        /// </summary>
        private int MakeSelfContained()
        {
            var copies = new Dictionary<FormKey, FormKey>();
            var pending = new Queue<Written>(_written);
            while (pending.TryDequeue(out var entry))
            {
                foreach (var property in LinkProperties(entry.Target.GetType()))
                {
                    var lookLinks = LinksIn(property.GetValue(entry.Target))
                        .Select(link => link.FormKey)
                        .Where(formKey => _sourceKeys.Contains(formKey.ModKey))
                        .Distinct()
                        .ToList();
                    if (lookLinks.Count == 0) continue;

                    if (VisualFields.Contains(property.Name) && lookLinks.All(formKey => copies.ContainsKey(formKey) || LookRecordContext(formKey) != null))
                    {
                        foreach (var formKey in lookLinks.Where(formKey => !copies.ContainsKey(formKey)))
                        {
                            var context = LookRecordContext(formKey)!;
                            var duplicate = context.DuplicateIntoAsNewRecord(_patch, context.Record.EditorID);
                            copies[formKey] = duplicate.FormKey;
                            _log.Verbose($"Copied {Label(context.Record)} from {context.ModKey} into the patch as {duplicate.FormKey}.");
                            pending.Enqueue(new Written(duplicate, null, $"{Label(context.Record)} (copied from {context.ModKey})"));
                        }
                        continue;
                    }

                    var targets = string.Join(", ", lookLinks);
                    if (entry.Fallback != null)
                    {
                        property.SetValue(entry.Target, property.GetValue(entry.Fallback));
                        _log.Warning(
                            $"{entry.Label}: its {property.Name} points at {targets} of a look plugin, which is not a look record the patch can carry; "
                            + "the stats side's value is kept.");
                    }
                    else
                    {
                        entry.Target.RemapLinks(lookLinks.ToDictionary(formKey => formKey, _ => FormKey.Null));
                        _log.Warning(
                            $"{entry.Label}: its {property.Name} points at {targets}, which is not a look record the patch can carry; the link is cleared.");
                    }
                }
            }
            _patch.RemapLinks(copies);

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

            foreach (var source in _sources) ReportUncarriedContent(source, copies);
            return copies.Count;
        }

        /// <summary>The look record (ARMA, TXST, STAT, PROJ) a look plugin has under <paramref name="formKey"/>, the strongest version; null otherwise.</summary>
        private IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>? LookRecordContext(FormKey formKey)
        {
            if (_lookRecordContexts.TryGetValue(formKey, out var known)) return known;
            IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>? found = null;
            foreach (var source in Enumerable.Reverse(_weakestFirst))
            {
                found = LookRecordType.All
                    .Select(type => SourceCache(source).TryResolveContext(formKey, type.GetterType, out var context) ? context : null)
                    .FirstOrDefault(context => context != null);
                if (found != null) break;
            }
            _lookRecordContexts[formKey] = found;
            return found;
        }

        /// <summary>The records of a look plugin the patch doesn't carry, which are lost when that plugin is disabled.</summary>
        private void ReportUncarriedContent(LookSource source, IReadOnlyDictionary<FormKey, FormKey> copied)
        {
            var lost = source.Mod.EnumerateMajorRecords()
                .Where(record => !ItemType.IsItem(record) && !copied.ContainsKey(record.FormKey) && !_lookDecisions.ContainsKey(record.FormKey))
                .GroupBy(RecordTypeName)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => $"{group.Count()} {group.Key}")
                .ToList();
            if (lost.Count > 0)
                _log.Info($"{source.ModKey}: the patch carries its item looks only. Disabling it also drops its other content: {string.Join(", ", lost)}.");
        }
    }

    /// <summary>The settable properties of a record type whose values can hold form links.</summary>
    private static IEnumerable<PropertyInfo> LinkProperties(Type recordType) =>
        recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
            .Where(property => property.PropertyType != typeof(string) && !property.PropertyType.IsPrimitive && !property.PropertyType.IsEnum);

    /// <summary>The form links inside a property value: a link, a link container (models, scripts, effects, ...) or a list of either.</summary>
    private static IEnumerable<IFormLinkGetter> LinksIn(object? value) => value switch
    {
        null or string => [],
        IFormLinkGetter link => [link],
        IFormLinkContainerGetter container => container.EnumerateFormLinks(),
        IEnumerable items => items.Cast<object?>().SelectMany(LinksIn),
        _ => [],
    };

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
