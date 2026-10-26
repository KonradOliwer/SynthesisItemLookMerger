namespace ItemLookMerger.Tests;

public class ConsoleLogTests
{
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    [Fact]
    public void Errors_go_to_standard_error_and_everything_else_to_standard_output_in_order()
    {
        var log = new ConsoleLog(verbose: true, _output, _error);

        log.Info("one");
        log.Warning("two");
        log.Verbose("three");
        log.Error("four");

        Assert.Equal($"one{Environment.NewLine}Warning: two{Environment.NewLine}  three{Environment.NewLine}", _output.ToString());
        Assert.Equal($"Error: four{Environment.NewLine}", _error.ToString());
    }

    [Fact]
    public void Verbose_lines_are_dropped_without_verbose_logging()
    {
        new ConsoleLog(verbose: false, _output, _error).Verbose("hidden");

        Assert.Equal(string.Empty, _output.ToString());
    }
}
