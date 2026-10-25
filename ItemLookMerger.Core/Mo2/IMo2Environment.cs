namespace ItemLookMerger.Core.Mo2;

/// <summary>The result of looking for MO2 among the ancestors of this process.</summary>
/// <param name="ExecutablePath">The full path of the nearest ModOrganizer.exe ancestor, or null.</param>
/// <param name="Problem">Why no path could be found, when MO2 was seen but its path could not be read.</param>
public sealed record Mo2ProcessLookup(string? ExecutablePath, string? Problem = null);

/// <summary>What the MO2 search needs from the machine; tests replace it with fixed answers.</summary>
public interface IMo2Environment
{
    Mo2ProcessLookup FindModOrganizer();

    /// <summary>The %LOCALAPPDATA% folder, or null.</summary>
    string? LocalAppData { get; }

    /// <summary>The name of MO2's current global instance (from MO2's registry key), or null.</summary>
    string? CurrentGlobalInstance();
}
