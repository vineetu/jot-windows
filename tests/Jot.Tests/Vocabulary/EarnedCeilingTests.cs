using System.Collections.Generic;
using System.Linq;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// E8 — the confidence-conditional plausibility ceiling on
/// <see cref="VocabularyGate.ApplyFromDetections"/>, and the two things that make it safe: it moves
/// ONLY for a detection carrying a real acoustic score, and it moves by an amount derived from
/// measured data rather than picked.
///
/// A SIXTH WINDOWS DIVERGENCE — Swift's `applyFromDetections` compares every gap against one constant.
/// No golden fixture covers this (their detections carry no acoustic flag, which is the shipped
/// default and is asserted below), so a fixture refresh will not catch its removal.
/// </summary>
public class EarnedCeilingTests
{
    private static VocabularyGate.Result Apply(string text, VocabularyGate.Detection det) =>
        VocabularyGate.ApplyFromDetections(text, [det], 2.0, EmbeddedCommonWordsProvider.Shared);

    private static VocabularyGate.Detection Heard(string term, float score) =>
        new(term, [], score, 0.0, 0.6, Acoustic: true);

    // MARK: - The mapping

    [Theory]
    // Anything that is not a measured acoustic score gets the shipped constant, whatever the number in
    // the field says — the corrector puts a negated string distance there.
    [InlineData(false, 0.0f, 0.45)]
    [InlineData(false, -0.1f, 0.45)]
    // At or below the floor score the ramp grants nothing: -1.793 is the strongest score at which the
    // 1041-clip corpus contains a detection beyond the ceiling whose term was never spoken.
    [InlineData(true, -3.0f, 0.45)]
    [InlineData(true, -1.8f, 0.45)]
    // Linear between the two anchors.
    [InlineData(true, -1.4f, 0.55)]
    // At or above the full-headroom score, the measured cap.
    [InlineData(true, -1.0f, 0.65)]
    [InlineData(true, -0.313f, 0.65)]
    public void TheMapping(bool acoustic, float score, double expected) =>
        Assert.Equal(expected, VocabularyGate.EffectiveCeiling(
            new VocabularyGate.Detection("Parakeet", [], score, 0, 1, acoustic)), 3);

    [Fact]
    public void TheRampIsMonotonicAndBounded()
    {
        double previous = VocabularyGate.PlausibilityCeiling;
        for (float s = -4.0f; s <= 0.0f; s += 0.05f)
        {
            double c = VocabularyGate.EffectiveCeiling(Heard("Parakeet", s));
            Assert.InRange(c, VocabularyGate.PlausibilityCeiling, VocabularyGate.PlausibilityCeilingMax);
            Assert.True(c >= previous - 1e-9, $"ceiling fell at score {s}");
            previous = c;
        }
    }

    // MARK: - End to end, on the row this exists for

    /// <summary>
    /// Verbatim from the first end-to-end run: the engine wrote "Herrakit", the spotter found
    /// "Parakeet" at -0.313 — the best score of five terms in that dictation — and the gap is 0.62,
    /// one guard too far. E5 then measured the whole band: 61 of 214 missed terms sit beyond 0.45, the
    /// spotter hears 35 of them, and the gate applied exactly zero.
    /// </summary>
    [Fact]
    public void AStronglyHeardTermReachesPastTheFixedCeiling()
    {
        Assert.Equal(0.62, VocabularyGate.Gap("Herrakit", "Parakeet", []), 2);

        VocabularyGate.Result r = Apply("Herrakit remains the fallback engine", Heard("Parakeet", -0.313f));
        Assert.Equal("Parakeet remains the fallback engine", r.Text);
        Assert.Equal(1, r.Applied);
    }

    [Fact]
    public void TheSameDetectionHeardWEAKLYDoesNot()
    {
        // The whole point of making it conditional. Below the floor score the row is refused exactly as
        // it was before, and it still leaves the spot-unplaced diagnostic behind.
        VocabularyGate.Result r = Apply("Herrakit remains the fallback engine", Heard("Parakeet", -2.4f));
        Assert.Equal("Herrakit remains the fallback engine", r.Text);
        Assert.Empty(r.Proposals);
    }

    [Fact]
    public void TheSameDetectionWithoutAnAcousticScoreDoesNot()
    {
        VocabularyGate.Result r = Apply("Herrakit remains the fallback engine",
            new VocabularyGate.Detection("Parakeet", [], -0.313f, 0.0, 0.6));
        Assert.Equal("Herrakit remains the fallback engine", r.Text);
    }

    [Fact]
    public void TheEarnedCeilingIsWhatTheDiagnosticReports()
    {
        var sink = new Sink();
        VocabularyGate.ApplyFromDetections(
            "Herrakit remains the fallback engine",
            [Heard("Parakeet", -1.6f)],   // ceiling 0.50, gap 0.62 — refused, but not at 0.45
            2.0, EmbeddedCommonWordsProvider.Shared, diagnostics: sink);

        string line = Assert.Single(sink.Lines, l => l.StartsWith("spot-unplaced", System.StringComparison.Ordinal));
        Assert.Contains("ceiling=0.50", line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommonWordBrakeStillStopsIt()
    {
        // Headroom is granted against the STRING, never against the brake. A term that lands on an
        // everyday word is still refused however sure the model was — this is the "every name becomes
        // Jamy" protection and E8 does not touch it.
        VocabularyGate.Result r = Apply("we finished the project today", Heard("Prochet", -0.05f));
        Assert.Equal("we finished the project today", r.Text);
        Assert.Equal("BLOCK", Assert.Single(r.Proposals).Decision);
    }

    // MARK: - The textual path must not move

    [Fact]
    public void TheCorrectorNeverClaimsAnAcousticScore()
    {
        // Its Score field is a negated string distance and is NOT on the spotter's scale. If this ever
        // flips, every language the corrector serves silently gains up to 0.20 of extra reach with no
        // acoustic evidence behind it — and E6's per-language distances were measured without it.
        IReadOnlyList<VocabularyGate.Detection> dets = new VocabularyCorrector().Spot(
            "talk to shriram tomorrow", [new VocabularyTerm { Text = "Sriram" }], 2.0, "en-US");
        Assert.NotEmpty(dets);
        Assert.All(dets, d => Assert.False(d.Acoustic));
        Assert.All(dets, d => Assert.Equal(
            VocabularyGate.PlausibilityCeiling, VocabularyGate.EffectiveCeiling(d)));
    }

    [Fact]
    public void ATextualDetectionAtTheSameGapIsStillRefused()
    {
        // The corrector cannot even propose this far, but the gate is what must refuse it: a future
        // textual source that emitted a strong-looking score must not inherit acoustic headroom.
        VocabularyGate.Result r = Apply("Herrakit remains the fallback engine",
            new VocabularyGate.Detection("Parakeet", [], -0.05f, 0.0, 0.6, Acoustic: false));
        Assert.Equal("Herrakit remains the fallback engine", r.Text);
    }

    private sealed class Sink : IDiagnosticsSink
    {
        public readonly List<string> Lines = [];
        public void Record(DiagnosticsCategory category, string message,
                           IReadOnlyDictionary<string, string> metadata) =>
            Lines.Add(message + " " + string.Join(" ", metadata.Select(kv => $"{kv.Key}={kv.Value}")));
    }
}
