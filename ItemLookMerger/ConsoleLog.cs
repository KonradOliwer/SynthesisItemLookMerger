using ItemLookMerger.Core;

namespace ItemLookMerger;

/// <summary>
/// The run's log on the console, which Synthesis captures. Synthesis logs standard output at information level and standard error at
/// error level (held back until the process ends); it has no warning level. So errors go to standard error and warnings stay on
/// standard output with a "Warning:" prefix, in order with the rest of the run's log.
/// </summary>
/// <param name="output">Standard output; the console's when null (read per line, as the log file tee replaces it).</param>
/// <param name="error">Standard error; the console's when null.</param>
internal sealed class ConsoleLog(bool verbose, TextWriter? output = null, TextWriter? error = null) : ILog
{
    public void Info(string text) => (output ?? Console.Out).WriteLine(text);

    public void Warning(string text) => (output ?? Console.Out).WriteLine($"Warning: {text}");

    public void Error(string text) => (error ?? Console.Error).WriteLine($"Error: {text}");

    public void Verbose(string text)
    {
        if (verbose) (output ?? Console.Out).WriteLine($"  {text}");
    }
}
