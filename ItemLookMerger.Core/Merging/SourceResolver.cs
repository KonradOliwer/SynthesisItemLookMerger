using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>
/// Step 1: turns the chosen plugins into look sources. Every listed plugin must be enabled, that is in the load order Synthesis was
/// given; its MO2 mod (for assets and credits) is the enabled mod MO2 serves the plugin file from. An empty list, the patch itself or a
/// plugin that isn't enabled fails the run before anything is changed, with one error naming every such entry.
/// </summary>
internal static class SourceResolver
{
    public static IReadOnlyList<LookSource> Resolve(
        IReadOnlyList<ModKey> plugins,
        IReadOnlyList<ISkyrimModGetter> loadOrder,
        ModKey patchKey,
        Mo2Instance mo2,
        ILog log)
    {
        var keys = plugins.Where(key => !key.IsNull).Distinct().ToList();
        // An empty list would write an empty patch and leave the last run's assets and credits describing looks it no longer forwards.
        if (keys.Count == 0)
            throw new RunFailedException(
                "\"Plugins to forward\" is empty, so there is nothing to forward and the run stops without changing anything. "
                + "Pick the plugins of your look mods in the patcher's settings.");

        var loaded = loadOrder.ToDictionary(mod => mod.ModKey);
        var problems = new List<string>();
        var notEnabled = new List<ModKey>();
        var sources = new List<LookSource>();
        foreach (var key in keys)
        {
            if (key == patchKey)
            {
                problems.Add($"{key} is this patcher's own output and cannot be a look source.");
                continue;
            }
            if (!loaded.TryGetValue(key, out var plugin))
            {
                notEnabled.Add(key);
                continue;
            }
            var mod = ServingMod(mo2, key);
            log.Verbose($"{key}: look source from {(mod == null ? "the game's Data folder" : $"MO2 mod \"{mod.Name}\"")}.");
            sources.Add(new LookSource(plugin, mod?.Name, mo2.PriorityOf(mod)));
        }
        if (notEnabled.Count > 0)
            problems.Add(
                $"{string.Join(", ", notEnabled)} {(notEnabled.Count == 1 ? "is" : "are")} not enabled (not in the load order Synthesis was given). "
                + "Enable the mod in MO2's left panel and the plugin in the right panel, then run again. "
                + "With content merging on, you can disable them again after the run.");
        if (problems.Count == 0) return sources;
        throw new RunFailedException(
            "\"Plugins to forward\" lists plugins that cannot be used, so the run stops without changing anything "
            + "(the forward folder and CREDITS.md are left as they are):\n  " + string.Join("\n  ", problems));
    }

    /// <summary>
    /// The enabled MO2 mod (Overwrite included) the game gets <paramref name="plugin"/> from: the highest-priority enabled mod with the
    /// file. Null when only disabled mods (or none) have it, so the game's Data folder provides it.
    /// </summary>
    private static ModListEntry? ServingMod(Mo2Instance mo2, ModKey plugin) =>
        mo2.FindModWithFile(plugin.FileName) is { Enabled: true } mod ? mod : null;
}
