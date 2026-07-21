using Jot.Text;
using Xunit;

namespace Jot.Tests;

public sealed class ModelArtifactScrubberTests
{
    [Theory]
    [InlineData("25<unk>", "25%")]                 // digit + <unk> → percent
    [InlineData("25 <unk>", "25%")]                // spaces between digit and <unk> collapse
    [InlineData("hola<unk>mundo", "hola mundo")]   // non-digit <unk> → single space (never glued)
    [InlineData("no artifact here", "no artifact here")] // no <unk> → untouched (rule-0 fast path)
    [InlineData("<unk>", "")]                       // lone <unk> → space → trimmed to empty
    [InlineData("veinticinco 25<unk> por ciento", "veinticinco 25% por ciento")]
    public void Scrubs(string input, string expected) =>
        Assert.Equal(expected, ModelArtifactScrubber.Scrub(input));

    [Fact]
    public void Digit_unk_across_newline_keeps_the_break()
    {
        // rule 1 fails across \n\n; the stray <unk> becomes a space, trimmed at the boundary.
        Assert.Equal("25\n\n", ModelArtifactScrubber.Scrub("25\n\n<unk>"));
    }

    [Fact]
    public void Interior_paragraph_breaks_survive()
    {
        Assert.Equal("one\n\ntwo three", ModelArtifactScrubber.Scrub("one\n\ntwo<unk>three"));
    }

    [Fact]
    public void Is_idempotent()
    {
        foreach (string x in new[] { "25<unk>", "hola<unk>mundo", "25\n\n<unk>", "clean text", "<unk>" })
            Assert.Equal(ModelArtifactScrubber.Scrub(x), ModelArtifactScrubber.Scrub(ModelArtifactScrubber.Scrub(x)));
    }

    [Fact]
    public void Text_without_unk_is_returned_reference_unchanged()
    {
        // The rule-0 fast path must not touch spacing of a no-<unk> string (byte-identity for no-op languages).
        const string s = "double  spaces  and \n\n breaks  kept";
        Assert.Same(s, ModelArtifactScrubber.Scrub(s));
    }
}
