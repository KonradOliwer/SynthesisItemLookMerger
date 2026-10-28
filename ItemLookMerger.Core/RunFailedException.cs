namespace ItemLookMerger.Core;

/// <summary>The run cannot go on; thrown so that Synthesis marks the run failed and writes no output.</summary>
public sealed class RunFailedException : InvalidOperationException
{
    public RunFailedException(string message)
        : base(message)
    {
    }

    /// <summary>Set when the failure is a previous folder that this run did not make, so the rollback must leave it alone and report nothing left behind.</summary>
    internal bool PreviousNotFromThisRun { get; init; }

    /// <summary>A failure caused by another exception, which stays reachable as <see cref="Exception.InnerException"/>.</summary>
    public RunFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
