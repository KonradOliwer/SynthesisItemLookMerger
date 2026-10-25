using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace ItemLookMerger.Core.Merging;

/// <summary>A plugin of the load order whose item looks are forwarded.</summary>
/// <param name="ModName">The enabled MO2 mod MO2 serves the plugin file from, or null (base game, Creation Club, or unknown).</param>
/// <param name="ModPriority">
/// That mod's MO2 priority (its position in the left panel; higher wins, as for file conflicts), or -1 without a mod.
/// Decides which look mod provides an asset several of them have; between records, the load order decides.
/// </param>
public sealed record LookSource(ISkyrimModGetter Mod, string? ModName, int ModPriority = -1)
{
    public ModKey ModKey => Mod.ModKey;
}
