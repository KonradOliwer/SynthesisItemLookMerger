using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>A plugin whose item looks are forwarded.</summary>
/// <param name="InLoadOrder">False for a plugin read from its MO2 mod folder (disabled or missing): its records and assets are merged in.</param>
/// <param name="ModName">The MO2 mod that holds the plugin, or null (base game, or unknown).</param>
/// <param name="ModPriority">
/// The mod's MO2 priority (its position in the left panel; higher wins, as for file conflicts), or -1 without a mod.
/// Decides between look sources that change the same record.
/// </param>
public sealed record LookSource(ISkyrimModGetter Mod, bool InLoadOrder, string? ModName, int ModPriority = -1)
{
    public ModKey ModKey => Mod.ModKey;
}
