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
        private readonly VocabularyCorrector _inner = new();

        public IReadOnlyList<VocabularyGate.Detection> Spot(
            string transcript, IReadOnlyList<VocabularyTerm> terms, double totalAudioDuration,
            CancellationToken ct = default)
        {
            Calls++;
            return _inner.Spot(transcript, terms, totalAudioDuration, ct);
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
        // Measured: stacking the corrector on the spotter traded about one recovery per one corrupted
        // word in English. Precision wins that, so the textual path stays off while the model is there.
        Assert.Equal(0, corrector.Calls);
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
