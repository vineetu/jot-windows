using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The ask deck's rules (ux §4.5), driven with no window and no dispatcher — which is the reason the
/// state machine is a separate class from <c>AskCardWindow</c> at all.
/// </summary>
public class AskDeckTests
{
    private static CorrectionRecord Applied(string original, string term, int start) => new()
    {
        OriginalWord = original,
        Term = term,
        Decision = "APPLY",
        Outcome = "applied",
        Confidence = 0.85f,
        Margin = 0,
        Unsure = false,
        OccurrenceIndex = 0,
        OriginalStart = start,
        OriginalLength = original.Length,
        PublishedStart = start,
        PublishedLength = term.Length,
    };

    private static AskPolicy.Selection Card(CorrectionRecord r) => new(r, false, null, null);

    private static AskDeck DeckOf(params CorrectionRecord[] records) =>
        new(records.Select(Card).ToList());

    [Fact]
    public void Answering_RecordsThePickAndAdvances()
    {
        AskDeck deck = DeckOf(Applied("neumotron", "Nemotron", 9), Applied("you jet", "UJET", 30));
        Assert.Equal("1 of 2", deck.PositionText);

        deck.Answer(keepOriginal: false);
        Assert.Equal("2 of 2", deck.PositionText);
        deck.Answer(keepOriginal: true);

        Assert.True(deck.IsComplete);
        Assert.Collection(deck.Answers,
            a => Assert.False(a.KeepOriginal),
            a => Assert.True(a.KeepOriginal));
        Assert.Equal(["term", "original"], deck.Answers.Select(a => a.Verdict));
    }

    /// <summary>"Ignoring is not rejecting" — Esc writes nothing at all, which is why an unanswered
    /// pair is still asked next time and why the doc's honest worst case says so.</summary>
    [Fact]
    public void Skipping_WritesNothing()
    {
        AskDeck deck = DeckOf(Applied("neumotron", "Nemotron", 9));
        deck.Skip();
        Assert.True(deck.IsComplete);
        Assert.Empty(deck.Answers);
    }

    /// <summary>UX-Q2, resolved on iOS: card 1 with ZERO interaction skips the WHOLE deck — don't
    /// march the user through 3 countdowns they are ignoring.</summary>
    [Fact]
    public void Timeout_OnAnUntouchedFirstCard_SkipsTheWholeDeck()
    {
        AskDeck deck = DeckOf(Applied("a", "A", 0), Applied("b", "B", 10), Applied("c", "C", 20));
        deck.TimeOut();
        Assert.True(deck.IsComplete);
        Assert.Empty(deck.Answers);
    }

    [Fact]
    public void Timeout_AfterAnInteraction_SkipsOnlyTheCurrentCard()
    {
        AskDeck deck = DeckOf(Applied("a", "A", 0), Applied("b", "B", 10), Applied("c", "C", 20));
        deck.Answer(keepOriginal: false);
        deck.TimeOut();

        Assert.False(deck.IsComplete);
        Assert.Equal("3 of 3", deck.PositionText);
        Assert.Single(deck.Answers);
    }

    [Fact]
    public void Abandon_KeepsEveryAnswerAlreadyGiven()
    {
        AskDeck deck = DeckOf(Applied("a", "A", 0), Applied("b", "B", 10));
        deck.Answer(keepOriginal: true);
        deck.Abandon();

        Assert.True(deck.IsComplete);
        Assert.Single(deck.Answers);
    }

    // MARK: - The splice

    [Fact]
    public void ApplyAnswers_RevertsOnlyTheKeepOriginalPicks()
    {
        const string text = "met with Nemotron about UJET today";
        IReadOnlyList<AskAnswer> answers =
        [
            new AskAnswer(Applied("neumotron", "Nemotron", 9), KeepOriginal: true),
            new AskAnswer(Applied("you jet", "UJET", 24), KeepOriginal: false),
        ];

        AskDeck.SpliceResult result = AskDeck.ApplyAnswers(text, answers);
        Assert.Equal("met with neumotron about UJET today", result.Text);
        AskDeck.AskSplice splice = Assert.Single(result.Splices);
        Assert.Equal(9, splice.Start);
        Assert.Equal("Nemotron".Length, splice.OldLength);
        Assert.Equal("neumotron".Length, splice.NewLength);
    }

    /// <summary>Two reverts of different lengths in one deck. The second card's anchor has to be
    /// carried through the first card's length delta — and the reported splice starts must be the
    /// FINAL ones, since that is what gets told to the ledger.</summary>
    [Fact]
    public void ApplyAnswers_CarriesLaterAnchorsThroughEarlierEdits()
    {
        const string text = "met with Nemotron about UJET today";
        IReadOnlyList<AskAnswer> answers =
        [
            new AskAnswer(Applied("neumotron", "Nemotron", 9), KeepOriginal: true),
            new AskAnswer(Applied("you jet", "UJET", 24), KeepOriginal: true),
        ];

        AskDeck.SpliceResult result = AskDeck.ApplyAnswers(text, answers);
        Assert.Equal("met with neumotron about you jet today", result.Text);
        Assert.Equal([9, 25], result.Splices.Select(s => s.Start));
        Assert.Equal("neumotron", result.Splices[0].TextAfter.Substring(9, result.Splices[0].NewLength));
        Assert.Equal("you jet", result.Splices[1].TextAfter.Substring(25, result.Splices[1].NewLength));
    }

    /// <summary>"Corrupting the pasted text is worse than leaving the default": an answer whose word
    /// is not where it was recorded is dropped, not guessed at.</summary>
    [Fact]
    public void ApplyAnswers_SkipsAnEditThatDoesNotResolveStrictly()
    {
        const string text = "met with Nemotron about the launch";
        IReadOnlyList<AskAnswer> answers =
            [new AskAnswer(Applied("you jet", "UJET", 24), KeepOriginal: true)];

        AskDeck.SpliceResult result = AskDeck.ApplyAnswers(text, answers);
        Assert.Equal(text, result.Text);
        Assert.Empty(result.Splices);
    }
}
