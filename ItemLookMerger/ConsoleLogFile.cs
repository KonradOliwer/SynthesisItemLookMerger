using System.Text;

namespace ItemLookMerger;

/// <summary>
/// Mirrors Console.Out and Console.Error into a file while keeping normal console output
/// (option "--log-file &lt;path&gt;", not a Synthesis option). Needed when MO2 launches the patcher
/// directly, where output cannot be redirected.
/// </summary>
internal sealed class ConsoleLogFile : IDisposable
{
    private const string Option = "--log-file";

    private readonly StreamWriter _file;
    private readonly TextWriter _synchronizedFile;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;

    private ConsoleLogFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        _file = new StreamWriter(path, append: false, Encoding.UTF8) { AutoFlush = true };
        _synchronizedFile = TextWriter.Synchronized(_file);
        _originalOut = Console.Out;
        _originalError = Console.Error;
        Console.SetOut(new TeeWriter(_originalOut, _synchronizedFile));
        Console.SetError(new TeeWriter(_originalError, _synchronizedFile));
    }

    /// <summary>
    /// Removes "--log-file &lt;path&gt;" (or a trailing "--log-file" without a path) from
    /// <paramref name="args"/> and starts logging if a path was given.
    /// </summary>
    public static ConsoleLogFile? StartIfRequested(ref string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, Option, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[index + 1])
            || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            args = args.Take(index).Concat(args.Skip(index + 1)).ToArray();
            Console.Error.WriteLine($"Warning: {Option} needs a file path; no log file is written.");
            return null;
        }
        var path = args[index + 1];
        args = args.Take(index).Concat(args.Skip(index + 2)).ToArray();
        return new ConsoleLogFile(path);
    }

    public void WriteToFileOnly(string text) => _synchronizedFile.WriteLine(text);

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _file.Dispose();
    }

    private sealed class TeeWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => first.Encoding;
        public override void Write(char value) { first.Write(value); second.Write(value); }
        public override void Write(char[] buffer, int index, int count) { first.Write(buffer, index, count); second.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { first.Write(buffer); second.Write(buffer); }
        public override void Write(string? value) { first.Write(value); second.Write(value); }
        public override void WriteLine(string? value) { first.WriteLine(value); second.WriteLine(value); }
        public override void Flush() { first.Flush(); second.Flush(); }
    }
}
