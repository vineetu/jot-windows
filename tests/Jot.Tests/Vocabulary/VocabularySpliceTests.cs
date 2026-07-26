using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The one splice rule shared by the ask card and the review surface. Strictness is the whole point:
/// iOS shipped a nearest-match fallback and deleted it after it "routinely resolved — sometimes onto
/// the wrong occurrence". A hidden row is a non-event; a row that edits the wrong word is data loss.
/// </summary>
public class VocabularySpliceTests
{
    private const string Text = "met with Nemotron about the launch";

    [Fact]
    public void Resolves_TheWordSittingExactlyAtTheAnchor()
    {
        Assert.True(VocabularySplice.TryResolve(Text, 9, "Nemotron", out VocabularySplice.Span span));
        Assert.Equal(9, span.Start);
        Assert.Equal("Nemotron", Text.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Resolves_CaseInsensitively()
        => Assert.True(VocabularySplice.TryResolve(Text, 9, "NEMOTRON", out _));

    /// <summary>The strict length+equality guard on the other platforms dropped a "Rama"→"Ramaa"
    /// replacement whose original carried a trailing period. Edge punctuation is trimmed, and the
    /// resolved span excludes it — so the splice never eats the sentence's full stop.</summary>
    [Fact]
    public void Resolves_ThroughTrailingPunctuation_WithoutSwallowingIt()
    {
        const string s = "we met Nemotron.";
        Assert.True(VocabularySplice.TryResolve(s, 7, "Nemotron", out VocabularySplice.Span span));
        Assert.Equal("Nemotron", s.Substring(span.Start, span.Length));
        Assert.Equal("we met Jot.", VocabularySplice.Replace(s, span, "Jot"));
    }

    [Fact]
    public void Resolves_MultiWordTerms()
    {
        const string s = "call Ramaa Nathan today";
        Assert.True(VocabularySplice.TryResolve(s, 5, "Ramaa Nathan", out VocabularySplice.Span span));
        Assert.Equal("Ramaa Nathan", s.Substring(span.Start, span.Length));
    }

    [Fact]
    public void Fails_WhenTheAnchorIsMidWord()
        => Assert.False(VocabularySplice.TryResolve(Text, 10, "emotron", out _));

    [Fact]
    public void Fails_WhenADifferentWordSitsAtTheAnchor()
        => Assert.False(VocabularySplice.TryResolve(Text, 9, "Ideaflow", out _));

    /// <summary>The wrong-occurrence bug, pinned: a MATCHING word elsewhere must NOT rescue a stale
    /// anchor. Only the anchor's own position counts.</summary>
    [Fact]
    public void Fails_EvenWhenTheSameWordExistsElsewhere()
    {
        const string s = "Nemotron and Nemotron";
        Assert.False(VocabularySplice.TryResolve(s, 5, "Nemotron", out _));
    }

    [Fact]
    public void Fails_PastTheEndOfTheText()
        => Assert.False(VocabularySplice.TryResolve(Text, 999, "Nemotron", out _));

    /// <summary>Anchors are grapheme-cluster counts; .NET indexes UTF-16. Getting this wrong shifts
    /// every splice after the first astral character in a transcript.</summary>
    [Fact]
    public void GraphemeOffsets_MapToUtf16Indices()
    {
        const string s = "hi \U0001F600 Nemotron";   // the emoji is ONE grapheme, TWO UTF-16 units
        int anchor = VocabularySplice.GraphemeIndex(s, s.IndexOf("Nemotron", StringComparison.Ordinal));
        Assert.Equal(5, anchor);
        Assert.Equal(s.IndexOf("Nemotron", StringComparison.Ordinal), VocabularySplice.Utf16Index(s, anchor));
        Assert.True(VocabularySplice.TryResolve(s, anchor, "Nemotron", out _));
    }

    [Fact]
    public void ContextAround_EllipsizesOnlyWhereItTruncates()
    {
        Assert.True(VocabularySplice.TryResolve(Text, 9, "Nemotron", out VocabularySplice.Span span));
        VocabularySplice.Context ctx = VocabularySplice.ContextAround(Text, span);
        Assert.Equal("Nemotron", ctx.Word);
        Assert.Equal("met with ", ctx.Before);          // start of string — no leading ellipsis
        Assert.Equal(" about the launch", ctx.After);   // end of string — no trailing ellipsis
    }

    // MARK: - Selection spans (the right-click "Add to Vocabulary…" range)

    /// <summary>WPF's double-click word selection carries the TRAILING whitespace. Splicing the
    /// trimmed term over that raw range is what published "My name is VineetSriram."</summary>
    [Theory]
    [InlineData("My name is Venith Sriram.", 11, 7, 11, 6)]   // "Venith " → "Venith"
    [InlineData("Venith is here", 0, 7, 0, 6)]                // at the start
    [InlineData("hello Venith", 6, 6, 6, 6)]                  // at the end, nothing to trim
    [InlineData("Venith, hello", 0, 6, 0, 6)]                 // punctuation is not whitespace: kept
    [InlineData("Venith\nhello", 0, 7, 0, 6)]                 // a newline is whitespace too
    [InlineData("call Venith now", 4, 8, 5, 6)]               // leading whitespace trimmed as well
    [InlineData("call Sri Ram now", 5, 8, 5, 7)]              // multi-word: INTERNAL space is kept
    public void SelectionSpan_TrimsOnlyTheWhitespaceAtTheEdges(
        string text, int start, int length, int expectedStart, int expectedLength)
    {
        Assert.True(VocabularySplice.TrySelectionSpan(text, start, length, out VocabularySplice.Span span));
        Assert.Equal(expectedStart, span.Start);
        Assert.Equal(expectedLength, span.Length);
    }

    /// <summary>Nothing to replace is NOT an empty replace — a caller that spliced a zero-length span
    /// would insert the term into whatever whitespace the owner happened to drag over.</summary>
    [Theory]
    [InlineData("call Venith now", 4, 1)]     // whitespace only
    [InlineData("call Venith now", 4, 0)]     // empty selection (a bare caret)
    [InlineData("call Venith now", -1, 4)]    // stale offsets from a control that moved on
    [InlineData("call Venith now", 12, 40)]   // runs off the end of the live text
    public void SelectionSpan_RefusesAnythingThatIsNotAWord(string text, int start, int length)
        => Assert.False(VocabularySplice.TrySelectionSpan(text, start, length, out _));
}
