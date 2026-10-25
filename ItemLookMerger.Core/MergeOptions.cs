namespace ItemLookMerger.Core;

/// <summary>The settings the run needs, free of Synthesis types (the entry project maps its settings onto this).</summary>
/// <param name="ModsToForward">
/// The look mods: MO2 mod folder names, each contributing every plugin at its top. Between them, the one with the higher MO2 priority
/// wins, not the list order.
/// </param>
/// <param name="ForwardFolderName">MO2 mod folder (under the instance's mods folder) that receives assets and CREDITS.md.</param>
public sealed record MergeOptions(
    IReadOnlyList<string?> ModsToForward,
    bool ForwardNames,
    bool VerboseLogging,
    bool AllowMerging,
    string? ForwardFolderName);
