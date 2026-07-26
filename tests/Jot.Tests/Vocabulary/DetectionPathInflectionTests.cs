using System.Collections.Generic;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The inflection guard on <see cref="VocabularyGate.ApplyFromDetections"/> — a fourth WINDOWS
/// DIVERGENCE, and the only thing that will notice if it is deleted.
///
/// <see cref="VocabularyGate.SkeletonOf"/> drops the apostrophe, so a POSSESSIVE measures as its own
/// plural and the term is applied over a word the engine got right. Found by measuring the SHIPPING
/// acoustic path on 1041 FLEURS clips: 5 of its 20 false applies were this one shape.
/// </summary>
public class DetectionPathInflectionTests
{
    private static VocabularyGate.Result Run(string text, string term, params string[] aliases) =>
        VocabularyGate.ApplyFromDetections(
            text,
            [new VocabularyGate.Detection(term, aliases, -1f, 0.5, 1.0)],
            2.0,
            EmbeddedCommonWordsProvider.Shared,
            "common-words");

    [Fact]
    public void APossessiveIsNeverRewrittenAsThePlural()
    {
        // skeleton("Mariana's") == skeleton("Marianas"), gap 0.00 — the plausibility brake cannot see
        // this one, and PreservingEdgePunctuation carries nothing because the trailing "s" is
        // alphanumeric, so an unguarded apply publishes "Marianas" and deletes the possessive.
        Assert.Equal(0.0, VocabularyGate.Gap("Mariana's", "Marianas", []));

        VocabularyGate.Result r = Run("the Mariana's trench expedition", "Marianas");
        Assert.Equal("the Mariana's trench expedition", r.Text);
        Assert.Equal(0, r.Applied);

        // Blocked, not dropped: the row still reaches review, like every other force-block.
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("kept", p.Outcome);
        Assert.Equal("BLOCK", p.Decision);
        Assert.False(p.AskCandidate);
    }

    [Theory]
    [InlineData("we asked the USOC's board", "USOC")]
    [InlineData("the Falkland's coastline", "Falklands")]
    [InlineData("Shayam's paper", "Shyam")]
    public void TheSameShapeAcrossTheMeasuredCases(string text, string term)
        => Assert.Equal(text, Run(text, term).Text);

    [Fact]
    public void ATermThatCARRIESAnApostropheStillCorrects()
    {
        // One-directional on purpose: an engine that drops the apostrophe is a real and common
        // mis-transcription, and refusing it too would trade one bug for another.
        VocabularyGate.Result r = Run("we met obrien yesterday", "O'Brien");
        Assert.Equal("we met O'Brien yesterday", r.Text);
        Assert.Equal(1, r.Applied);
    }

    [Fact]
    public void AnAliasCarryingTheApostropheIsEnoughToAllowIt()
    {
        // The user saying "when Jot hears X, spell it Y" is consent for exactly this pair, and the
        // alias is how that consent reaches the gate — so an apostrophe on the alias unblocks a span
        // the term's own spelling would have refused.
        VocabularyGate.Result r = Run("we met o'conners today", "OConnor", "O'Connor");
        Assert.Equal("we met OConnor today", r.Text);
    }

    [Fact]
    public void CurlyApostrophesCountToo()
    {
        // The offline cleanup pipeline and several keyboards produce U+2019, so a straight-quote-only
        // check would leave the hole open on exactly the text Jot itself writes.
        Assert.Equal("the Mariana’s trench", Run("the Mariana’s trench", "Marianas").Text);
    }
}
