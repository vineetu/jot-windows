using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The two detection-path defects the first real end-to-end run found (2026-07-25), pinned directly
/// against <see cref="VocabularyGate.ApplyFromDetections"/> so they need no model, no audio and no
/// recorder — just the exact strings the shipping engine produced.
///
/// THESE WERE WRITTEN WHEN NOTHING ELSE COVERED THEM. jot-shared's `applyFromDetections` had the same
/// holes and its fixtures were single-word terms only, so a fixture refresh could not validate — or
/// protect — the fix. Both sides now carry the guards and the fixtures to match
/// (`detections_apply_upstream`), but these stay: they assert the diagnostics and the span anchors,
/// which no fixture reads.
/// </summary>
public class DetectionPathDedupTests
{
    private static readonly EmbeddedCommonWordsProvider Common = EmbeddedCommonWordsProvider.Shared;

    private sealed class Sink : IDiagnosticsSink
    {
        public readonly List<string> Lines = [];
        public void Record(DiagnosticsCategory category, string message,
                           IReadOnlyDictionary<string, string> metadata) =>
            Lines.Add($"{message} {{{string.Join(", ", metadata.Select(kv => $"{kv.Key}={kv.Value}"))}}}");
    }

    private static VocabularyGate.Detection Det(
        string term, double start = 0.4, double end = 0.6, params string[] aliases) =>
        new(term, aliases, -0.36f, start, end);

    private static VocabularyGate.Result Apply(
        string transcript, VocabularyGate.Detection det, double duration = 1.0,
        IDiagnosticsSink? sink = null) =>
        VocabularyGate.ApplyFromDetections(transcript, [det], duration, Common, diagnostics: sink);

    // MARK: - Defect 1 · a multi-word term must never duplicate its own tail

    /// <summary>
    /// THE regression. Verbatim from the end-to-end run: the engine wrote "Claude code" (already
    /// almost right), and vocabulary turned it into "Claude Code code" — corrupting text that was
    /// fine, in a string that is pasted before anyone can review it.
    ///
    /// The pair is now claimed as a window when it is eligible (containing-window promotion). The
    /// published span is the same one absorb used to construct; absorb remains the fallback for a
    /// merged token or a partial host with no containing window.
    /// </summary>
    [Fact]
    public void MultiWordTerm_HostedOnOneWord_AbsorbsTheDuplicatedTail()
    {
        VocabularyGate.Result r = Apply(
            "logged. Claude code generated most of the boiler plate for the installer",
            Det("Claude Code"), duration: 4.0);

        Assert.Equal("logged. Claude Code generated most of the boiler plate for the installer", r.Text);
        Assert.DoesNotContain("Code code", r.Text, StringComparison.OrdinalIgnoreCase);

        // The span identity is the WIDENED text, not just the host word: a revert has to restore
        // everything the replacement actually ate, and a learned demotion has to key on it.
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("Claude code", p.OriginalWord);
        Assert.Equal("Claude Code", p.Term);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal(1, r.Applied);
    }

    /// <summary>The anchors must describe the WIDENED span, or the review surface highlights the
    /// wrong characters and a revert splices over the wrong range.</summary>
    [Fact]
    public void AbsorbedSpan_AnchorsCoverTheWholeReplacedRange()
    {
        VocabularyGate.Result r = Apply("we use Claude code daily", Det("Claude Code"));

        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("we use ".Length, p.OriginalStart);
        Assert.Equal("Claude code".Length, p.OriginalLength);
        Assert.Equal("we use ".Length, p.PublishedStart);
        Assert.Equal("Claude Code".Length, p.PublishedLength);
        Assert.Equal("we use Claude Code daily", r.Text);
        // The exact slice the anchors claim really is the replacement.
        Assert.Equal("Claude Code", r.Text.Substring(p.PublishedStart, p.PublishedLength));
    }

    /// <summary>The spotter fires on the AUDIO, so it also fires when the engine already wrote the
    /// term perfectly — the common case for a term the user added last week. Absorbing makes the
    /// span identical to the term, and that must become a no-op: no text change, and no pill chip or
    /// review row for a correction that never happened.</summary>
    [Fact]
    public void MultiWordTerm_AlreadyWrittenCorrectly_IsANoOp()
    {
        VocabularyGate.Result r = Apply("Claude Code generated the installer", Det("Claude Code"));

        Assert.Equal("Claude Code generated the installer", r.Text);
        Assert.Empty(r.Proposals);
        Assert.Equal(0, r.Applied);
    }

    /// <summary>Case-only difference is still a real correction — it changes the text — so it must
    /// NOT be swallowed by the no-op check, which normalizes case.</summary>
    [Fact]
    public void MultiWordTerm_WrittenInTheWrongCase_StillCorrects()
    {
        VocabularyGate.Result r = Apply("claude code generated the installer", Det("Claude Code"));
        Assert.Equal("Claude Code generated the installer", r.Text);
    }

    /// <summary>Absorb at most (term words − span words): a REAL following "code" that the term does
    /// not account for is never eaten.</summary>
    [Fact]
    public void Absorb_NeverEatsMoreWordsThanTheTermHas()
    {
        VocabularyGate.Result r = Apply("Claude code code review", Det("Claude Code"), duration: 2.0);
        Assert.Equal("Claude Code code review", r.Text);
    }

    /// <summary>A term whose tail does NOT match what follows is not a dedup case at all, so the
    /// widening does nothing — and with nothing to widen onto, the partial-term guard refuses the
    /// placement outright rather than inserting the term's missing words. See
    /// <see cref="Window_WhereOnlyTheHeadAligns_DoesNotFallBackToTheSingleWord"/>.</summary>
    [Fact]
    public void Absorb_DoesNothingWhenTheFollowingWordIsDifferent()
    {
        VocabularyGate.Result r = Apply("Claude wrote the installer", Det("Claude Code"));
        Assert.Equal("Claude wrote the installer", r.Text);
    }

    /// <summary>Trailing punctuation on the ABSORBED word survives outside the replaced span.</summary>
    [Fact]
    public void Absorb_KeepsTheAbsorbedWordsTrailingPunctuation()
    {
        VocabularyGate.Result r = Apply("we ship Claude code.", Det("Claude Code"));
        Assert.Equal("we ship Claude Code.", r.Text);
    }

    /// <summary>
    /// The one genuinely ambiguous shape: the HOST token carries its own trailing punctuation, so
    /// widening would eat the comma and not widening would publish "Claude Code, code". Both change
    /// text on a guess, so the gate does neither — the transcript is left byte-for-byte as the engine
    /// wrote it and the near-miss is still reviewable.
    /// </summary>
    [Fact]
    public void Absorb_AcrossThePunctuationTheEngineWrote_BlocksInsteadOfGuessing()
    {
        var sink = new Sink();
        VocabularyGate.Result r = Apply("ask Claude, code review is done", Det("Claude Code"),
                                        duration: 3.0, sink: sink);

        Assert.Equal("ask Claude, code review is done", r.Text);
        Assert.Equal(0, r.Applied);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("kept", p.Outcome);
        Assert.Equal("BLOCK", p.Decision);
        Assert.False(p.AskCandidate);                       // an unsafe span is never a live ask
        Assert.Contains(sink.Lines, l => l.StartsWith("dedup-ambiguous", StringComparison.Ordinal));
    }

    /// <summary>
    /// The symmetric (WIDER-span) case, for the record. A pick is exactly ONE whitespace token and
    /// the widening absorbs at most (term words − span words), so a span with strictly MORE words
    /// than the term is structurally unreachable on this path — only EQUAL width is. That branch of
    /// AlignmentWindow demands every span word align with the term word in its position, and it is
    /// what stops a widened span whose head is not really the term's head.
    /// </summary>
    [Fact]
    public void AlignmentWindow_EqualWidthBranch_IsTheOnlyOneThisPathCanReach()
    {
        // A widened "Claude code" aligns word-for-word with "Claude Code" and applies (above).
        // A shifted equal-width span does not — the guard Apply already relies on.
        Assert.True(VocabularyGate.AlignmentWindow(["Claude", "Code"], ["Claude", "code"]).Unique);
        Assert.False(VocabularyGate.AlignmentWindow(["Claude", "Code"], ["Claude", "Claude"]).Unique);
    }

    /// <summary>A single-word term can never trigger any of this — which is exactly why the five
    /// golden `detections_apply` cases are untouched by the fix.</summary>
    [Fact]
    public void SingleWordTerms_AreUnaffected()
    {
        VocabularyGate.Result r = Apply("talk to shriram tomorrow", Det("Sriram"), duration: 2.0);
        Assert.Equal("talk to Sriram tomorrow", r.Text);
        Assert.Equal("shriram", Assert.Single(r.Proposals).OriginalWord);
    }

    // MARK: - Defect 3 · a multi-word term must be placeable across SEPARATE words

    /// <summary>
    /// THE regression, verbatim from the owner's first live dictation. He said "I love Claude Code",
    /// the engine wrote "I love cloud code." and the term was DROPPED — the spotter heard it
    /// (score -2.106) but placement compared ONE token against the WHOLE term: skeleton("cloud") vs
    /// skeleton("Claude Code") is 0.56, past the 0.45 ceiling, so `spot-unplaced` and nothing else.
    /// The two-word WINDOW "cloud code" measures 0.20 and is obviously the right host.
    ///
    /// It only ever appeared to work because the engine happened to write "Claude code", where
    /// "Claude" alone scores 0.40 — just inside the ceiling. That was luck, not correctness.
    /// </summary>
    [Fact]
    public void MultiWordTerm_SpreadOverTwoWords_IsPlacedOnTheWindow()
    {
        var sink = new Sink();
        VocabularyGate.Result r = Apply("I love cloud code.", Det("Claude Code", 1.38, 2.18),
                                        duration: 2.5, sink: sink);

        Assert.Equal("I love Claude Code.", r.Text);          // period preserved, outside the span
        Assert.DoesNotContain("Code code", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sink.Lines, l => l.StartsWith("spot-unplaced", StringComparison.Ordinal));
        // The window already covers both words, so the dedup guard has nothing to absorb
        // (maxAbsorb < 1). The two mechanisms compose; they must never both fire on one span.
        Assert.DoesNotContain(sink.Lines, l => l.StartsWith("dedup-absorbed", StringComparison.Ordinal));

        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("cloud code", p.OriginalWord);
        Assert.Equal("Claude Code", p.Term);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal(1, r.Applied);
    }

    /// <summary>The window IS the proposal's identity: the anchors, the published span and therefore
    /// the revert range must all describe both words, or a revert splices over the wrong text.</summary>
    [Fact]
    public void WindowSpan_AnchorsCoverTheWholeWindow()
    {
        VocabularyGate.Result r = Apply("I love cloud code.", Det("Claude Code", 1.38, 2.18), duration: 2.5);

        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("I love ".Length, p.OriginalStart);
        Assert.Equal("cloud code".Length, p.OriginalLength);     // the '.' stays outside
        Assert.Equal("I love ".Length, p.PublishedStart);
        Assert.Equal("Claude Code".Length, p.PublishedLength);
        Assert.Equal("Claude Code", r.Text.Substring(p.PublishedStart, p.PublishedLength));
        Assert.Equal("cloud code", "I love cloud code.".Substring(p.OriginalStart, p.OriginalLength));
    }

    /// <summary>Window width is driven by the term's own word count, not hardcoded at two.</summary>
    [Fact]
    public void ThreeWordTerm_IsPlacedOnAThreeWordWindow()
    {
        VocabularyGate.Result r = Apply("use the model context protocall now",
                                        Det("Model Context Protocol", 0.5, 1.5), duration: 2.0);

        Assert.Equal("use the Model Context Protocol now", r.Text);
        Assert.Equal("model context protocall", Assert.Single(r.Proposals).OriginalWord);
    }

    /// <summary>
    /// Whole-window plausibility ALONE would be a text-corrupting brake: "claude wrote" measures 0.27
    /// against "Claude Code" and "load code" 0.40 — both inside the ceiling — so a window whose words
    /// do not each align would silently overwrite a word the engine got right. Per-word alignment is
    /// what refuses them, and the refusal is still reported.
    /// </summary>
    [Fact]
    public void Window_WhoseWordsDoNotEachAlign_IsRefusedAndReported()
    {
        var sink = new Sink();
        VocabularyGate.Result r = Apply("the load code runs", Det("Claude Code"), duration: 2.0, sink: sink);

        Assert.Equal("the load code runs", r.Text);        // "load" vs "Claude" is 0.67 — not a host
        Assert.Empty(r.Proposals);
        Assert.Contains(sink.Lines, l => l.StartsWith("spot-unplaced", StringComparison.Ordinal));
    }

    /// <summary>The head word aligns but the tail does not, so the window is refused — and the term is
    /// NOT then dropped onto the single word, because that word is the term's own first word,
    /// correctly transcribed, and publishing over it INSERTS "Code" where the engine wrote "wrote".
    /// Nothing downstream would catch it: Decide waves every multi-word term through as
    /// self-gating.</summary>
    [Fact]
    public void Window_WhereOnlyTheHeadAligns_DoesNotFallBackToTheSingleWord()
    {
        VocabularyGate.Result r = Apply("Claude wrote the installer", Det("Claude Code"));

        Assert.Equal("Claude wrote the installer", r.Text);
        Assert.Empty(r.Proposals);
    }

    /// <summary>The reverse shape, and the reason width 1 is always tried: the engine MERGED the term
    /// into one token. "ramanathan" is 0.09 from "Ramaa Nathan", so the single word is still the host
    /// — window placement must not have taken that away.</summary>
    [Fact]
    public void MergedSingleToken_StillHostsTheMultiWordTerm()
    {
        VocabularyGate.Result r = Apply("call ramanathan tomorrow", Det("Ramaa Nathan"), duration: 2.0);

        Assert.Equal("call Ramaa Nathan tomorrow", r.Text);
        Assert.Equal("ramanathan", Assert.Single(r.Proposals).OriginalWord);
    }

    /// <summary>An ALIAS's word count drives the width too, which is what makes the split-word class
    /// ("pera keet" → "Parakeet") placeable on this path at all: neither half is plausible alone (0.50
    /// each), only the pair. The term itself is ONE word here — the width comes from nothing but the
    /// alias.</summary>
    [Fact]
    public void AliasWordCount_DrivesTheWindowWidth()
    {
        VocabularyGate.Result r = Apply("the pera keet model is fast",
                                        Det("Parakeet", 0.4, 0.6, "pera keet"), duration: 2.0);

        Assert.Equal("the Parakeet model is fast", r.Text);
        Assert.Equal("pera keet", Assert.Single(r.Proposals).OriginalWord);
    }

    /// <summary>One detection per span, still: a window claims EVERY word it covers, so a second
    /// detection can never splice over a word the first already took.</summary>
    [Fact]
    public void TwoDetectionsCompetingForAdjacentWords_CannotBothTakeThem()
    {
        var sink = new Sink();
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "we use cloud code today",
            [
                new VocabularyGate.Detection("Claude Code", [], -0.3f, 0.5, 1.0),
                // Wants "code" on its own; the window above already owns it.
                new VocabularyGate.Detection("Kode", [], -0.9f, 1.0, 1.4),
            ],
            2.0, Common, diagnostics: sink);

        Assert.Equal("we use Claude Code today", r.Text);
        Assert.Equal(1, r.Applied);
        Assert.Equal("cloud code", Assert.Single(r.Proposals).OriginalWord);
        Assert.Contains(sink.Lines, l => l.StartsWith("spot-unplaced Kode", StringComparison.Ordinal));
    }

    // MARK: - Defect 2 · a detection with no plausible host must not vanish silently

    /// <summary>
    /// Verbatim from the end-to-end run: the spotter found "Parakeet" with the BEST score of all five
    /// terms (-0.313) while the engine had written "Herrakit". The gap is 0.50 against a 0.45
    /// ceiling, so no proposal was created — and nothing was logged, so "I added the term and nothing
    /// happened" was unanswerable.
    ///
    /// SUPERSEDED IN PART BY E8, and kept exactly as it is for that reason. This detection carries no
    /// Acoustic flag, so it earns the fixed 0.45 ceiling and is still unplaced — which is the contract
    /// for every source that is not a measured acoustic score. The real spotter now flags its
    /// detections, and THIS row is the one E8 was built for: at -0.313 it earns 0.65 and places. See
    /// <c>EarnedCeilingTests</c>. What is pinned here is the diagnostic, and that an unflagged
    /// detection gets no headroom.
    /// </summary>
    [Fact]
    public void UnplacedDetection_IsReportedWithItsScoreAndNearestMiss()
    {
        var sink = new Sink();
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "Herrakit remains the fallback engine",
            [new VocabularyGate.Detection("Parakeet", [], -0.313f, 0.5, 1.0)],
            2.0, Common, diagnostics: sink);

        Assert.Equal("Herrakit remains the fallback engine", r.Text);   // text untouched, as before
        Assert.Empty(r.Proposals);

        string line = Assert.Single(sink.Lines, l => l.StartsWith("spot-unplaced", StringComparison.Ordinal));
        Assert.Contains("Parakeet", line, StringComparison.Ordinal);
        Assert.Contains("score=-0.313", line, StringComparison.Ordinal);
        Assert.Contains("nearest=Herrakit", line, StringComparison.Ordinal);
        // 5 edits over 8 letters = 0.625, well past the 0.45 ceiling. The number is the point: it is
        // what tells the reader this was a plausibility block and not a spotter miss.
        Assert.Contains("gap=0.62", line, StringComparison.Ordinal);
        Assert.Contains("ceiling=0.45", line, StringComparison.Ordinal);
    }

    /// <summary>A detection that DOES place stays silent on that channel — the log must not become
    /// noise on every ordinary dictation.</summary>
    [Fact]
    public void PlacedDetection_EmitsNoUnplacedDiagnostic()
    {
        var sink = new Sink();
        _ = Apply("talk to shriram tomorrow", Det("Sriram"), duration: 2.0, sink: sink);
        Assert.DoesNotContain(sink.Lines, l => l.StartsWith("spot-unplaced", StringComparison.Ordinal));
    }

    /// <summary>The plausibility threshold is unchanged for an UNFLAGGED detection: this was a
    /// diagnostic, not a widening. E8 widens it only for a detection carrying a real acoustic score;
    /// everything else — the corrector, Mac's own path, any fixture — still stops at 0.45.</summary>
    [Fact]
    public void PlausibilityCeiling_IsUnchanged()
    {
        Assert.Equal(0.45, VocabularyGate.PlausibilityCeiling);
        VocabularyGate.Result r = Apply("Herrakit remains the fallback", Det("Parakeet"), duration: 2.0);
        Assert.Equal("Herrakit remains the fallback", r.Text);
    }

    /// <summary>An overlap dropped because an earlier pick's widening swallowed the word is logged
    /// too — a silently vanished correction is the same untraceable shape as an unplaced one.
    ///
    /// The host is a MERGED token, so it is not only-part-of-term (closer to the whole term than to
    /// either word). Window placement keeps the one-word pick; absorb then eats the following
    /// "code"; the second detection's pick is the one that vanishes. A split host ("Claude code")
    /// is now claimed as the pair up front, and this path cannot fire on it.
    /// </summary>
    [Fact]
    public void OverlapDrop_IsLogged()
    {
        var sink = new Sink();
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "we use claudecode code today",
            [
                new VocabularyGate.Detection("Claude Code", [], -0.3f, 0.5, 1.0),
                // Claims "code" on its own; the first pick's widening eats it first.
                new VocabularyGate.Detection("Kode", [], -0.9f, 1.0, 1.4),
            ],
            2.0, Common, diagnostics: sink);

        Assert.Equal("we use Claude Code today", r.Text);
        Assert.Contains(sink.Lines, l => l.StartsWith("overlap-dropped", StringComparison.Ordinal));
    }

    // MARK: - Strongly-heard tail must not starve the local pair that contains it

    /// <summary>
    /// Live miss: E8 makes the tail eligible ("code" vs "claudecode" is 0.60, inside the earned 0.65),
    /// position then prefers that tail over the two-word pair, and the partial-term guard correctly
    /// refuses to promote a bare "code". Existing tests never hit this — <see cref="Det"/> leaves
    /// <c>Acoustic</c> false, so the ceiling stays 0.45 and the tail is not a candidate. n=10, pair
    /// center 0.40, tail center 0.45, detection mid/duration = 0.45 → tail wins on position.
    /// </summary>
    [Fact]
    public void StronglyHeardMultiWord_DoesNotLoseTheLocalPairToItsOwnTail()
    {
        var det = new VocabularyGate.Detection(
            "Claude Code", [], -0.36f, 4.2, 4.8, Acoustic: true);
        const string text = "yesterday we tried cloud code and it worked well enough";
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            text, [det], 10.0, Common);

        Assert.Equal("yesterday we tried Claude Code and it worked well enough", r.Text);
        Assert.Equal(1, r.Applied);
        Assert.Equal("cloud code", Assert.Single(r.Proposals).OriginalWord);
    }

    /// <summary>
    /// Same Acoustic/−0.36 as the live miss, so the tail is E8-eligible. There is no containing pair
    /// ("load" fails per-word 0.45). The later partial-term guard must still refuse the insert.
    /// </summary>
    [Fact]
    public void StronglyHeardTail_WithNoContainingPair_DoesNotInsertTheMissingHead()
    {
        var sink = new Sink();
        var det = new VocabularyGate.Detection(
            "Claude Code", [], -0.36f, 2.2, 2.8, Acoustic: true);
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "the load code runs", [det], 4.0, Common, diagnostics: sink);

        Assert.Equal("the load code runs", r.Text);
        Assert.Empty(r.Proposals);
        Assert.Equal(0, r.Applied);
        Assert.Contains(sink.Lines, l => l.StartsWith("partial-term-skipped", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, l => l.StartsWith("spot-unplaced", StringComparison.Ordinal));
    }

    /// <summary>
    /// Existing <see cref="Window_WhereOnlyTheHeadAligns_DoesNotFallBackToTheSingleWord"/>, now
    /// under E8 so a 0.60 cousin would be eligible if one existed. "wrote" still fails per-word
    /// 0.45, so there is no containing pair and the head must not become "Claude Code wrote".
    /// </summary>
    [Fact]
    public void StronglyHeardHead_WithNoContainingPair_DoesNotInsertTheMissingTail()
    {
        var sink = new Sink();
        var det = new VocabularyGate.Detection(
            "Claude Code", [], -0.36f, 0.2, 0.8, Acoustic: true);
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "Claude wrote the installer", [det], 4.0, Common, diagnostics: sink);

        Assert.Equal("Claude wrote the installer", r.Text);
        Assert.Empty(r.Proposals);
        Assert.Equal(0, r.Applied);
        Assert.Contains(sink.Lines, l => l.StartsWith("partial-term-skipped", StringComparison.Ordinal));
    }

    /// <summary>
    /// WindowRange refuses the comma, so there is no containing width-2. The positional winner stays
    /// the punctuated host (times put the mid on "Claude,", even though E8 also admits "code"). The
    /// later path must still emit the reviewable kept row — making the partial host ineligible in
    /// the search loop would turn this into a silent spot-unplaced.
    /// </summary>
    [Fact]
    public void StronglyHeardPunctuatedHost_StillEmitsTheReviewableKeptRow()
    {
        var sink = new Sink();
        var det = new VocabularyGate.Detection(
            "Claude Code", [], -0.36f, 1.2, 1.8, Acoustic: true);
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "ask Claude, code review is done", [det], 6.0, Common, diagnostics: sink);

        Assert.Equal("ask Claude, code review is done", r.Text);
        Assert.Equal(0, r.Applied);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("kept", p.Outcome);
        Assert.Contains(sink.Lines, l => l.StartsWith("dedup-ambiguous", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, l => l.StartsWith("spot-unplaced", StringComparison.Ordinal));
    }
}
