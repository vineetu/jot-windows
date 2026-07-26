using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The model-free corrector, always measured THROUGH the gate — what it proposes on its own is not the
/// product, and a rule that looks right in isolation but is then blocked (or worse, placed somewhere
/// else) would read as passing here and fail in the field.
/// </summary>
public class VocabularyCorrectorTests
{
    private const double Seconds = 10.0;

    private static VocabularyGate.Result Run(string text, params string[] terms) =>
        Run(text, terms.Select(t => new VocabularyTerm { Text = t }).ToList());

    private static VocabularyGate.Result Run(string text, IReadOnlyList<VocabularyTerm> terms)
    {
        IReadOnlyList<VocabularyGate.Detection> detections =
            new VocabularyCorrector().Spot(text, terms, Seconds);
        return VocabularyGate.ApplyFromDetections(
            text, detections, Seconds, EmbeddedCommonWordsProvider.Shared, "common-words");
    }

    // MARK: - The near band, which is the whole point

    [Fact]
    public void NearMissIsCorrected()
    {
        VocabularyGate.Result r = Run("We shipped Neumotron this week.", "Nemotron");
        Assert.Equal("We shipped Nemotron this week.", r.Text);
        Assert.Equal(1, r.Applied);
    }

    [Fact]
    public void PunctuationOnTheHostWordSurvives()
    {
        VocabularyGate.Result r = Run("It runs on Neumotron, mostly.", "Nemotron");
        Assert.Equal("It runs on Nemotron, mostly.", r.Text);
    }

    [Fact]
    public void MergedSplitWordIsRejoined()
    {
        // Width unlock: the gate only tries a window width some term/alias shape has, so without the
        // matched span fed back as an alias this correction is found and then never placed.
        VocabularyGate.Result r = Run("We climbed nyira gongo last year.", "Nyiragongo");
        Assert.Equal("We climbed Nyiragongo last year.", r.Text);
    }

    [Fact]
    public void MergedAsrWordSplitsBackIntoAMultiWordTerm()
    {
        VocabularyGate.Result r = Run("We met ramanathan today.", "Ramaa Nathan");
        Assert.Equal("We met Ramaa Nathan today.", r.Text);
    }

    [Fact]
    public void MultiWordTermRecoversFromAMisheardHead()
    {
        VocabularyGate.Result r = Run("I asked cloud code about it.", "Claude Code");
        Assert.Equal("I asked Claude Code about it.", r.Text);
    }

    // MARK: - Precision: the ways it must refuse

    [Fact]
    public void ShortTermsRequireAnExactMatch()
    {
        // One edit on a three-letter term is a third of the word; every such term would collide with
        // ordinary text.
        Assert.Equal(0, VocabularyCorrector.EditBudget(3));
        VocabularyGate.Result r = Run("I got a job done today.", "Jot");
        Assert.Empty(r.Proposals);
        Assert.Equal("I got a job done today.", r.Text);
    }

    [Fact]
    public void AWordTheEngineAlreadyGotRightIsLeftAlone()
    {
        VocabularyGate.Result r = Run("Nemotron shipped today.", "Nemotron");
        Assert.Empty(r.Proposals);
        Assert.Equal("Nemotron shipped today.", r.Text);
    }

    [Fact]
    public void EverydayWordsAreNeverSilentlyRewritten()
    {
        // "list" is one edit from "Lisa" — this is the shipped Spanish incident's shape, and the brake
        // that stops it lives in the gate, not here. The proposal must still surface for review.
        VocabularyGate.Result r = Run("Make a list of names.", "Lisa");
        Assert.Equal("Make a list of names.", r.Text);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("kept", p.Outcome);
        Assert.Equal("BLOCK", p.Decision);
    }

    [Fact]
    public void ATieBetweenTwoTermsIsDroppedRatherThanGuessed()
    {
        VocabularyGate.Result r = Run("We discussed the marin sample.", "Sarin", "Karin");
        Assert.Empty(r.Proposals);
        Assert.Equal("We discussed the marin sample.", r.Text);
    }

    [Fact]
    public void UnrelatedTextIsUntouchedByALargeTermList()
    {
        string[] terms =
        [
            "Nemotron", "Parakeet", "Nyiragongo", "Sundarbans", "Kirchner", "Tendulkar",
            "Balasubramanian", "Timbuktu", "Schengen", "Kundalini", "Sagittarius", "Galapagos",
        ];
        const string text = "Please send the quarterly report to the finance team before Friday.";
        VocabularyGate.Result r = Run(text, terms);
        Assert.Equal(text, r.Text);
        Assert.Empty(r.Proposals);
    }

    // MARK: - The band the corrector cannot reach, and neither can anything else

    [Fact]
    public void TheFarBandIsOutOfReachForBOTHDetectionSources()
    {
        // Measured case: the acoustic spotter scored "Parakeet" best of five while the engine had
        // written "Herrakit". The corrector refuses it — and so does the gate, at 0.63 against a 0.45
        // ceiling, so an acoustic detection for it cannot be APPLIED either. Anything claiming the
        // spotter's marginal value lives out here has to explain this number first.
        Assert.True(VocabularyGate.Gap("herrakit", "Parakeet", []) > VocabularyGate.PlausibilityCeiling);

        VocabularyGate.Result byText = Run("the herrakit model", "Parakeet");
        Assert.Empty(byText.Proposals);

        var acoustic = new[] { new VocabularyGate.Detection("Parakeet", [], -0.313f, 1.0, 1.5) };
        VocabularyGate.Result bySound = VocabularyGate.ApplyFromDetections(
            "the herrakit model", acoustic, Seconds, EmbeddedCommonWordsProvider.Shared, "common-words");
        Assert.Equal("the herrakit model", bySound.Text);
        Assert.Empty(bySound.Proposals);
    }

    [Fact]
    public void AMergeWhoseHalvesAreEverydayWordsStaysBlocked()
    {
        // "nemo" is in the 24k English list, so the common-word brake blocks "nemo tron" → "Nemotron".
        // Documented because it looks like a corrector bug and is the gate refusing to rewrite an
        // everyday word — the same rule that stops "list" → "Lisa".
        VocabularyGate.Result r = Run("We use nemo tron here.", "Nemotron");
        Assert.Equal("We use nemo tron here.", r.Text);
        Assert.All(r.Proposals, p => Assert.Equal("kept", p.Outcome));
    }

    // MARK: - Distance

    [Fact]
    public void TranspositionsCostOneEdit()
    {
        Assert.Equal(1, VocabularyCorrector.RestrictedEdits(
            VocabularyGate.SkeletonOf("nemotorn"), VocabularyGate.SkeletonOf("nemotron"), 2));
    }

    [Fact]
    public void EditBudgetScalesWithTermLength()
    {
        Assert.Equal(0, VocabularyCorrector.EditBudget(1));
        Assert.Equal(0, VocabularyCorrector.EditBudget(3));
        Assert.Equal(1, VocabularyCorrector.EditBudget(4));
        Assert.Equal(1, VocabularyCorrector.EditBudget(5));
        Assert.Equal(2, VocabularyCorrector.EditBudget(9));
        Assert.Equal(2, VocabularyCorrector.EditBudget(10));
        Assert.Equal(3, VocabularyCorrector.EditBudget(11));
    }

    [Fact]
    public void DistanceAbandonsOnceEveryAlignmentExceedsTheBudget()
    {
        Assert.True(VocabularyCorrector.RestrictedEdits(
            VocabularyGate.SkeletonOf("completely different"), VocabularyGate.SkeletonOf("Nemotron"), 2) > 2);
    }

    // MARK: - Degenerate input

    [Fact]
    public void NothingToDoIsCheapAndSilent()
    {
        var corrector = new VocabularyCorrector();
        Assert.Empty(corrector.Spot("", [new VocabularyTerm { Text = "Nemotron" }], Seconds));
        Assert.Empty(corrector.Spot("   ", [new VocabularyTerm { Text = "Nemotron" }], Seconds));
        Assert.Empty(corrector.Spot("some text", [], Seconds));
    }

    [Fact]
    public void PlacementSurvivesAMissingDuration()
    {
        // The re-transcribe path has no recording duration; the gate then assumes mid-transcript and
        // places on plausibility alone, which must still land on the only plausible word.
        var terms = new[] { new VocabularyTerm { Text = "Nemotron" } };
        IReadOnlyList<VocabularyGate.Detection> d = new VocabularyCorrector().Spot(
            "We shipped Neumotron this week.", terms, 0);
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            "We shipped Neumotron this week.", d, 0, EmbeddedCommonWordsProvider.Shared, "common-words");
        Assert.Equal("We shipped Nemotron this week.", r.Text);
    }

    // MARK: - Cost

    [Fact]
    public void AFullTermListOnALongTranscriptStaysOffTheStopBudget()
    {
        var terms = Enumerable.Range(0, VocabularyStore.MaxTerms)
            .Select(i => new VocabularyTerm { Text = $"Nemotron{i:D3}" })
            .ToList();
        string text = string.Join(' ', Enumerable.Repeat(
            "the quick brown fox jumps over the lazy dog while nobody watches", 30));

        var corrector = new VocabularyCorrector();
        corrector.Spot(text, terms, Seconds);          // warm the JIT before timing
        var sw = Stopwatch.StartNew();
        corrector.Spot(text, terms, Seconds);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500,
            $"{terms.Count} terms over {text.Split(' ').Length} words took {sw.ElapsedMilliseconds} ms");
    }
}
