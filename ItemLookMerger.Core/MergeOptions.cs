using Mutagen.Bethesda.Plugins;

namespace ItemLookMerger.Core;

/// <summary>The settings the run needs, free of Synthesis types (the entry project maps its settings onto this).</summary>
/// <param name="PluginsToForward">The look sources. Between them, the one whose MO2 mod has the higher priority wins, not the list order.</param>
/// <param name="ForwardFolderName">MO2 mod folder (under the instance's mods folder) that receives assets and CREDITS.md.</param>
public sealed record MergeOptions(
    IReadOnlyList<ModKey> PluginsToForward,
    bool ForwardNames,
    bool VerboseLogging,
    bool AllowMerging,
    string? ForwardFolderName);
