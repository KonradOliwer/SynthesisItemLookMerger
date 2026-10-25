using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core;

/// <summary>The settings the run needs, free of Synthesis types (the entry project maps its settings onto this).</summary>
/// <param name="PluginsToForward">
/// The look plugins; each must be enabled (in the load order) when the run starts. Between them, the one later in the load order wins,
/// not the list order.
/// </param>
/// <param name="AllowMerging">Makes the patch self-contained (no look plugin as a master) and copies the look plugins' assets.</param>
/// <param name="ForwardFolderName">MO2 mod folder (under the instance's mods folder) that receives assets and CREDITS.md.</param>
/// <param name="OnTypeDiscrepancy">What to do when a look plugin gives an item another weapon or armor type than its stats side has.</param>
public sealed record MergeOptions(
    IReadOnlyList<ModKey> PluginsToForward,
    bool ForwardNames,
    bool VerboseLogging,
    bool AllowMerging,
    string? ForwardFolderName,
    TypeDiscrepancyHandling OnTypeDiscrepancy = TypeDiscrepancyHandling.TakeVisualModWeaponType);
