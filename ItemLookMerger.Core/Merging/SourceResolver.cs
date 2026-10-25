using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>
/// Step 1: turns the chosen plugins into look sources. A plugin in the load order is taken from it; any other is read from
/// its MO2 mod folder when merging is allowed. A plugin that cannot be used is skipped with an error, the run goes on.
/// </summary>
internal static class SourceResolver
{
    public static IReadOnlyList<LookSource> Resolve(
        IReadOnlyList<ModKey> plugins,
        IReadOnlyList<ISkyrimModGetter> loadOrder,
        ModKey patchKey,
        Mo2Instance mo2,
        IPluginOpener opener,
        bool allowMerging,
        ILog log)
    {
        var loaded = loadOrder.ToDictionary(mod => mod.ModKey);
        var sources = new List<LookSource>();
        foreach (var key in plugins.Where(key => !key.IsNull).Distinct())
        {
            if (key == patchKey)
            {
                log.Error($"{key} is this patcher's own output and cannot be a look source; skipped.");
                continue;
            }
            if (loaded.TryGetValue(key, out var listed))
            {
                var holder = mo2.FindModWithFile(key.FileName);
                sources.Add(new LookSource(listed, InLoadOrder: true, holder?.Name, mo2.PriorityOf(holder)));
                continue;
            }
            if (!allowMerging)
            {
                log.Error($"{key} is not in the load order; skipped. Turn on content merging to read it from its MO2 mod.");
                continue;
            }
            var mod = mo2.FindModWithFile(key.FileName);
            if (mod == null)
            {
                log.Error($"{key} is neither in the load order nor in any MO2 mod of profile \"{mo2.ProfileName}\"; skipped.");
                continue;
            }

            ISkyrimModGetter? plugin = null;
            try
            {
                plugin = opener.Open(Path.Combine(mo2.ModFolder(mod.Name), key.FileName));
                // The overlay reads lazily; walking the records once finds a broken file here instead of halfway through the merge.
                _ = plugin.EnumerateMajorRecords().Count();
                var missingMasters = plugin.MasterReferences.Select(master => master.Master).Where(master => !loaded.ContainsKey(master)).ToList();
                if (missingMasters.Count > 0)
                {
                    log.Error($"{key} (MO2 mod \"{mod.Name}\") needs masters that are not in the load order: {string.Join(", ", missingMasters)}; skipped.");
                    (plugin as IDisposable)?.Dispose();
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A corrupt or unreadable plugin file: Mutagen fails in many ways on broken data.
                (plugin as IDisposable)?.Dispose();
                log.Error($"{key} (MO2 mod \"{mod.Name}\") cannot be read ({ex.GetType().Name}: {ex.Message}); skipped.");
                continue;
            }
            log.Info($"{key} is not in the load order; read from MO2 mod \"{mod.Name}\"{(mod.Enabled ? "" : " (disabled)")}, its looks are merged in.");
            sources.Add(new LookSource(plugin, InLoadOrder: false, mod.Name, mo2.PriorityOf(mod)));
        }
        return sources;
    }
}
