using ItemLookMerger.Core;

namespace ItemLookMerger;

/// <summary>The run's log on the console, which Synthesis captures. Errors go to standard error.</summary>
internal sealed class ConsoleLog(bool verbose) : ILog
{
    public void Info(string text) => Console.WriteLine(text);

    public void Warning(string text) => Console.WriteLine($"Warning: {text}");

    public void Error(string text) => Console.Error.WriteLine($"Error: {text}");

    public void Verbose(string text)
    {
        if (verbose) Console.WriteLine($"  {text}");
    }
}
