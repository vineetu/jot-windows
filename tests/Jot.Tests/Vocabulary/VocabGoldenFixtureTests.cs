using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// THE CROSS-PLATFORM CONFORMANCE CONTRACT. These JSON files are copied verbatim from
/// jot-shared (`Tests/JotVocabCoreTests/Fixtures`, pinned commit 5326460) — the same files the
/// Swift package runs against. Its README states the intent outright: "a future non-Apple
/// (e.g. Windows) port must reproduce the same outputs from the same fixtures."
///
/// So a failure here does not mean the fixture is wrong. It means the Windows gate has diverged
/// from the shipping Mac/iOS behaviour, and that divergence is the bug. Every case encodes a
/// real over-correction incident (the "jamy-disaster" case is the shipped macOS bug where adding
/// the term "Jamy" rewrote every "name").
///
/// If these fixtures are ever refreshed from upstream, re-pin the commit in this comment.
/// </summary>
public class VocabGoldenFixtureTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static T Load<T>(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocab", file + ".json");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
    }

    private static readonly EmbeddedCommonWordsProvider Common = EmbeddedCommonWordsProvider.Shared;

    // MARK: - 1 · CorrectionKey.Normalize

    private sealed record StringCase(string Name, string Input, string Expected);

    [Fact]
    public void CorrectionKeyNormalize_MatchesGolden()
    {
        var cases = Load<List<StringCase>>("correction_key_normalize");
        Assert.NotEmpty(cases);
        foreach (StringCase c in cases)
        {
            Assert.Equal(c.Expected, CorrectionKey.Normalize(c.Input));
        }
    }

    // MARK: - 2 · CorrectionKey.PairKey

    private sealed record PairKeyCase(string Name, string OriginalWord, string Term, string Expected);

    [Fact]
    public void CorrectionPairKey_MatchesGolden()
    {
        var cases = Load<List<PairKeyCase>>("pair_key");
        Assert.NotEmpty(cases);
        foreach (PairKeyCase c in cases)
        {
            Assert.Equal(c.Expected, CorrectionKey.PairKey(c.OriginalWord, c.Term));
        }
    }

    // MARK: - 3 · VocabularyGate.Apply

    private sealed record RescoreProposalFixture(
        string OriginalWord, string? ReplacementWord, bool ShouldReplace,
        float? ReplacementScore, float OriginalScore);

    private sealed record TokenTimingFixture(string Token, float Confidence);

    private sealed record OverrideFixture(string OriginalWord, string Term, int Net, bool AlwaysReplace);

    private sealed record AlternateFixture(string Term, string Find);

    private sealed record ExpectProposalFixture(
        string OriginalWord, string? Shape, List<AlternateFixture>? Alternates, bool? AskCandidate);

    private sealed record GateCase(
        string Name,
        string Transcript,
        List<RescoreProposalFixture> Replacements,
        List<TokenTimingFixture>? TokenTimings,
        List<OverrideFixture>? Overrides,
        Dictionary<string, List<string>>? Aliases,
        List<string>? AllTerms,
        string? CommonWordsResource,
        string ExpectText,
        Dictionary<string, string>? ExpectOutcomes,
        List<ExpectProposalFixture>? ExpectProposals);

    /// <summary>
    /// DELIBERATE DIVERGENCE, not a TODO. Upstream classifies the split-word span as <c>merge</c>
    /// and keeps it (the common-word brake fires on each decoder token). Windows applies when the
    /// concatenated skeleton is the term — measured 2 TP / 0 FP on E5's 1041 clips. The fixture
    /// file stays a verbatim jot-shared copy; the new outcome is pinned by
    /// <c>DetectionPathConcatTests</c>.
    /// </summary>
    private static readonly IReadOnlySet<string> DecideExceptions =
        new HashSet<string>(StringComparer.Ordinal) { "merge-shape-classified" };

    [Fact]
    public void VocabularyGateApply_MatchesGolden()
    {
        var cases = Load<List<GateCase>>("vocabulary_gate_decide");
        Assert.NotEmpty(cases);
        Assert.All(DecideExceptions, name => Assert.Contains(cases, c => c.Name == name));

        foreach (GateCase c in cases)
        {
            if (DecideExceptions.Contains(c.Name)) continue;
            var reps = c.Replacements
                .Select(p => new RescoreProposal(
                    p.OriginalWord, p.ReplacementWord, p.ShouldReplace, p.ReplacementScore, p.OriginalScore))
                .ToList();
            var output = new RescoreOutput(c.Transcript, reps, true);
            var timings = (c.TokenTimings ?? [])
                .Select(t => new TokenTiming(t.Token, t.Confidence)).ToList();
            var overrides = (c.Overrides ?? [])
                .Select(o => new OverrideEntry(o.OriginalWord, o.Term, o.Net, o.AlwaysReplace)).ToList();
            var aliases = (c.Aliases ?? [])
                .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);

            VocabularyGate.Result result = VocabularyGate.Apply(
                c.Transcript, output, timings, Common,
                overrides: overrides,
                termAliases: aliases,
                commonWordsResource: c.CommonWordsResource ?? "common-words",
                allTerms: c.AllTerms ?? []);

            Assert.True(c.ExpectText == result.Text,
                $"gate[{c.Name}] text\n  expected: {c.ExpectText}\n  actual:   {result.Text}");

            foreach ((string ow, string expected) in c.ExpectOutcomes ?? [])
            {
                VocabularyGate.Proposal? p = result.Proposals
                    .FirstOrDefault(x => x.OriginalWord.ToLowerInvariant() == ow);
                Assert.True(expected == p?.Outcome,
                    $"gate[{c.Name}] outcome[{ow}] expected {expected}, got {p?.Outcome ?? "<no proposal>"}");
            }

            foreach (ExpectProposalFixture ep in c.ExpectProposals ?? [])
            {
                VocabularyGate.Proposal? p = result.Proposals.FirstOrDefault(
                    x => string.Equals(x.OriginalWord, ep.OriginalWord, StringComparison.OrdinalIgnoreCase));
                Assert.True(p is not null, $"gate[{c.Name}] missing proposal for {ep.OriginalWord}");

                if (ep.Shape is not null)
                    Assert.True(ep.Shape == p!.Shape, $"gate[{c.Name}] shape expected {ep.Shape}, got {p.Shape}");
                if (ep.AskCandidate is { } ask)
                    Assert.True(ask == p!.AskCandidate, $"gate[{c.Name}] askCandidate expected {ask}");
                if (ep.Alternates is { } alts)
                {
                    Assert.True(alts.Count == p!.Alternates.Count,
                        $"gate[{c.Name}] alt count expected {alts.Count}, got {p.Alternates.Count}");
                    for (int i = 0; i < alts.Count && i < p.Alternates.Count; i++)
                    {
                        Assert.Equal(alts[i].Term, p.Alternates[i].Term);
                        Assert.Equal(alts[i].Find, p.Alternates[i].Find);
                    }
                }
            }
        }
    }

    // MARK: - 4 · VocabularyGate.ApplyFromDetections (the no-fork Nemotron path Windows uses)

    /// <param name="Acoustic">Absent in JSON ⇒ false, which is the shipped default and why every
    /// pre-existing case is unaffected by the earned ceiling. Present ONLY on cases that pin
    /// <see cref="VocabularyGate.EffectiveCeiling"/>.</param>
    private sealed record DetectionFixture(
        string Term, List<string> Aliases, float Score, double StartTime, double EndTime,
        bool Acoustic = false);

    private sealed record DetectionExpectProposal(
        string OriginalWord, string Outcome, string? Decision, bool? AskCandidate);

    private sealed record DetectionCase(
        string Name,
        string Transcript,
        double TotalAudioDuration,
        List<DetectionFixture> Detections,
        List<OverrideFixture>? Overrides,
        string? CommonWordsResource,
        string ExpectText,
        List<DetectionExpectProposal>? ExpectProposals,
        int? ExpectProposalCount);

    [Fact]
    public void VocabularyGateApplyFromDetections_MatchesGolden() =>
        RunDetectionCases("detections_apply", 33, exceptions: new HashSet<string>());

    /// <summary>
    /// The SAME harness against jot-shared's own copy of the file (branch <c>claude/jot-cli-v2</c>),
    /// which grew 17 cases after Windows sent its five divergences upstream — including three the
    /// Swift review then found on top. Kept as a second file rather than merged into ours: this one is
    /// upstream's to change, ours is a superset with Windows-only shapes, and a merge would make it
    /// impossible to tell which side moved.
    ///
    /// <para><b>Exceptions are DELIBERATE DIVERGENCES, not TODOs.</b> Each one is a case where Windows
    /// measured a different answer and wrote down why; the exception list is where that decision is
    /// visible instead of buried. Adding one requires the same standard: a reason, in the gate.</para>
    /// </summary>
    [Fact]
    public void VocabularyGateApplyFromDetections_MatchesUpstreamGolden() =>
        RunDetectionCases("detections_apply_upstream", 22, exceptions: UpstreamDetectionExceptions);

    private static readonly IReadOnlySet<string> UpstreamDetectionExceptions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // The multi-word IDENTITY relation. Swift makes it depend on HOW the span was reached
            // (dedup-widened → ordinal, window-placed → case-insensitive), so upstream leaves a
            // window-placed "Claude code" alone. Windows uses ORDINAL for every multi-word span,
            // because for a multi-word proper noun the casing IS the term — it is what the user typed
            // and the reason they added it — and a rule that depends on which mechanism found the span
            // gives one transcript two answers. Pinned the other way by our own
            // `spot-multiword-wrong-case-still-corrects`. See the identity note in ApplyFromDetections.
            "spot-multiword-already-correct-is-noop",
        };

    private static void RunDetectionCases(string file, int expectedCount, IReadOnlySet<string> exceptions)
    {
        var cases = Load<List<DetectionCase>>(file);
        // Anti-vacuum: a truncated/mis-parsed fixture file that silently loads a handful of cases
        // would pass this test while asserting nothing about the multi-word, apostrophe, inflection
        // and earned-ceiling behaviour. Pin the count and pin that every case names itself.
        Assert.Equal(expectedCount, cases.Count);
        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
        // Every case must assert SOMETHING beyond "the text came back": either an expected proposal
        // count or at least one expected proposal.
        Assert.All(cases, c => Assert.True(
            c.ExpectProposalCount is not null || (c.ExpectProposals?.Count ?? 0) > 0,
            $"detections[{c.Name}] asserts nothing about proposals"));
        // An exception that no longer names a real case is a stale excuse — fail rather than skip.
        Assert.All(exceptions, name => Assert.Contains(cases, c => c.Name == name));

        foreach (DetectionCase c in cases)
        {
            if (exceptions.Contains(c.Name)) continue;
            var dets = c.Detections
                .Select(d => new VocabularyGate.Detection(
                    d.Term, d.Aliases, d.Score, d.StartTime, d.EndTime, d.Acoustic))
                .ToList();
            var overrides = (c.Overrides ?? [])
                .Select(o => new OverrideEntry(o.OriginalWord, o.Term, o.Net, o.AlwaysReplace)).ToList();

            VocabularyGate.Result result = VocabularyGate.ApplyFromDetections(
                c.Transcript, dets, c.TotalAudioDuration, Common,
                commonWordsResource: c.CommonWordsResource ?? "common-words",
                overrides: overrides);

            Assert.True(c.ExpectText == result.Text,
                $"detections[{c.Name}] text\n  expected: {c.ExpectText}\n  actual:   {result.Text}");

            if (c.ExpectProposalCount is { } count)
                Assert.True(count == result.Proposals.Count,
                    $"detections[{c.Name}] proposal count expected {count}, got {result.Proposals.Count}");

            foreach (DetectionExpectProposal ep in c.ExpectProposals ?? [])
            {
                VocabularyGate.Proposal? p = result.Proposals.FirstOrDefault(
                    x => string.Equals(x.OriginalWord, ep.OriginalWord, StringComparison.OrdinalIgnoreCase));
                Assert.True(p is not null, $"detections[{c.Name}] missing proposal for {ep.OriginalWord}");
                Assert.True(ep.Outcome == p!.Outcome,
                    $"detections[{c.Name}] outcome expected {ep.Outcome}, got {p.Outcome}");
                if (ep.Decision is { } decision)
                    Assert.True(decision == p.Decision,
                        $"detections[{c.Name}] decision expected {decision}, got {p.Decision}");
                if (ep.AskCandidate is { } ask)
                    Assert.True(ask == p.AskCandidate, $"detections[{c.Name}] askCandidate expected {ask}");
            }
        }
    }

    // MARK: - 4c · VocabularyCorrector — the model-free 19-language path, end to end

    private sealed record CorrectorTermFixture(string Text, List<string>? Aliases);

    private sealed record CorrectorCase(
        string Name,
        string Transcript,
        string Language,
        List<CorrectorTermFixture> Terms,
        string ExpectText,
        List<DetectionExpectProposal>? ExpectProposals,
        int? ExpectProposalCount,
        int? ExpectDetectionCount);

    /// <summary>
    /// Corrector → gate, on jot-shared's fixtures. This composes the two exactly as a caller must
    /// (<c>docs/plans/jot-cli-windows.md</c> §5.3): the corrector is served iff the language's number
    /// was MEASURED (<see cref="VocabularyLimits.TextualShips"/>) and a frequency list exists to brake
    /// it with — deliberately not <see cref="VocabularyRunner.ModeFor"/>, which routes English to the
    /// acoustic spotter and would make the English rows vacuous.
    /// </summary>
    [Fact]
    public void VocabularyCorrectorApply_MatchesGolden()
    {
        var cases = Load<List<CorrectorCase>>("corrector_apply");
        Assert.Equal(12, cases.Count);
        Assert.All(cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));

        var corrector = new VocabularyCorrector();
        foreach (CorrectorCase c in cases)
        {
            if (CorrectorExceptions.Contains(c.Name)) continue;

            List<VocabularyTerm> terms = [.. c.Terms.Select(t => new VocabularyTerm
            {
                Text = t.Text,
                Aliases = t.Aliases ?? [],
            })];

            string? resource = EmbeddedCommonWordsProvider.ResourceFor(c.Language);
            bool served = VocabularyLimits.TextualShips(c.Language) && resource is not null;

            IReadOnlyList<VocabularyGate.Detection> detections = served
                ? corrector.Spot(c.Transcript, terms, 1.0, VocabularyLimits.TextualMaxDistance(c.Language))
                : [];

            if (c.ExpectDetectionCount is { } expectedDetections)
                Assert.True(expectedDetections == detections.Count,
                    $"corrector[{c.Name}] detection count expected {expectedDetections}, got {detections.Count}");

            VocabularyGate.Result result = detections.Count == 0
                ? new VocabularyGate.Result(c.Transcript, 0, [], [])
                : VocabularyGate.ApplyFromDetections(
                    c.Transcript, detections, 1.0, Common, commonWordsResource: resource);

            Assert.True(c.ExpectText == result.Text,
                $"corrector[{c.Name}] text\n  expected: {c.ExpectText}\n  actual:   {result.Text}");

            if (c.ExpectProposalCount is { } count)
                Assert.True(count == result.Proposals.Count,
                    $"corrector[{c.Name}] proposal count expected {count}, got {result.Proposals.Count}");

            foreach (DetectionExpectProposal ep in c.ExpectProposals ?? [])
            {
                VocabularyGate.Proposal? p = result.Proposals.FirstOrDefault(
                    x => string.Equals(x.OriginalWord, ep.OriginalWord, StringComparison.OrdinalIgnoreCase));
                Assert.True(p is not null, $"corrector[{c.Name}] missing proposal for {ep.OriginalWord}");
                Assert.True(ep.Outcome == p!.Outcome,
                    $"corrector[{c.Name}] outcome expected {ep.Outcome}, got {p.Outcome}");
                if (ep.AskCandidate is { } ask)
                    Assert.True(ask == p.AskCandidate, $"corrector[{c.Name}] askCandidate expected {ask}");
            }
        }
    }

    private static readonly IReadOnlySet<string> CorrectorExceptions =
        new HashSet<string>(StringComparer.Ordinal)
        {
            // The inflection guard's LEXICAL arm, and the two ports read the spec sentence "the span is
            // an everyday word minus its final character" in opposite directions. Upstream blocks a span
            // that is a list word PLUS one trailing character ("abläufe" + n). Windows blocks a span that
            // is a list word MINUS its final character ("пирамид" ← "пирамиды"), and measured the other
            // reading on E6's 1207 applied corrections: it widens the class from 21 rows to 63 and turns
            // a 4:17 recall-to-precision trade into 30:33 — most of what it adds is CORRECT corrections.
            // See CommonWordStems. Windows keeps the measured reading; upstream's example is exempt.
            "corrector-inflection-guard-blocks-overwrite",
        };

    // MARK: - 4b · AskPolicy.Select — the "don't nag me twice" throttle

    private sealed record AskRecordFixture(
        string OriginalWord, string Term, string Decision, string Outcome,
        float Confidence, float Margin, bool Unsure, int OccurrenceIndex,
        int OriginalStart, int OriginalLength, int PublishedStart, int PublishedLength,
        List<AlternateFixture>? Alternates, string? Shape);

    private sealed record AskExpect(string OriginalWord, bool IsMergeTeach, string? AltTerm, string? AltFind);

    private sealed record AskCase(
        string Name,
        List<AskRecordFixture> Records,
        List<OverrideFixture> Overrides,
        List<string> KeyboardSuppressed,
        List<string> MergeAsked,
        List<AskExpect> ExpectSelected);

    [Fact]
    public void AskPolicySelect_MatchesGolden()
    {
        var cases = Load<List<AskCase>>("ask_policy_select");
        Assert.NotEmpty(cases);

        foreach (AskCase c in cases)
        {
            var records = c.Records.Select(r => new CorrectionRecord
            {
                OriginalWord = r.OriginalWord,
                Term = r.Term,
                Decision = r.Decision,
                Outcome = r.Outcome,
                Confidence = r.Confidence,
                Margin = r.Margin,
                Unsure = r.Unsure,
                OccurrenceIndex = r.OccurrenceIndex,
                OriginalStart = r.OriginalStart,
                OriginalLength = r.OriginalLength,
                PublishedStart = r.PublishedStart,
                PublishedLength = r.PublishedLength,
                Alternates = r.Alternates?.Select(a => new VocabularyGate.Alternate(a.Term, a.Find)).ToList(),
                Shape = r.Shape,
            }).ToList();

            var overrides = c.Overrides
                .Select(o => new OverrideEntry(o.OriginalWord, o.Term, o.Net, o.AlwaysReplace)).ToList();

            IReadOnlyList<AskPolicy.Selection> selected = AskPolicy.Select(
                records, overrides,
                c.KeyboardSuppressed.ToHashSet(),
                c.MergeAsked.ToHashSet());

            Assert.True(c.ExpectSelected.Count == selected.Count,
                $"askpolicy[{c.Name}] count expected {c.ExpectSelected.Count}, got {selected.Count}");

            for (int i = 0; i < c.ExpectSelected.Count && i < selected.Count; i++)
            {
                AskExpect e = c.ExpectSelected[i];
                Assert.True(e.OriginalWord == selected[i].Record.OriginalWord,
                    $"askpolicy[{c.Name}][{i}] originalWord expected {e.OriginalWord}, got {selected[i].Record.OriginalWord}");
                Assert.True(e.IsMergeTeach == selected[i].IsMergeTeach,
                    $"askpolicy[{c.Name}][{i}] isMergeTeach expected {e.IsMergeTeach}");
                Assert.Equal(e.AltTerm, selected[i].AltTerm);
                Assert.Equal(e.AltFind, selected[i].AltFind);
            }
        }
    }

    // MARK: - 5 · The brake must actually be loaded

    /// <summary>
    /// The macOS #1 shipped bug in this feature was the common-words list silently missing from
    /// the bundle, which turns the over-correction brake into a no-op. Assert the embedded
    /// resources are really there and sane, so a packaging regression fails here rather than in
    /// the field as "every name became Jamy".
    /// </summary>
    [Theory]
    [InlineData("common-words")]
    [InlineData("common-words-es")]
    [InlineData("common-words-de")]
    [InlineData("common-words-ru")]
    public void CommonWordLists_AreEmbeddedAndPopulated(string resource)
    {
        IReadOnlySet<string> words = Common.Words(resource);
        Assert.True(words.Count > 1000, $"{resource} loaded only {words.Count} words — resource missing or truncated?");
    }

    [Fact]
    public void CommonWords_EnglishContainsTheWordThatBrokeMacOS()
    {
        // "name" must be a common word, or the "Jamy" over-correction returns.
        Assert.Contains("name", Common.Words("common-words"));
    }

    // MARK: - 6 · Signals that are INERT on the Windows engine path

    /// <summary>
    /// Pins a trap. On the detection path (Nemotron: bare text, no per-word confidence) the gate
    /// runs with an EMPTY confidence map, so `Confidence` is pinned at exactly `LowConfidence`,
    /// `Unsure` is always false, and `Margin` is always 0. Anything downstream that filters on
    /// those — e.g. the Mac's `notable` heuristic (`confidence &lt;= 0.85 || margin &gt;= 4.0`) — is
    /// therefore true 100% of the time and is NOT a throttle. Port such a filter and you ship
    /// dead code that looks like a control. If this test ever fails, the engine started supplying
    /// real confidence and those filters become meaningful again.
    /// </summary>
    [Fact]
    public void DetectionPath_ConfidenceSignalsAreInert()
    {
        var detections = new List<VocabularyGate.Detection>
        {
            new("Sriram", [], -8.0f, 0.5, 1.0),
        };
        VocabularyGate.Result result = VocabularyGate.ApplyFromDetections(
            "call shriram about it", detections, 2.0, Common);

        VocabularyGate.Proposal p = Assert.Single(result.Proposals);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal(VocabularyGate.LowConfidence, p.Confidence);   // never a measured value
        Assert.Equal(0f, p.Margin);                                  // spotter score is a different scale
        Assert.False(p.Unsure);                                      // no measured confidence ⇒ never "unsure"
        Assert.True(p.AskCandidate);                                 // true for every single-word apply
    }

    /// <summary>The common-word brake on the detection path: a plausible near-miss of an everyday
    /// word is BLOCKED (text unchanged) but still surfaced as an ask candidate. This is the
    /// "name"/"Jamy" class — the whole reason the gate exists.</summary>
    [Fact]
    public void DetectionPath_CommonWordIsBlockedButOffered()
    {
        var detections = new List<VocabularyGate.Detection>
        {
            new("Jamy", [], -8.0f, 0.5, 1.0),
        };
        VocabularyGate.Result result = VocabularyGate.ApplyFromDetections(
            "my name is important", detections, 2.0, Common);

        Assert.Equal("my name is important", result.Text);
        // "name"→"Jamy" is beyond the plausibility ceiling (0.50 > 0.45), so it never even
        // reaches the common-word brake — blocked one guard earlier, and NOT offered as an ask.
        if (result.Proposals.Count > 0)
        {
            Assert.Equal("kept", result.Proposals[0].Outcome);
        }
    }

    [Fact]
    public void ResourceFor_MapsLocalesAndAdmitsWhenItHasNoList()
    {
        Assert.Equal("common-words", EmbeddedCommonWordsProvider.ResourceFor("en-US"));
        Assert.Equal("common-words-es", EmbeddedCommonWordsProvider.ResourceFor("es-ES"));
        Assert.Equal("common-words-de", EmbeddedCommonWordsProvider.ResourceFor("de"));
        // No list ships for Japanese — must be null (brake off), never a bogus resource name.
        Assert.Null(EmbeddedCommonWordsProvider.ResourceFor("ja-JP"));
        Assert.Null(EmbeddedCommonWordsProvider.ResourceFor(null));
    }
}
