namespace ItemLookMerger.Core;

/// <summary>The run cannot go on; thrown so that Synthesis marks the run failed and writes no output.</summary>
public sealed class RunFailedException(string message) : InvalidOperationException(message);
