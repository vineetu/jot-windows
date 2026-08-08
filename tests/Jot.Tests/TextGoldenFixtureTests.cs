using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jot.Text;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// THE CROSS-PLATFORM CONFORMANCE CONTRACT for the text pipeline. These JSON files are copied
/// verbatim from jot-shared (<c>Tests/JotTextPipelineTests/Fixtures</c>, branch
/// <c>claude/jot-cli-v2</c>) — the same files the Swift package runs against. A failure here means
/// the Windows pipeline has diverged from the shipping Mac behaviour, and the divergence is the bug.
/// </summary>
public sealed class TextGoldenFixtureTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static T Load<T>(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "text", file + ".json");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
    }

    private sealed record StringCase(string Name, string Input, string Expected);

    [Fact]
    public void PostProcessing_MatchesGolden()
    {
        var cases = Load<List<StringCase>>("post_processing");
        // Anti-vacuum: a truncated or mis-parsed file that loads two cases would assert nothing about
        // paragraph preservation or the space-before-punctuation rule.
        Assert.Equal(9, cases.Count);
        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));

        foreach (StringCase c in cases)
        {
            Assert.True(c.Expected == PostProcessing.Apply(c.Input),
                $"post_processing[{c.Name}]\n  expected: {c.Expected}\n  actual:   {PostProcessing.Apply(c.Input)}");
        }
    }

    /// <summary>
    /// The insertion point, pinned from the outside. PostProcessing trims trailing whitespace, so if it
    /// ever runs LAST the pipeline stops ending in the single space that makes a pasted dictation join
    /// the next one — a silent regression no PostProcessing fixture can see.
    /// </summary>
    [Fact]
    public void PostProcessing_RunsInsideTheChain_NotAfterTheTrailingSpace()
    {
        // A tab collapses and the stray space before ";" closes (only PostProcessing does either —
        // FillerWordCleaner's own tidy covers neither), AND the trailing space survives, which it only
        // can if the pass ran BEFORE EnsureSingleTrailingSpace.
        Assert.Equal("Hello world; ", TextPipeline.Clean("Hello\tworld ;", "English", isNemotron: true));
        Assert.Equal("Hola mundo; ", TextPipeline.Clean("Hola\tmundo ;", "Spanish", isNemotron: true));
        // Paragraph boundaries survive the collapse.
        Assert.Equal("One.\n\nTwo. ", TextPipeline.Clean("One.\n\n  Two.", "English", isNemotron: true));
    }

    /// <summary>A spaceless / non-Latin script must not be whitespace-surgeried: the pipeline's promise
    /// for a language it does not clean is that the transcript comes back byte-identical.</summary>
    [Theory]
    [InlineData("こんにちは  世界 。", "Japanese")]
    [InlineData("안녕하세요  두  칸", "Korean")]
    [InlineData("построили  три  пирамид", "Russian")]
    public void PostProcessing_IsGatedOffForNonLatinScripts(string text, string language) =>
        Assert.Equal(text, TextPipeline.Clean(text, language, isNemotron: true));
}
