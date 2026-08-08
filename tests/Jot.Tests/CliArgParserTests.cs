using Jot.Cli;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The parser's rules are a contract with scripted callers, and the failure they exist to prevent is
/// silent: a missing option value used to swallow the next token, so `--language --raw a.wav b.wav`
/// transcribed the wrong file and exited 0.
/// </summary>
public class CliArgParserTests
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--raw", "--no-vocab" };

    private static readonly Dictionary<string, string> Options = new(StringComparer.Ordinal)
    {
        ["--language"] = "--language",
        ["-o"] = "-o",
        ["--output"] = "-o",
        ["--vocab"] = "--vocab",
    };

    private static ParsedArgs Parse(params string[] tokens) =>
        CliArgParser.Parse(tokens, Flags, Options);

    [Fact]
    public void OptionValueThatLooksLikeAnOptionIsAMissingValue()
    {
        ParsedArgs p = Parse("--language", "--raw", "a.wav", "b.wav");
        Assert.Equal("missing value for --language", p.Error);
    }

    [Fact]
    public void TrailingOptionWithNoValueIsAMissingValue()
    {
        Assert.Equal("missing value for --vocab", Parse("a.wav", "--vocab").Error);
    }

    [Fact]
    public void UnknownDashTokenIsAUsageError()
    {
        Assert.Equal("unknown option '--badflag'", Parse("--badflag", "a.wav").Error);
    }

    [Fact]
    public void OutputAliasesCollapseToOneKey()
    {
        Assert.Equal("out.txt", Parse("-o", "out.txt", "a.wav").Options["-o"]);
        Assert.Equal("out.txt", Parse("--output", "out.txt", "a.wav").Options["-o"]);
    }

    [Fact]
    public void FlagsAndPositionalsSeparate()
    {
        ParsedArgs p = Parse("--raw", "a.wav", "--language", "de-DE");
        Assert.Contains("--raw", p.Flags);
        Assert.Equal("de-DE", p.Options["--language"]);
        Assert.Equal(["a.wav"], p.Positionals);
        Assert.Null(p.Error);
    }

    [Fact]
    public void EverythingAfterEndOfOptionsIsPositional()
    {
        ParsedArgs p = Parse("--raw", "--", "--language", "-h");
        Assert.Contains("--raw", p.Flags);
        Assert.Equal(["--language", "-h"], p.Positionals);
        Assert.Null(p.Error);
    }

    [Fact]
    public void HelpDetectionNeverLooksPastEndOfOptions()
    {
        Assert.Contains("-h", CliArgParser.PreEndOfOptions(["transcribe", "-h"]));
        Assert.DoesNotContain("-h", CliArgParser.PreEndOfOptions(["transcribe", "--", "-h"]));
    }

    [Fact]
    public void VersionAndStreamDetectionNeverLookPastEndOfOptions()
    {
        IReadOnlyList<string> head = CliArgParser.PreEndOfOptions(["transcribe", "--", "--version", "--stream"]);
        Assert.Equal(["transcribe"], head);
    }
}
