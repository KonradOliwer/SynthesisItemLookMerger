using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core.Mo2;

/// <summary>Reads a profile's plugins.txt: "*Plugin.esp" is active, "Plugin.esp" inactive, "#" a comment.</summary>
internal static class PluginsTxt
{
    public static IReadOnlyList<ModKey> ActivePlugins(string text)
    {
        var active = new List<ModKey>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 2 || line[0] != '*') continue;
            if (ModKey.TryFromFileName(line[1..].Trim(), out var key)) active.Add(key);
        }
        return active;
    }
}
