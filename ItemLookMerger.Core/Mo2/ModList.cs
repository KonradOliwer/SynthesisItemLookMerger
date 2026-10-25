namespace ItemLookMerger.Core.Mo2;

/// <summary>
/// One mod of a profile's modlist.txt. Priority 0 is the top line, the mod that wins file conflicts; -1 is Overwrite, above them all.
/// </summary>
public sealed record ModListEntry(string Name, bool Enabled, int Priority);

/// <summary>Reads a profile's modlist.txt: "+Mod" enabled, "-Mod" disabled, top line wins.</summary>
internal static class ModList
{
    private const string SeparatorSuffix = "_separator";

    /// <summary>
    /// The managed mods in file order. Skipped: comments, "*" entries (unmanaged content such as DLCs and Creation Club,
    /// which lives in the game's Data folder) and separators.
    /// </summary>
    public static IReadOnlyList<ModListEntry> Parse(string text)
    {
        var entries = new List<ModListEntry>();
        // A BOM that reached the text would hide the first line's flag, since Trim keeps it.
        foreach (var rawLine in text.TrimStart('﻿').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length < 2 || line[0] is not ('+' or '-')) continue;
            var name = line[1..].Trim();
            if (name.Length == 0 || name.EndsWith(SeparatorSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(new ModListEntry(name, line[0] == '+', entries.Count));
        }
        return entries;
    }
}
