using System.Text;
using ItemLookMerger.Core.Merging;
using ItemLookMerger.Core.Mo2;
using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core.Credits;

/// <summary>Where a plugin comes from: its MO2 mod and that mod's meta.ini facts, when there are any.</summary>
public sealed record PluginOrigin(ModKey Plugin, string? ModName, ModMeta? Meta);

/// <summary>
/// Step 7: CREDITS.md. One row per changed item with its look side (every look plugin it takes looks from, its own and those of the
/// forms its look depends on) and its stats side, then every mod involved with its
/// download facts. Base-game plugins and mods without a meta.ini are listed by plugin name only.
/// </summary>
internal static class CreditsWriter
{
    public const string FileName = "CREDITS.md";

    public static string Build(IReadOnlyList<ChangedItem> changes, Func<ModKey, PluginOrigin> originOf)
    {
        var origins = changes
            .SelectMany(change => change.LookPlugins.Append(change.StatsPlugin).Concat(change.Base is { } baseItem ? new[] { baseItem.StatsPlugin } : Array.Empty<ModKey>()))
            .Distinct()
            .ToDictionary(plugin => plugin, originOf);

        var text = new StringBuilder();
        text.AppendLine("# ItemLookMerger credits");
        text.AppendLine();
        text.AppendLine("These items take their look from one plugin and their stats from another. Thanks to the authors of the mods below.");
        text.AppendLine();
        text.AppendLine("## Items");
        text.AppendLine();
        text.AppendLine("| EditorID | Name | FormKey | Look | Stats |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var change in changes)
        {
            text.AppendLine(
                $"| {Cell(change.EditorID)} | {Cell(change.Name)} | `{change.FormKey}` | {string.Join(", ", change.LookPlugins.Select(plugin => Side(origins[plugin])))} | {StatsCell(change, origins)} |");
        }

        var withDetails = origins.Values
            .Where(origin => origin is { ModName: not null, Meta: not null })
            .GroupBy(origin => origin.ModName!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        var nameOnly = origins.Values
            .Where(origin => origin.ModName == null || origin.Meta == null)
            .Select(origin => origin.Plugin.FileName.String)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        text.AppendLine();
        text.AppendLine("## Mods");
        foreach (var mod in withDetails)
        {
            var meta = mod.First().Meta!;
            text.AppendLine();
            text.AppendLine($"### {mod.Key}");
            text.AppendLine();
            text.AppendLine($"- Plugins: {string.Join(", ", mod.Select(origin => origin.Plugin.FileName.String).Order(StringComparer.OrdinalIgnoreCase))}");
            if (meta.NexusUrl != null) text.AppendLine($"- Nexus: {meta.NexusUrl}");
            AppendFact(text, "modid", meta.ModId);
            AppendFact(text, "version", meta.Version);
            AppendFact(text, "installationFile", meta.InstallationFile);
            AppendFact(text, "repository", meta.Repository);
            AppendFact(text, "url", meta.Url);
        }
        if (nameOnly.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("### Other plugins (base game, or no meta.ini)");
            text.AppendLine();
            foreach (var plugin in nameOnly) text.AppendLine($"- {plugin}");
        }
        return text.ToString();
    }

    /// <summary>The origin of a plugin: a look source's listed mod, else the MO2 mod with the plugin file (the stats side).</summary>
    public static PluginOrigin Origin(Mo2Instance mo2, IReadOnlyList<LookSource> sources, ModKey plugin) =>
        sources.FirstOrDefault(source => source.ModKey == plugin) is { ModName: { } modName }
            ? new PluginOrigin(plugin, modName, mo2.ReadMeta(modName))
            : OriginFromMo2(mo2, plugin);

    /// <summary>The origin of a plugin from MO2: the mod with the plugin file (enabled first), else none.</summary>
    public static PluginOrigin OriginFromMo2(Mo2Instance mo2, ModKey plugin)
    {
        var mod = mo2.FindModWithFile(plugin.FileName);
        return mod == null ? new PluginOrigin(plugin, null, null) : new PluginOrigin(plugin, mod.Name, mo2.ReadMeta(mod.Name));
    }

    /// <summary>The stats side; for a weapon that took its type from a base item, also that item and where its stats come from.</summary>
    private static string StatsCell(ChangedItem change, IReadOnlyDictionary<ModKey, PluginOrigin> origins) =>
        change.Base is not { } baseItem
            ? Side(origins[change.StatsPlugin])
            : $"{Side(origins[change.StatsPlugin])}; weapon type stats from {Cell(baseItem.EditorID)} `{baseItem.FormKey}` ({Side(origins[baseItem.StatsPlugin])})";

    private static string Side(PluginOrigin origin) =>
        origin.ModName == null ? Cell(origin.Plugin.FileName) : $"{Cell(origin.Plugin.FileName)} ({Cell(origin.ModName)})";

    private static void AppendFact(StringBuilder text, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) text.AppendLine($"- {key}: {value}");
    }

    private static string Cell(string? value) => (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
