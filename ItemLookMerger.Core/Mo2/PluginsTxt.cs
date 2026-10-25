using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core.Mo2;

/// <summary>One line of a profile's plugins.txt.</summary>
public sealed record PluginEntry(ModKey Plugin, bool Active);

/// <summary>Reads a profile's plugins.txt: "*Plugin.esp" is active, "Plugin.esp" inactive, "#" a comment.</summary>
internal static class PluginsTxt
{
    /// <summary>Every plugin line in file order (the load order), active or not.</summary>
    public static IReadOnlyList<PluginEntry> Entries(string text)
    {
        var entries = new List<PluginEntry>();
        foreach (var rawLine in text.TrimStart('﻿').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var active = line[0] == '*';
            if (ModKey.TryFromFileName((active ? line[1..] : line).Trim(), out var key)) entries.Add(new PluginEntry(key, active));
        }
        return entries;
    }
}
