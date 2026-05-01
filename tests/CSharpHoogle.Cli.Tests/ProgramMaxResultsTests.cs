using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

// Console.Error is process-global; serialize tests that redirect it to avoid
// xUnit's default cross-class parallelism racing on the writer.
[CollectionDefinition("Console", DisableParallelization = true)]
public class ConsoleCollection { }

[Collection("Console")]
public class ProgramMaxResultsTests
{
    [Fact]
    public void Main_MaxResults_MissingArgument_ReturnsExit2_WithError()
    {
        var (exit, stderr) = RunMain("--max-results");

        Assert.Equal(2, exit);
        Assert.Contains("--max-results requires a number argument.", stderr);
    }

    [Fact]
    public void Main_MaxResults_NonIntegerArgument_ReturnsExit2_WithError()
    {
        var (exit, stderr) = RunMain("--max-results", "abc");

        Assert.Equal(2, exit);
        Assert.Contains("expects integer", stderr);
        Assert.Contains("abc", stderr);
    }

    [Fact]
    public void Main_MaxResults_NegativeArgument_ReturnsExit2_WithError()
    {
        var (exit, stderr) = RunMain("--max-results", "-5");

        Assert.Equal(2, exit);
        Assert.Contains("non-negative", stderr);
        Assert.Contains("-5", stderr);
    }

    [Fact]
    public void LimitMatches_DefaultMinusOne_ReturnsAllMatches()
    {
        var matches = BuildMatches(5);

        var result = Program.LimitMatches(matches, -1).ToList();

        Assert.Equal(5, result.Count);
        Assert.Equal(matches, result);
    }

    [Fact]
    public void LimitMatches_Zero_ReturnsEmpty()
    {
        var matches = BuildMatches(5);

        var result = Program.LimitMatches(matches, 0).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void LimitMatches_LessThanCount_ReturnsFirstN()
    {
        var matches = BuildMatches(5);

        var result = Program.LimitMatches(matches, 2).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(matches.Take(2), result);
    }

    [Fact]
    public void LimitMatches_GreaterThanCount_ReturnsAll()
    {
        var matches = BuildMatches(5);

        var result = Program.LimitMatches(matches, 100).ToList();

        Assert.Equal(5, result.Count);
        Assert.Equal(matches, result);
    }

    private static (int ExitCode, string Stderr) RunMain(params string[] args)
    {
        var originalErr = Console.Error;
        var capture = new StringWriter();
        Console.SetError(capture);
        try
        {
            var exit = Program.Main(args);
            return (exit, capture.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    private static IReadOnlyList<CachedMethod> BuildMatches(int count)
    {
        var list = new List<CachedMethod>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new CachedMethod(
                $"Test.Type.Method{i}",
                "void",
                Array.Empty<string>(),
                Array.Empty<string>(),
                false,
                "",
                null,
                new MethodSource("assembly", "Test"),
                RequiredParameterCount: 0));
        }
        return list;
    }
}
