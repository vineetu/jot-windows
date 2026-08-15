using Jot.Text;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// Casing-only single-word identity. The letters already agree; the saved form is the user's
/// intent. Not a correction: no proposal, no ask, no pill chip, no learning net. Multi-word
/// terms already recase through Decide — this is the single-word arm the golden fixture used
/// to pin as a no-op.
/// </summary>
public class DetectionPathCasingTests
{
    private static readonly EmbeddedCommonWordsProvider Common = EmbeddedCommonWordsProvider.Shared;

    private static VocabularyGate.Result Apply(string transcript, string term) =>
        VocabularyGate.ApplyFromDetections(
            transcript,
            [new VocabularyGate.Detection(term, [], -0.36f, 0.4, 0.6)],
            2.0,
            Common);

    private static VocabularyGate.Result ViaCorrector(string transcript, string term)
    {
        IReadOnlyList<VocabularyGate.Detection> dets =
            new VocabularyCorrector().Spot(transcript, [new VocabularyTerm { Text = term }], 2.0, "en-US");
        return VocabularyGate.ApplyFromDetections(transcript, dets, 2.0, Common, "common-words");
    }

    // MARK: - The relation

    [Theory]
    [InlineData("talk to xabcd", "XaBcD", "talk to XaBcD")]           // mixed, decoded lower
    [InlineData("talk to XABCD", "XaBcD", "talk to XaBcD")]           // decoded upper
    [InlineData("xabcd left early.", "XaBcD", "XaBcD left early.")]   // sentence-initial
    [InlineData("the xwidget is here", "xWidget", "the xWidget is here")] // leading-lower saved
    [InlineData("the XWIDGET is here", "xWidget", "the xWidget is here")]
    [InlineData("the openxx model", "OpenXX", "the OpenXX model")]     // internal caps
    public void SameLettersWrongCasePublishesTheSavedForm(string text, string term, string expect)
    {
        VocabularyGate.Result r = Apply(text, term);
        Assert.Equal(expect, r.Text);
        Assert.Empty(r.Proposals);
        Assert.Equal(0, r.Applied);
    }

    [Fact]
    public void ExactCaseMatchIsStillSilent()
    {
        const string text = "talk to XaBcD";
        VocabularyGate.Result r = Apply(text, "XaBcD");
        Assert.Equal(text, r.Text);
        Assert.Empty(r.Proposals);
    }

    [Fact]
    public void ALetterDifferenceIsNotASilentRecase()
    {
        // Extra letter: Normalize disagrees, so this is not the casing branch. Silent
        // publish is reserved for same-letters; a letter change is a real correction or a refusal.
        const string text = "talk to xabcdz";
        VocabularyGate.Result r = Apply(text, "XaBcD");
        if (r.Proposals.Count == 0)
            Assert.Equal(text, r.Text);
        else
            Assert.NotEqual(CorrectionKey.Normalize("xabcdz"), CorrectionKey.Normalize("XaBcD"));
    }

    [Fact]
    public void ALetterDifferenceThatAppliesIsAReportedCorrection()
    {
        // One deleted letter, inside the ceiling (1/5 = 0.20). Must go through Decide, not
        // the silent recase — the owner sees this as a real swap.
        VocabularyGate.Result r = Apply("talk to xabc", "XaBcD");
        Assert.Equal("talk to XaBcD", r.Text);
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("applied", p.Outcome);
        Assert.True(p.AskCandidate);
    }

    [Fact]
    public void EdgePunctuationSurvivesARecase()
    {
        VocabularyGate.Result r = Apply("call xabcd, then rest", "XaBcD");
        Assert.Equal("call XaBcD, then rest", r.Text);
        Assert.Empty(r.Proposals);
    }

    // MARK: - Common-word brake, ask, cleanup

    [Fact]
    public void ACommonWordThatDiffersOnlyByCaseIsStillRecasedAndNotAsked()
    {
        // "apple" is in the 24k list. The brake must not turn a casing-only hit into a
        // reviewable kept or an apply-ask; the letters already agree.
        VocabularyGate.Result r = Apply("the apple fell", "Apple");
        Assert.Equal("the Apple fell", r.Text);
        Assert.Empty(r.Proposals);
    }

    [Fact]
    public void ALowercaseSavedFormWinsEvenAtSentenceStart()
    {
        // Saved form is the user's intent. Cleanup already ran (see next test); nothing
        // after the gate recapitalises a sentence start.
        VocabularyGate.Result r = Apply("Xabcd left early.", "xabcd");
        Assert.Equal("xabcd left early.", r.Text);
        Assert.Empty(r.Proposals);
    }

    [Fact]
    public void CleanupDoesNotRecaseAfterTheGate()
    {
        // Contract: TextPipeline.Clean runs BEFORE vocab. PostProcessing does not recase.
        // Running Clean again on the gate output must not undo the saved casing.
        string cleaned = TextPipeline.Clean("xabcd left early.", "English", isNemotron: true);
        VocabularyGate.Result r = Apply(cleaned, "XaBcD");
        Assert.Contains("XaBcD", r.Text, StringComparison.Ordinal);
        string again = TextPipeline.Clean(r.Text, "English", isNemotron: true);
        Assert.Contains("XaBcD", again, StringComparison.Ordinal);
        Assert.DoesNotContain("Xabcd", again, StringComparison.Ordinal);
    }

    // MARK: - Corrector (CLI / textual path)

    [Fact]
    public void TheCorrectorFeedsCasingOnlyToTheGate()
    {
        VocabularyGate.Result r = ViaCorrector("talk to xabcd", "XaBcD");
        Assert.Equal("talk to XaBcD", r.Text);
        Assert.Empty(r.Proposals);
    }

    [Fact]
    public void TheCorrectorStillClaimsAnExactIdentitySoAWiderWindowCannotStealIt()
    {
        // MEASURED shape in Place(): without claiming the exact one-word match first,
        // "George W" won the two-word slot for the term "George" and deleted the initial.
        VocabularyGate.Result r = ViaCorrector("George W arrived.", "George");
        Assert.Equal("George W arrived.", r.Text);
        Assert.Empty(r.Proposals);
    }
}
