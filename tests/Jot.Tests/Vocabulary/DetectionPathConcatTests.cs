using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// Exact-concat of a single-word term over a multi-word span. The decoder's spaces are not
/// evidence; the common-word brake does not fire when the concatenated skeleton IS the term.
/// Search considers a window wider than the term only when that exactness holds — a bare
/// spotter detection (empty aliases) must reach the pair. Near-match concatenations and a
/// span that is only part of the term stay refused, including as multi-word candidates.
/// </summary>
public class DetectionPathConcatTests
{
    private static readonly EmbeddedCommonWordsProvider Common = EmbeddedCommonWordsProvider.Shared;

    private static VocabularyGate.Detection Det(
        string term, params string[] aliases) =>
        new(term, aliases, -0.36f, 0.4, 0.6);

    private static VocabularyGate.Result Apply(
        string transcript, VocabularyGate.Detection det, double duration = 2.0) =>
        VocabularyGate.ApplyFromDetections(transcript, [det], duration, Common);

    [Fact]
    public void ExactConcatOfASingleWordTermAppliesEvenWhenThePiecesAreEverydayWords()
    {
        // Neutral fixture: "nemo" is in the 24k list; "tron" is not. The pair's skeleton is the
        // term. The width-unlock alias is how the corrector (and a 2-word alias) hands the pair
        // to the gate — this test is the gate rule, not the detector.
        VocabularyGate.Result r = Apply("We use nemo tron here.", Det("Nemotron", "nemo tron"));

        Assert.Equal("We use Nemotron here.", r.Text);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal("nemo tron", p.OriginalWord);
    }

    [Fact]
    public void MotivatingSplitNameIsTheSameRelation()
    {
        VocabularyGate.Result r = Apply("call Sri Ram now", Det("Sriram", "Sri Ram"));

        Assert.Equal("call Sriram now", r.Text);
        Assert.Equal("applied", Assert.Single(r.Proposals).Outcome);
    }

    [Fact]
    public void ANearMatchConcatenationWhosePiecesAreEverydayWordsStaysBlocked()
    {
        // "nemo trim" vs "Nemotron" is 2/8 = 0.25 — inside the ceiling, not exact.
        // Concat-scan: this is the population that would be 2003 FP-absent if the exception
        // were "any plausible concat" (`there are` → `Therese` 0.25).
        VocabularyGate.Result r = Apply("We use nemo trim here.", Det("Nemotron", "nemo trim"));

        Assert.Equal("We use nemo trim here.", r.Text);
        Assert.Equal("kept", Assert.Single(r.Proposals).Outcome);
    }

    [Fact]
    public void ASpanThatIsOnlyPartOfTheTermStaysBlocked()
    {
        // One common piece, even a prefix, is not the term. Acoustic + strong score so the
        // earned ceiling will host on "nemo" (0.50) rather than leave it unplaced.
        var det = new VocabularyGate.Detection("Nemotron", [], -1.0f, 0.5, 1.0, Acoustic: true);
        VocabularyGate.Result r = Apply("We use nemo here.", det);

        Assert.Equal("We use nemo here.", r.Text);
        if (r.Proposals.Count > 0)
            Assert.Equal("kept", r.Proposals[0].Outcome);
    }

    [Fact]
    public void SpotterAloneAppliesAnExactConcatWithNoAliasToUnlockWidth()
    {
        // THE hole: English is spotter-only, detections carry empty aliases, maxWidth from
        // shapes is 1. Acoustic + E8 admits a shard (nemo vs Nemotron is 0.50, inside 0.65);
        // times put the mid on that shard so position-first prefers it. The pair must still
        // win — exact concat of the term, no alias, no corrector.
        var det = new VocabularyGate.Detection("Nemotron", [], -1.0f, 0.4, 0.6, Acoustic: true);
        VocabularyGate.Result r = Apply("We use nemo tron here.", det);

        Assert.Equal("We use Nemotron here.", r.Text);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal("nemo tron", p.OriginalWord);
    }

    [Fact]
    public void ANearMatchConcatenationWithNoAliasStaysUnreachable()
    {
        // Same acoustic / times as the apply case, so a plausibility-only wider search
        // would admit the pair (0.25) and then either apply or keep it. Exactness against
        // the term must refuse the window as a candidate — not "place then Decide".
        var det = new VocabularyGate.Detection("Nemotron", [], -1.0f, 0.4, 0.6, Acoustic: true);
        VocabularyGate.Result r = Apply("We use nemo trim here.", det);

        Assert.Equal("We use nemo trim here.", r.Text);
        Assert.Equal(0, r.Applied);
        Assert.DoesNotContain(r.Proposals, p => p.OriginalWord.Contains(' '));
    }

    [Fact]
    public void ASplitThatIsOnlyPartOfTheTermStaysUnreachable()
    {
        // Two ordinary words whose concat is a PREFIX of the term, not the term.
        // "nemo tr" vs "Nemotron" is 2/8 = 0.25 — inside the ceiling, not exact.
        var det = new VocabularyGate.Detection("Nemotron", [], -1.0f, 0.4, 0.6, Acoustic: true);
        VocabularyGate.Result r = Apply("We use nemo tr here.", det);

        Assert.Equal("We use nemo tr here.", r.Text);
        Assert.Equal(0, r.Applied);
        Assert.DoesNotContain(r.Proposals, p => p.OriginalWord.Contains(' '));
    }

    [Fact]
    public void RescorePathAppliesTheSameExactConcat()
    {
        // The jot-shared golden `merge-shape-classified` still expects "kept". Windows
        // diverges here; this is the replacement assertion.
        var reps = new[]
        {
            new RescoreProposal("sri ram", "Sriram", true, 3.5f, 0f),
        };
        VocabularyGate.Result r = VocabularyGate.Apply(
            "my name is sri ram ok",
            new RescoreOutput("my name is sri ram ok", reps, true),
            [],
            Common);

        Assert.Equal("my name is Sriram ok", r.Text);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal("merge", p.Shape);
    }
}
