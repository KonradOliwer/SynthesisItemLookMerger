namespace ItemLookMerger.Core;

/// <summary>Where the run writes its log. The entry project prints to the console; tests collect the lines.</summary>
public interface ILog
{
    void Info(string text);

    void Warning(string text);

    void Error(string text);

    /// <summary>Only shown with verbose logging on.</summary>
    void Verbose(string text);
}
