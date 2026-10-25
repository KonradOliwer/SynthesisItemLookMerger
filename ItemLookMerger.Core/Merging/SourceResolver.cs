using System.IO.Abstractions;
using ItemLookMerger.Core.Assets;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>
/// Step 1: turns the chosen MO2 mods into look sources. Each listed mod folder contributes every plugin at its top; the mod gives the
/// source its priority, assets and credits. A plugin in the load order is taken from it; any other is read from the mod folder
/// (enabled or disabled) when merging is allowed. A listed mod that is unknown or has no usable plugin fails the run, before
/// anything is changed, with one error naming every such entry.
/// </summary>
internal static class SourceResolver
{
    public static IReadOnlyList<LookSource> Resolve(
        IReadOnlyList<string?> modNames,
        IReadOnlyList<ISkyrimModGetter> loadOrder,
        ModKey patchKey,
        Mo2Instance mo2,
        IPluginOpener opener,
        bool allowMerging,
        string? forwardFolderName,
        IFileSystem fileSystem,
        ILog log)
    {
        var loaded = loadOrder.ToDictionary(mod => mod.ModKey);
        var sources = new List<LookSource>();
        var problems = new List<string>();
        var names = modNames
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Every entry is checked before any plugin is opened, so one error can list all the unknown names.
        var mods = new List<ModListEntry>();
        foreach (var name in names)
        {
            var problem = CheckModName(name, mo2, forwardFolderName, fileSystem, out var mod);
            if (mod != null) mods.Add(mod);
            if (problem != null) problems.Add(problem);
        }
        if (problems.Count == 0)
        {
            var sourceMods = new Dictionary<ModKey, string>();
            foreach (var mod in mods)
            {
                var plugins = mo2.TopLevelPlugins(mod.Name);
                if (plugins.Count == 0)
                {
                    problems.Add($"\"{mod.Name}\" has no plugin (.esp, .esm or .esl) at its top level, in {mo2.ModFolder(mod.Name)}.");
                    continue;
                }
                foreach (var plugin in plugins)
                {
                    if (!ModKey.TryFromFileName(plugin, out var key))
                    {
                        problems.Add($"{plugin} in \"{mod.Name}\" is not a valid plugin file name.");
                        continue;
                    }
                    // Two listed mods with the same plugin would give two sources for one ModKey.
                    if (sourceMods.TryGetValue(key, out var other))
                    {
                        problems.Add($"{key} is in both \"{other}\" and \"{mod.Name}\"; list only one of them.");
                        continue;
                    }
                    sourceMods[key] = mod.Name;
                    var problem = ResolvePlugin(key, mod, loaded, patchKey, mo2, opener, allowMerging, log, out var source);
                    if (source != null) sources.Add(source);
                    if (problem != null) problems.Add(problem);
                }
            }
        }
        if (problems.Count == 0) return sources;

        // A partial merge would silently drop the looks the user asked for, so nothing is merged and no folder is touched.
        foreach (var source in sources.Where(source => !source.InLoadOrder)) (source.Mod as IDisposable)?.Dispose();
        throw new RunFailedException(
            $"{(problems.Count == 1 ? "An entry" : $"{problems.Count} entries")} of \"Mods to forward\" cannot be used, so the run stops "
            + "without changing anything (the forward folder and CREDITS.md are left as they are):\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>
    /// Why the listed name <paramref name="name"/> is not a mod folder this run can read from, or null with its modlist.txt entry.
    /// It must be a plain folder name, not the forward folder, in the profile's modlist.txt and present under the mods folder.
    /// </summary>
    private static string? CheckModName(string name, Mo2Instance mo2, string? forwardFolderName, IFileSystem fileSystem, out ModListEntry? mod)
    {
        mod = null;
        if (!ForwardFolder.IsPlainFolderName(fileSystem, name))
            return $"\"{name}\" is not a plain MO2 mod folder name (no \"..\", \\ or /, not a full path): use the name shown in MO2's left panel.";
        if (string.Equals(name, forwardFolderName?.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"\"{name}\" is the forward folder, this patcher's own output, and cannot be a look source.";

        var listed = mo2.FindListedMod(name);
        var exists = fileSystem.Directory.Exists(mo2.ModFolder(listed?.Name ?? name));
        if (listed != null && exists)
        {
            mod = listed;
            return null;
        }
        var reason = listed != null
            ? $"\"{name}\" is in modlist.txt of profile \"{mo2.ProfileName}\", but its folder {mo2.ModFolder(listed.Name)} does not exist."
            : exists
                ? $"\"{name}\" is a folder in {mo2.ModsDirectory} but not in modlist.txt of profile \"{mo2.ProfileName}\"; refresh MO2 so it picks the mod up."
                : $"\"{name}\" is no mod of profile \"{mo2.ProfileName}\" (not in {mo2.ModListPath}, no folder in {mo2.ModsDirectory}).";
        var suggestions = mo2.SuggestMods(name).Where(suggestion => !string.Equals(suggestion, listed?.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return suggestions.Count == 0 ? reason : $"{reason} Did you mean {string.Join(", ", suggestions.Select(suggestion => $"\"{suggestion}\""))}?";
    }

    /// <summary>The source for <paramref name="key"/> of the listed <paramref name="mod"/>, or why it cannot be used.</summary>
    private static string? ResolvePlugin(
        ModKey key,
        ModListEntry mod,
        Dictionary<ModKey, ISkyrimModGetter> loaded,
        ModKey patchKey,
        Mo2Instance mo2,
        IPluginOpener opener,
        bool allowMerging,
        ILog log,
        out LookSource? source)
    {
        source = null;
        if (key == patchKey) return $"{key} in \"{mod.Name}\" is this patcher's own output and cannot be a look source.";
        if (loaded.TryGetValue(key, out var listed))
        {
            log.Verbose($"{key} of MO2 mod \"{mod.Name}\" is read from the load order.");
            source = new LookSource(listed, InLoadOrder: true, mod.Name, mo2.PriorityOf(mod));
            return null;
        }
        if (!allowMerging) return $"{key} in \"{mod.Name}\" is not in the load order. Turn on content merging to read it from the mod.";

        var path = Path.Combine(mo2.ModFolder(mod.Name), key.FileName);
        ISkyrimModGetter? plugin = null;
        try
        {
            plugin = opener.Open(path);
            // The overlay reads lazily; walking the records once finds a broken file here instead of halfway through the merge.
            _ = plugin.EnumerateMajorRecords().Count();
            var missingMasters = plugin.MasterReferences.Select(master => master.Master).Where(master => !loaded.ContainsKey(master)).ToList();
            if (missingMasters.Count > 0)
            {
                (plugin as IDisposable)?.Dispose();
                return $"{key} (MO2 mod \"{mod.Name}\") needs masters that are not in the load order: {string.Join(", ", missingMasters)}.";
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A corrupt or unreadable plugin file: Mutagen fails in many ways on broken data.
            (plugin as IDisposable)?.Dispose();
            return $"{key} (MO2 mod \"{mod.Name}\") cannot be read from {path} ({ex.GetType().Name}: {ex.Message}).";
        }
        log.Info($"{key} is not in the load order; read from MO2 mod \"{mod.Name}\"{(mod.Enabled ? "" : " (disabled)")}, its looks are merged in.");
        source = new LookSource(plugin, InLoadOrder: false, mod.Name, mo2.PriorityOf(mod));
        return null;
    }
}
