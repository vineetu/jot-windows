using System;
using System.Collections.Generic;
using System.Threading;
using Jot.Services.Abstractions;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The three-state language gate: sound-alike in English, spelling-only wherever a frequency list
/// ships, off otherwise. The state that matters most is the LAST one — a language with no list runs
/// the gate with its over-correction brake absent, which is the shipped "lista → Lisa" incident with
/// the safety net removed.
/// </summary>
public class VocabularyModeTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeSpotter(bool ready) : IVocabularySpotter
    {
        public bool IsReady { get; } = ready;
        public int Calls;
        public IReadOnlyList<VocabularyGate.Detection> Result = [];

        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
        {
            Calls++;
            return Result;
        }
    }

    private sealed class CountingCorrector : ITextVocabularySpotter
    {
        public int Calls;
        public string? Language;                    // what the runner told us we are dictating in
        private readonly VocabularyCorrector _inner = new();

        public IReadOnlyList<VocabularyGate.Detection> Spot(
            string transcript, IReadOnlyList<VocabularyTerm> terms, double totalAudioDuration,
            string? language = null, CancellationToken ct = default)
        {
            Calls++;
            Language = language;
            return _inner.Spot(transcript, terms, totalAudioDuration, language, ct);
        }
    }

    private static (VocabularyRunner Runner, FakeSpotter Spotter, CountingCorrector Corrector) Build(
        string language, bool spotterReady, string term, bool withCorrector = true)
    {
        var settings = new FakeSettingsStore();
        settings.Current.VocabularyEnabled = true;
        settings.Current.Language = language;

        var terms = new VocabularyStore(null);
        terms.Add(term);

        var spotter = new FakeSpotter(spotterReady);
        var corrector = new CountingCorrector();
        var runner = new VocabularyRunner(
            settings, terms, new CorrectionStore(null), new CorrectionProvenance(null),
            spotter, EmbeddedCommonWordsProvider.Shared, NoopDiagnosticsSink.Instance,
            withCorrector ? corrector : null);
        return (runner, spotter, corrector);
    }

    // MARK: - The language policy

    [Theory]
    [InlineData("en-US", VocabularyRunner.VocabularyMode.Acoustic)]
    [InlineData("en-GB", VocabularyRunner.VocabularyMode.Acoustic)]
    [InlineData("es-ES", VocabularyRunner.VocabularyMode.Textual)]
    [InlineData("de-DE", VocabularyRunner.VocabularyMode.Textual)]
    [InlineData("sv-SE", VocabularyRunner.VocabularyMode.Textual)]
    [InlineData("uk-UA", VocabularyRunner.VocabularyMode.Textual)]
    [InlineData("pt-BR", VocabularyRunner.VocabularyMode.Textual)]
    // No frequency list ⇒ no brake ⇒ off. CJK is doubly out: VocabularyGate.SplitWords splits on a
    // space, so a Chinese transcript is one "word" and placement collapses.
    [InlineData("auto", VocabularyRunner.VocabularyMode.Off)]
    // HAS a list, and still off: E6 measured 1.72 false applies per 1000 words there and no setting
    // fixed it without collapsing recall (docs/plans/vocabulary-brake-per-language.md).
    [InlineData("sl-SI", VocabularyRunner.VocabularyMode.Off)]
    [InlineData("ja-JP", VocabularyRunner.VocabularyMode.Off)]
    [InlineData("zh-CN", VocabularyRunner.VocabularyMode.Off)]
    [InlineData("ko-KR", VocabularyRunner.VocabularyMode.Off)]
    [InlineData("tr-TR", VocabularyRunner.VocabularyMode.Off)]
    [InlineData("th-TH", VocabularyRunner.VocabularyMode.Off)]
    public void ModeFor_RoutesEachLanguage(string language, VocabularyRunner.VocabularyMode expected)
        => Assert.Equal(expected, VocabularyRunner.ModeFor(language));

    /// <summary>The acoustic flag keeps its old meaning exactly — the 132 MB download offer and the
    /// spotter session's lifecycle both key on it, and widening it would offer an English checkpoint
    /// to a Swedish user.</summary>
    [Fact]
    public void LanguageSupported_StillMeansTheAcousticPath()
    {
        Assert.True(VocabularyRunner.LanguageSupported("en-US"));
        Assert.False(VocabularyRunner.LanguageSupported("sv-SE"));
        Assert.False(VocabularyRunner.LanguageSupported("auto"));
    }

    // MARK: - What the runner actually does

    [Fact]
    public void ARunnerWithNoCorrectorCannotServeATextualLanguage()
    {
        (VocabularyRunner runner, _, _) = Build("es-ES", spotterReady: true, "Nemotron", withCorrector: false);
        Assert.Equal(VocabularyRunner.VocabularyMode.Off, runner.Mode);
        Assert.False(runner.ShouldRun);
    }

    [Fact]
    public void SpanishNowCorrectsBySpelling()
    {
        (VocabularyRunner runner, FakeSpotter spotter, CountingCorrector corrector) =
            Build("es-ES", spotterReady: true, "Nemotron");

        VocabularyRunner.Outcome outcome =
            runner.Run("usamos el neumotron aquí", [], 16000, TimeSpan.FromSeconds(3));

        Assert.Equal("usamos el Nemotron aquí", outcome.Text);
        Assert.Equal(1, corrector.Calls);
        // The English checkpoint must never be asked about Spanish audio.
        Assert.Equal(0, spotter.Calls);
    }

    /// <summary>
    /// The wiring E6 depends on: the corrector's acceptance distance is PER LANGUAGE, and it can only
    /// be per language if the runner actually tells it which one. Russian ships at 0.20 and Spanish at
    /// the English setting; a runner that passed null would silently give every language the
    /// unmeasured default and nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void TheRunnerTellsTheCorrectorWhichLanguageItIs()
    {
        (VocabularyRunner runner, _, CountingCorrector corrector) =
            Build("ru-RU", spotterReady: false, "Nemotron");

        runner.Run("модель нейmotron здесь", [], 16000, TimeSpan.FromSeconds(3));

        Assert.Equal("ru-RU", corrector.Language);
        Assert.Equal(0.20, VocabularyLimits.TextualMaxDistance(corrector.Language));
    }

    /// <summary>THE regression this whole gate exists for. Spanish "lista" is one edit from the term
    /// "Lisa", so the corrector proposes it — and the SPANISH frequency list is what stops it.</summary>
    [Fact]
    public void TheSpanishIncidentStaysBlocked()
    {
        (VocabularyRunner runner, _, _) = Build("es-ES", spotterReady: false, "Lisa");

        VocabularyRunner.Outcome outcome =
            runner.Run("hazme una lista de nombres", [], 16000, TimeSpan.FromSeconds(3));

        Assert.Equal("hazme una lista de nombres", outcome.Text);
        Assert.Equal("kept", Assert.Single(outcome.Proposals).Outcome);
    }

    [Fact]
    public void EnglishWithTheModelReadyUsesTheSpotterAlone()
    {
        (VocabularyRunner runner, FakeSpotter spotter, CountingCorrector corrector) =
            Build("en-US", spotterReady: true, "Nemotron");
        spotter.Result = [new VocabularyGate.Detection("Nemotron", [], -1f, 0.5, 1.0)];

        VocabularyRunner.Outcome outcome =
            runner.Run("the neumotron model", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("the Nemotron model", outcome.Text);
        Assert.Equal(1, spotter.Calls);
        // Residual stacking stays off. The corrector is only asked for terms this spotter heard
        // and the gate then lost; this detection places, so the textual path stays idle.
        Assert.Equal(0, corrector.Calls);
    }

    [Fact]
    public void EnglishHeardButUnplacedLetsTheCorrectorPlace()
    {
        // Near-concat of uncommon pieces: not exact, so width-unlock will not admit the pair,
        // and at score −2 the shards sit past the 0.45 ceiling (spot-unplaced). The corrector
        // unlocks the width. MEASURED on focused-25 (heard-unplaced, 2026-08-14): this class
        // is the +2 at 0.00 (John Drow → Johndroe, Ogar Sinska → Ogarzynska).
        (VocabularyRunner runner, FakeSpotter spotter, CountingCorrector corrector) =
            Build("en-US", spotterReady: true, "Zorblatt");
        spotter.Result = [new VocabularyGate.Detection("Zorblatt", [], -2f, 0.8, 1.2, Acoustic: true)];

        VocabularyRunner.Outcome outcome =
            runner.Run("we called Zor Blott yesterday", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("we called Zorblatt yesterday", outcome.Text);
        Assert.Equal(1, spotter.Calls);
        Assert.Equal(1, corrector.Calls);
    }

    [Fact]
    public void EnglishUnheardTermDoesNotAskTheCorrector()
    {
        // Lisa is in the list and "list" is in the transcript. Residual stacking would propose
        // it (the 0.27 arm). The spotter never heard Lisa, so the corrector is not asked.
        var settings = new FakeSettingsStore();
        settings.Current.VocabularyEnabled = true;
        settings.Current.Language = "en-US";
        var terms = new VocabularyStore(null);
        terms.Add("Nemotron");
        terms.Add("Lisa");
        var spotter = new FakeSpotter(true);
        spotter.Result = [new VocabularyGate.Detection("Nemotron", [], -1f, 0.3, 0.7, Acoustic: true)];
        var corrector = new CountingCorrector();
        var runner = new VocabularyRunner(
            settings, terms, new CorrectionStore(null), new CorrectionProvenance(null),
            spotter, EmbeddedCommonWordsProvider.Shared, NoopDiagnosticsSink.Instance, corrector);

        VocabularyRunner.Outcome outcome =
            runner.Run("the neumotron list is ready", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("the Nemotron list is ready", outcome.Text);
        Assert.Equal(0, corrector.Calls);
    }

    [Fact]
    public void EnglishHeardCommonSpanStaysBlocked()
    {
        // The spotter heard Lisa and the host is the everyday word. Decide BLOCKs; the
        // corrector is asked (it is a heard-unplaced term) and the gate BLOCKs again.
        (VocabularyRunner runner, FakeSpotter spotter, CountingCorrector corrector) =
            Build("en-US", spotterReady: true, "Lisa");
        spotter.Result = [new VocabularyGate.Detection("Lisa", [], -1f, 0.4, 0.8, Acoustic: true)];

        VocabularyRunner.Outcome outcome =
            runner.Run("the list is ready", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("the list is ready", outcome.Text);
        Assert.Equal(1, corrector.Calls);
        Assert.Equal("kept", Assert.Single(outcome.Proposals).Outcome);
    }

    [Fact]
    public void EnglishWithNoModelFallsBackToSpellingInsteadOfDoingNothing()
    {
        (VocabularyRunner runner, FakeSpotter spotter, CountingCorrector corrector) =
            Build("en-US", spotterReady: false, "Nemotron");

        VocabularyRunner.Outcome outcome =
            runner.Run("the neumotron model", [], 16000, TimeSpan.FromSeconds(2));

        Assert.Equal("the Nemotron model", outcome.Text);
        Assert.Equal(0, spotter.Calls);
        Assert.Equal(1, corrector.Calls);
    }
}
