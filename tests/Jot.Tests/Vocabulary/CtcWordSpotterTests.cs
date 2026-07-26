using System;
using System.Collections.Generic;
using System.Linq;
using Jot.Transcription.Ctc;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The DP, tested WITHOUT the model. Every case here hand-builds a log-prob matrix, because the parts
/// most likely to be subtly wrong — blank handling, the repeated-token rule, where a match's frame range
/// starts and ends, and whether the score is really length-normalized — are properties of the recursion
/// and not of the checkpoint. Model-gated coverage lives in <c>CtcSpotterTests</c> and is skipped in CI.
///
/// Convention for the fixtures below: ids 0..4 are ordinary tokens, id 5 is blank. A frame "carries" one
/// id at <see cref="Hot"/> and everything else at <see cref="Cold"/>, which is the spiky shape real CTC
/// output has.
/// </summary>
public class CtcWordSpotterTests
{
    private const int V = 6;
    private const int Blank = 5;
    private const double Fs = 0.08;      // the measured 80 ms/frame

    private const float Hot = -0.05f;    // ≈ 95 % — a confident frame
    private const float Cold = -8.0f;    // ≈ 0.03 % — a frame that is definitely something else

    /// <summary>A blank frame is CHEAPER than a token frame, as it is in real CTC output (blank
    /// dominates between the spikes). The difference is load-bearing: it is what makes
    /// <see cref="BlankFramesDoNotInflateTheScore"/> able to tell a per-token mean from a per-frame one.</summary>
    private const float BlankHot = -0.005f;

    private static CtcWordSpotter Spotter(float minScore = CtcWordSpotter.DefaultMinScore, int maxOcc = 8)
        => new(Blank, Fs, minScore, maxOcc);

    /// <summary>A matrix from a per-frame argmax: <c>-1</c> means blank.</summary>
    private static float[] Frames(params int[] argmax)
    {
        var m = new float[argmax.Length * V];
        for (int t = 0; t < argmax.Length; t++)
        {
            int hot = argmax[t] < 0 ? Blank : argmax[t];
            float peak = hot == Blank ? BlankHot : Hot;
            for (int v = 0; v < V; v++) m[t * V + v] = v == hot ? peak : Cold;
        }
        return m;
    }

    private static CtcSpotQuery Q(string term, params int[] ids) => new(term, [], ids);

    private static IReadOnlyList<VocabularyGate.Detection> Run(
        float[] matrix, CtcWordSpotter spotter, params CtcSpotQuery[] queries) =>
        spotter.Spot(matrix, matrix.Length / V, V, queries);

    // MARK: - Finding things

    [Fact]
    public void FindsATermAndReportsItsFrameRange()
    {
        // ▁ ▁ ▁ 0 1 2 ▁ ▁    — the term occupies frames 3..5
        float[] m = Frames(-1, -1, -1, 0, 1, 2, -1, -1);

        var d = Assert.Single(Run(m, Spotter(), Q("Nemotron", 0, 1, 2)));
        Assert.Equal("Nemotron", d.Term);
        Assert.Equal(3 * Fs, d.StartTime, 6);
        Assert.Equal(6 * Fs, d.EndTime, 6);   // the end frame's audio runs to the NEXT boundary
        Assert.True(d.Score > -0.1f, $"a clean match should score near 0, got {d.Score}");
    }

    [Fact]
    public void DoesNotFindATermThatWasNotSaid()
    {
        float[] m = Frames(-1, 0, 1, 2, -1);
        Assert.Empty(Run(m, Spotter(), Q("Parakeet", 3, 4)));
    }

    [Fact]
    public void AllBlankAudioProducesNothing()
    {
        // "Zero insertions on silence" is an exit criterion, not a nicety: a spotter that fires on
        // silence would let the gate rewrite words in every quiet dictation.
        float[] m = Frames(-1, -1, -1, -1, -1, -1, -1, -1, -1, -1);
        Assert.Empty(Run(m, Spotter(), Q("Nemotron", 0, 1, 2), Q("Okta", 3, 4)));
    }

    [Fact]
    public void FindsEveryOccurrenceSeparately()
    {
        float[] m = Frames(0, 1, -1, -1, -1, -1, 0, 1, -1);

        var d = Run(m, Spotter(), Q("Jot", 0, 1));
        Assert.Equal(2, d.Count);
        Assert.Equal(0.0, d[0].StartTime, 6);
        Assert.Equal(2 * Fs, d[0].EndTime, 6);
        Assert.Equal(6 * Fs, d[1].StartTime, 6);
        Assert.Equal(8 * Fs, d[1].EndTime, 6);
    }

    [Fact]
    public void AdjacentFramesOfOneUtteranceCollapseToOneDetection()
    {
        // The accepting state stays hot for several frames after a match; without overlap suppression
        // this single utterance would surface as four near-identical proposals.
        float[] m = Frames(-1, 0, 0, 1, 1, 1, -1);
        Assert.Single(Run(m, Spotter(), Q("Jot", 0, 1)));
    }

    // MARK: - Boundaries

    [Fact]
    public void MatchAtTheVeryFirstFrameStartsAtZero()
    {
        float[] m = Frames(0, 1, 2, -1, -1);
        var d = Assert.Single(Run(m, Spotter(), Q("Nemotron", 0, 1, 2)));
        Assert.Equal(0.0, d.StartTime, 6);
        Assert.Equal(3 * Fs, d.EndTime, 6);
    }

    [Fact]
    public void MatchAtTheVeryLastFrameEndsAtTheEndOfTheAudio()
    {
        float[] m = Frames(-1, -1, 0, 1, 2);
        var d = Assert.Single(Run(m, Spotter(), Q("Nemotron", 0, 1, 2)));
        Assert.Equal(2 * Fs, d.StartTime, 6);
        Assert.Equal(5 * Fs, d.EndTime, 6);   // == frames * Fs, the full clip length
    }

    [Fact]
    public void SingleTokenTermIsHandled()
    {
        // M = 2L-1 = 1: the loop over interior states never runs and state 0 is also the accepting
        // state. Easy to break with an off-by-one and impossible to notice without a test.
        float[] m = Frames(-1, 3, -1);
        var d = Assert.Single(Run(m, Spotter(), Q("Ok", 3)));
        Assert.Equal(1 * Fs, d.StartTime, 6);
        Assert.Equal(2 * Fs, d.EndTime, 6);
    }

    // MARK: - Blanks and repeats — the whole subtlety

    [Fact]
    public void BlanksBetweenTokensDoNotBreakAMatch()
    {
        // A pause inside a multi-word term ("Claude ␣␣␣ Code") must still match, and must not be
        // penalised for the pause: blank frames are cheap and the score divides by TOKEN count.
        float[] m = Frames(-1, 0, -1, -1, -1, 1, -1);
        var d = Assert.Single(Run(m, Spotter(), Q("Claude Code", 0, 1)));
        Assert.Equal(1 * Fs, d.StartTime, 6);
        Assert.Equal(6 * Fs, d.EndTime, 6);
        Assert.True(d.Score > -0.2f, $"a bridged match should still score near 0, got {d.Score}");
    }

    [Fact]
    public void RepeatedTokenRequiresABlankBetweenTheCopies()
    {
        // CTC collapses "0 0" on consecutive frames into ONE emission, so a term whose tokens repeat
        // can only be aligned through the blank. Three hot frames of id 0 and no blank between them is
        // therefore NOT the term "0 0" — dropping the ext[j] != ext[j-2] guard makes this pass.
        float[] noBlank = Frames(-1, 0, 0, 0, -1);
        Assert.Empty(Run(noBlank, Spotter(), Q("Bookkeeper", 0, 0)));

        float[] withBlank = Frames(-1, 0, -1, 0, -1);
        var d = Assert.Single(Run(withBlank, Spotter(), Q("Bookkeeper", 0, 0)));
        Assert.Equal(1 * Fs, d.StartTime, 6);
        Assert.Equal(4 * Fs, d.EndTime, 6);
    }

    [Fact]
    public void DifferentAdjacentTokensNeedNoBlank()
    {
        // The converse of the rule above: two DIFFERENT labels may sit on consecutive frames. Gating
        // the skip on "always" instead of "different labels" would reject every tightly-spoken term.
        float[] m = Frames(-1, 0, 1, -1);
        Assert.Single(Run(m, Spotter(), Q("Jot", 0, 1)));
    }

    [Fact]
    public void AHeldTokenIsOneEmissionNotTwo()
    {
        float[] m = Frames(-1, 0, 0, 0, 1, 1, -1);
        var d = Assert.Single(Run(m, Spotter(), Q("Jot", 0, 1)));
        Assert.True(d.Score > -0.1f, $"holding a token must not be penalised, got {d.Score}");
    }

    // MARK: - Normalization

    [Fact]
    public void ScoreIsLengthNormalized_LongAndShortTermsAreComparable()
    {
        // THE property the threshold depends on. Both terms are matched with identical per-token
        // quality; a raw path score would differ by 3x (2 tokens vs 6) and any single threshold would
        // then be meaningless. Mean-log-prob-per-token puts them on top of each other.
        float[] shortM = Frames(-1, 0, 1, -1);
        float[] longM = Frames(-1, 0, 1, 2, 3, 4, 0, -1);

        float s2 = Assert.Single(Run(shortM, Spotter(), Q("Jot", 0, 1))).Score;
        float s6 = Assert.Single(Run(longM, Spotter(), Q("Kubernetes", 0, 1, 2, 3, 4, 0))).Score;

        Assert.Equal(s2, s6, 3);
        Assert.Equal(Hot, s2, 3);   // and the unit is meaningful: mean log-prob per token
    }

    [Fact]
    public void ScoreIsLengthNormalized_APartialMissCostsProportionallyLess_OnALongerTerm()
    {
        // One bad token out of six hurts less than one bad token out of two. That is the intended
        // behaviour of a per-token mean and is worth pinning, because the alternative (per-FRAME mean)
        // would instead be dominated by however many blank frames happened to sit inside the term.
        float[] shortM = Frames(-1, 0, 4, -1);           // second token wrong
        float[] longM = Frames(-1, 0, 1, 2, 3, 4, 4, -1); // last token wrong

        float s2 = Run(shortM, Spotter(-20f), Q("Jot", 0, 1)).Single().Score;
        float s6 = Run(longM, Spotter(-20f), Q("Kubernetes", 0, 1, 2, 3, 4, 0)).Single().Score;
        Assert.True(s6 > s2, $"one miss in six ({s6}) should beat one miss in two ({s2})");
    }

    [Fact]
    public void BlankFramesDoNotInflateTheScore()
    {
        // A per-FRAME mean would rate the bridged match far better than the tight one, purely because
        // blank frames are nearly free. Dividing by token count makes the two essentially equal — which
        // is what stops a term spanning a long pause from sailing over any threshold.
        float[] tight = Frames(-1, 0, 1, -1);
        float[] bridged = Frames(-1, 0, -1, -1, -1, -1, -1, -1, 1, -1);

        float a = Assert.Single(Run(tight, Spotter(), Q("Claude Code", 0, 1))).Score;
        float b = Assert.Single(Run(bridged, Spotter(), Q("Claude Code", 0, 1))).Score;

        // Divide by PATH LENGTH instead and the bridged match scores ~3x better than the tight one on
        // exactly the same token evidence, purely because six near-free blank frames dilute the mean.
        Assert.True(b <= a + 1e-4f, $"tight {a} vs bridged {b} — blank frames are inflating the score");
        Assert.True(Math.Abs(a - b) < 0.1f, $"tight {a} vs bridged {b} — bridging is over-penalised");
    }

    // MARK: - Threshold

    [Fact]
    public void ThresholdIsHonoured()
    {
        float[] m = Frames(-1, 0, 4, -1);   // one of two tokens is wrong ⇒ ≈ -4.0 per token

        Assert.Empty(Run(m, Spotter(), Q("Jot", 0, 1)));            // default -2.3 rejects it
        Assert.Single(Run(m, Spotter(minScore: -6f), Q("Jot", 0, 1)));
    }

    [Fact]
    public void DefaultThresholdSitsBetweenTheMeasuredHitAndDecoyBands()
    {
        // Pins the derivation, not the taste. Measured (CtcSpotterTests.M2_Threshold…): true hits run
        // -0.038 … -1.325, the strongest decoy candidate is -5.679. Anything outside that gap is a
        // threshold nobody measured.
        Assert.InRange(CtcWordSpotter.DefaultMinScore, -5.6f, -1.4f);
    }

    // MARK: - Unmatchable queries

    [Theory]
    [InlineData(new int[0])]          // a length-1 term encodes to nothing at all
    [InlineData(new[] { 0, 99 })]     // an id outside the head's range
    [InlineData(new[] { 0, Blank })]  // the blank itself can never be a term token
    public void UnmatchableQueriesAreSkippedNotThrown(int[] ids)
    {
        float[] m = Frames(-1, 0, 1, 2, -1);
        Assert.Empty(Run(m, Spotter(), Q("bad", ids)));
    }

    [Fact]
    public void AnUnmatchableQueryDoesNotSuppressAGoodOne()
    {
        float[] m = Frames(-1, 0, 1, -1);
        var d = Run(m, Spotter(), Q("bad", 0, 99), Q("Jot", 0, 1));
        Assert.Equal("Jot", Assert.Single(d).Term);
    }

    [Fact]
    public void EmptyInputsAreEmptyOutputs()
    {
        Assert.Empty(Spotter().Spot([], 0, V, [Q("Jot", 0, 1)]));
        Assert.Empty(Spotter().Spot(Frames(0, 1), 2, V, []));
    }

    // MARK: - Aliases and occurrence cap

    [Fact]
    public void AliasHitsReportUnderTheCanonicalTermAndDoNotDoubleCount()
    {
        // "Sri Ram" spoken tokenizes nothing like "Sriram" written, so both forms are searched. When
        // both land on the same audio the term must surface ONCE, under its canonical spelling.
        float[] m = Frames(-1, 0, 1, -1);
        var queries = new[]
        {
            new CtcSpotQuery("Sriram", ["Sri Ram"], new[] { 0, 1 }),
            new CtcSpotQuery("Sriram", ["Sri Ram"], new[] { 0 }),
        };

        var d = Assert.Single(Spotter().Spot(m, m.Length / V, V, queries));
        Assert.Equal("Sriram", d.Term);
        Assert.Equal("Sri Ram", Assert.Single(d.Aliases));
    }

    [Fact]
    public void OccurrenceCountIsCapped()
    {
        var argmax = new List<int>();
        for (int i = 0; i < 12; i++) { argmax.Add(0); argmax.Add(1); argmax.Add(-1); argmax.Add(-1); }

        var d = Run(Frames([.. argmax]), Spotter(maxOcc: 3), Q("Jot", 0, 1));
        Assert.Equal(3, d.Count);
    }

    [Fact]
    public void DetectionsComeBackInDocumentOrder()
    {
        // ApplyFromDetections splices in document order; unordered input would splice one detection
        // over another's offsets.
        float[] m = Frames(0, 1, -1, -1, 3, 4, -1, -1, 0, 1, -1);
        var d = Run(m, Spotter(), Q("Jot", 0, 1), Q("Okta", 3, 4));

        Assert.Equal(3, d.Count);
        Assert.True(d[0].StartTime <= d[1].StartTime && d[1].StartTime <= d[2].StartTime);
        Assert.Equal(["Jot", "Okta", "Jot"], d.Select(x => x.Term));
    }
}
