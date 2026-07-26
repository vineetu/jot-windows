using Jot.Services.Abstractions;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The one orchestration seam: when vocabulary may run at all (D5), what it does when the spotter
/// isn't there, and THE ask-deck filter (D8).
///
/// <see cref="AskDeckFilter_DropsATaughtCommonWordBlock"/> is the load-bearing one. Without the
/// filter, a taught common-word pair carries <c>Prior &gt; 0</c> forever while
/// <c>VocabularyGate.Decide</c> keeps blocking it, so <c>AskPolicy</c> would ask about it on every
/// single dictation, indefinitely — the exact nagging the owner ruled out.
/// </summary>
public class VocabularyRunnerTests
{
    private static readonly IReadOnlySet<string> NoPairs = new HashSet<string>(StringComparer.Ordinal);

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeSpotter : IVocabularySpotter
    {
        public bool IsReady { get; init; } = true;
        public int Calls;
        public IReadOnlyList<VocabularyGate.Detection> Result = [];

        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
        {
            Calls++;
            return Result;
        }
    }

    private static (VocabularyRunner Runner, FakeSettingsStore Settings, VocabularyStore Terms, FakeSpotter Spotter)
        Build(bool enabled = true, string language = "en-US", bool spotterReady = true, string? term = "Nemotron")
    {
        var settings = new FakeSettingsStore();
        settings.Current.VocabularyEnabled = enabled;
        settings.Current.Language = language;

        var terms = new VocabularyStore(null);
        if (term is not null) terms.Add(term);

        var spotter = new FakeSpotter { IsReady = spotterReady };
        var runner = new VocabularyRunner(
            settings, terms, new CorrectionStore(null), new CorrectionProvenance(null),
            spotter, EmbeddedCommonWordsProvider.Shared, NoopDiagnosticsSink.Instance);
        return (runner, settings, terms, spotter);
    }

    // MARK: - D5 · language gating

    /// <summary>Auto-detect is OFF, and that is not a simplification: both transcribers discard the
    /// model's locale token, so a per-recording resolved language does not exist to gate on.</summary>
    [Theory]
    [InlineData("en-US", true)]
    [InlineData("en-GB", true)]
    [InlineData("auto", false)]
    [InlineData("es-ES", false)]
    [InlineData("ja-JP", false)]
    public void LanguageSupported_RequiresAnExplicitEnglishLocale(string language, bool expected)
        => Assert.Equal(expected, VocabularyRunner.LanguageSupported(language));

    // MARK: - ShouldRun

    [Fact]
    public void ShouldRun_RequiresToggleTermsAndEnglish()
    {
        Assert.True(Build().Runner.ShouldRun);
        Assert.False(Build(enabled: false).Runner.ShouldRun);
        Assert.False(Build(term: null).Runner.ShouldRun);
        Assert.False(Build(language: "auto").Runner.ShouldRun);
        Assert.False(Build(language: "es-ES").Runner.ShouldRun);
    }

    /// <summary>Model readiness is deliberately NOT part of <c>ShouldRun</c> — terms must still save
    /// and the dictation must still be untouched, silently, while the model is downloading.</summary>
    [Fact]
    public void ShouldRun_IsTrueEvenWithoutASpotterModel()
        => Assert.True(Build(spotterReady: false).Runner.ShouldRun);

    // MARK: - Run

    [Fact]
    public void Run_WithNoSpotterModel_ReturnsTheTranscriptUntouchedAndDoesNotSpot()
    {
        (VocabularyRunner runner, _, _, FakeSpotter spotter) = Build(spotterReady: false);
        VocabularyRunner.Outcome outcome = runner.Run("met with nemo tron", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("met with nemo tron", outcome.Text);
        Assert.Empty(outcome.Proposals);
        Assert.Equal(0, spotter.Calls);
    }

    [Fact]
    public void Run_WhenGatedOff_NeverReachesTheSpotter()
    {
        (VocabularyRunner runner, _, _, FakeSpotter spotter) = Build(language: "auto");
        Assert.Equal("hola lista", runner.Run("hola lista", [], 16000, TimeSpan.FromSeconds(1)).Text);
        Assert.Equal(0, spotter.Calls);
    }

    [Fact]
    public void Run_WithNoDetections_ReturnsTheTranscriptUnchanged()
    {
        (VocabularyRunner runner, _, _, FakeSpotter spotter) = Build();
        Assert.Equal("hello world", runner.Run("hello world", [], 16000, TimeSpan.FromSeconds(1)).Text);
        Assert.Equal(1, spotter.Calls);
    }

    /// <summary>The end-to-end shape: a detection reaches the gate, the gate rewrites the transcript,
    /// and the proposal is recorded. Also pins that the runner feeds the store's aliases through — the
    /// gate's only plausibility lever on this engine.</summary>
    [Fact]
    public void Run_AppliesAPlausibleDetectionAndRecordsTheProposal()
    {
        (VocabularyRunner runner, _, VocabularyStore terms, FakeSpotter spotter) = Build(term: null);
        terms.Add("Nemotron", ["neumotron"]);
        spotter.Result = [new VocabularyGate.Detection("Nemotron", [], -3f, 0.5, 1.0)];

        VocabularyRunner.Outcome outcome = runner.Run("the neumotron model", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("the Nemotron model", outcome.Text);
        Assert.Single(outcome.Proposals);
        Assert.Equal("applied", outcome.Proposals[0].Outcome);
    }

    /// <summary>The common-word brake, reached through the runner rather than the gate directly:
    /// "neutron" is an everyday word, so it is blocked and surfaced, never silently swapped.</summary>
    [Fact]
    public void Run_DoesNotSwapACommonWord()
    {
        (VocabularyRunner runner, _, _, FakeSpotter spotter) = Build();
        spotter.Result = [new VocabularyGate.Detection("Nemotron", ["neutron"], -3f, 0.5, 1.0)];

        VocabularyRunner.Outcome outcome = runner.Run("the neutron model", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("the neutron model", outcome.Text);
        Assert.Equal("kept", Assert.Single(outcome.Proposals).Outcome);
        Assert.Empty(outcome.Deck);   // and a first-encounter block gets no ask either
    }

    // MARK: - D8 · the ask-deck filter

    private static CorrectionRecord Record(string original, string term, string outcome) => new()
    {
        OriginalWord = original,
        Term = term,
        Decision = outcome == "applied" ? "APPLY" : "BLOCK",
        Outcome = outcome,
        Confidence = VocabularyGate.LowConfidence,
        Margin = 0,
        Unsure = false,
        OccurrenceIndex = 0,
        OriginalStart = 0,
        OriginalLength = original.Length,
        PublishedStart = 0,
        PublishedLength = original.Length,
    };

    /// <summary>
    /// THE regression. `lista → Lisa` is blocked forever (Decide's override branch requires
    /// `!isCommon`), but the review surface's KEPT pick and the right-click "Add to Vocabulary"
    /// gesture both push the pair to net +1 — and `AskPolicy.WorthAsking` ends
    /// `Outcome == "applied" || Prior(r) > 0`. Unfiltered, that is an ask on every later dictation
    /// that never stops. The filter is what makes D8 true rather than aspirational.
    /// </summary>
    [Fact]
    public void AskDeckFilter_DropsATaughtCommonWordBlock()
    {
        CorrectionRecord kept = Record("lista", "Lisa", "kept");
        OverrideEntry[] overrides = [new("lista", "Lisa", Net: 1, AlwaysReplace: false)];

        // The hazard, pinned: the unfiltered policy asks about this record.
        Assert.Single(AskPolicy.Select([kept], overrides, NoPairs, NoPairs));

        // The fix: caller-side composition. AskPolicy itself is unchanged.
        Assert.Empty(VocabularyRunner.SelectAsks([kept], overrides, NoPairs, NoPairs));
    }

    [Fact]
    public void AskDeckFilter_KeepsAppliedCorrections()
    {
        CorrectionRecord applied = Record("nemo tron", "Nemotron", "applied");
        IReadOnlyList<AskPolicy.Selection> deck =
            VocabularyRunner.SelectAsks([applied], [], NoPairs, NoPairs);

        Assert.Equal("Nemotron", Assert.Single(deck).Record.Term);
    }

    [Fact]
    public void AskDeckFilter_StillHonoursAnAnsweredPair()
    {
        CorrectionRecord applied = Record("nemo tron", "Nemotron", "applied");
        var answered = new HashSet<string>(StringComparer.Ordinal)
        {
            CorrectionKey.PairKey("nemo tron", "Nemotron"),
        };

        Assert.Empty(VocabularyRunner.SelectAsks([applied], [], answered, NoPairs));
    }
}
