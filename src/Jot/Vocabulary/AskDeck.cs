namespace Jot.Vocabulary;

/// <summary>One card the owner explicitly answered. Ignoring a card produces NO answer — ux §4.5:
/// "ignoring is not rejecting", and only an explicit verdict moves the learning net.</summary>
public sealed record AskAnswer(CorrectionRecord Record, bool KeepOriginal)
{
    /// <summary>The verdict string <see cref="CorrectionProvenance.SetVerdict"/> takes.</summary>
    public string Verdict => KeepOriginal ? "original" : "term";
}

/// <summary>
/// The ask deck's state machine, deliberately separated from <c>AskCardWindow</c> so every rule in
/// ux §4.5 is unit-testable with no window, no dispatcher and no microphone.
///
/// The card is ALWAYS drawn around an APPLIED correction — D8 plus the caller-side
/// <c>Outcome == "applied"</c> filter in <see cref="VocabularyRunner.SelectAsks"/> guarantee it — so
/// "keep original" means REVERT a change already made in the pending text, and "use the term" means
/// confirm it. There is no third option: <c>Alternates</c> is always empty on this engine path.
/// </summary>
public sealed class AskDeck
{
    private readonly List<AskPolicy.Selection> _cards;
    private readonly List<AskAnswer> _answers = [];

    public AskDeck(IReadOnlyList<AskPolicy.Selection> cards) => _cards = [.. cards];

    public int Count => _cards.Count;

    /// <summary>Zero-based position of the card on screen.</summary>
    public int Index { get; private set; }

    /// <summary>True once every card has been answered or skipped — the deck is done.</summary>
    public bool IsComplete => Index >= _cards.Count;

    /// <summary>Any explicit interaction with ANY card. Drives the §4.5 timeout branch.</summary>
    public bool Engaged { get; private set; }

    public AskPolicy.Selection? Current => IsComplete ? null : _cards[Index];

    /// <summary>Monospaced "N of M" counter.</summary>
    public string PositionText => $"{Math.Min(Index + 1, Count)} of {Count}";

    public IReadOnlyList<AskAnswer> Answers => _answers;

    /// <summary>An explicit pick. <paramref name="keepOriginal"/> true reverts this occurrence.</summary>
    public void Answer(bool keepOriginal)
    {
        if (Current is not { } card) return;
        Engaged = true;
        _answers.Add(new AskAnswer(card.Record, keepOriginal));
        Index++;
    }

    /// <summary>Esc: keep the original-as-written (i.e. leave the applied term alone) and advance,
    /// writing NOTHING. Not an answer — see <see cref="AskAnswer"/>.</summary>
    public void Skip()
    {
        if (IsComplete) return;
        Engaged = true;      // Esc is an interaction: the user is triaging, not ignoring
        Index++;
    }

    /// <summary>
    /// The countdown expired. §4.5, resolved on iOS as UX-Q2: card 1 with ZERO interaction skips the
    /// WHOLE deck ("don't march the user through 3×10s they're ignoring"); after any interaction only
    /// the current card is skipped. No banner either way — a banner is itself a nag.
    /// </summary>
    public void TimeOut()
    {
        if (IsComplete) return;
        if (!Engaged) { Index = _cards.Count; return; }
        Index++;
    }

    /// <summary>Abandon the rest of the deck, keeping every answer already given. The single exit
    /// used by click-away, a new recording, and the wall-clock deadline.</summary>
    public void Abandon() => Index = _cards.Count;

    // MARK: - Splice

    /// <summary>One splice this deck made, in the units <see cref="CorrectionProvenance.NoteSelfEdit"/>
    /// speaks (grapheme clusters), plus the text as it stood immediately after it. Reporting our own
    /// edits is what keeps the review surface's rows alive across a revert: a blind diff of
    /// "Nemotron" → "neumotron" maps the anchor into the middle of the new word, and the row would
    /// then be hidden as unresolvable.</summary>
    public readonly record struct AskSplice(
        string RecordKey, int Start, int OldLength, int NewLength, string TextAfter);

    /// <summary>The spliced text plus the report of what was spliced.</summary>
    public readonly record struct SpliceResult(string Text, IReadOnlyList<AskSplice> Splices);

    /// <summary>
    /// Apply the answers to the pending text. Only a "keep original" pick changes anything — the
    /// term is already in the text — so this is a revert, resolved STRICTLY at the recorded anchor
    /// and skipped outright if it does not resolve ("corrupting the pasted text is worse than
    /// leaving the default").
    ///
    /// Front-to-back with a running offset, NOT back-to-front: going backwards would leave every
    /// already-recorded splice start stale the moment an earlier one shortened the text, and those
    /// starts are what gets reported to the ledger.
    /// </summary>
    public static SpliceResult ApplyAnswers(string text, IReadOnlyList<AskAnswer> answers)
    {
        var splices = new List<AskSplice>();
        int shift = 0;   // grapheme clusters, the unit the anchors use

        foreach (AskAnswer a in answers.Where(a => a.KeepOriginal)
                                       .OrderBy(a => a.Record.PublishedStart))
        {
            if (!VocabularySplice.TryResolve(text, a.Record.PublishedStart + shift, a.Record.Term,
                    out VocabularySplice.Span span))
                continue;

            int start = VocabularySplice.GraphemeIndex(text, span.Start);
            int oldLength = VocabularySplice.GraphemeLength(a.Record.Term);
            int newLength = VocabularySplice.GraphemeLength(a.Record.OriginalWord);
            text = VocabularySplice.Replace(text, span, a.Record.OriginalWord);
            splices.Add(new AskSplice(a.Record.Key, start, oldLength, newLength, text));
            shift += newLength - oldLength;
        }

        return new SpliceResult(text, splices);
    }
}
