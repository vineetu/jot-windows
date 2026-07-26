using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The SEARCH MODEL half of the port-fidelity audit, pinned separately because these two cases
/// pull in opposite directions and only one of them is about the search.
///
/// Swift finds a span with `text.range(of:options:[.caseInsensitive])` — ICU loose matching, which
/// compares under CANONICAL EQUIVALENCE, so an NFC needle matches NFD text and the match length
/// may differ from the needle's. Mixed normalization is reachable in the field: transcript text
/// comes from the engine, but vocabulary terms are user-typed and macOS keyboards emit NFD.
///
/// Non-ASCII inputs are written as \u escapes on purpose: these cases are ABOUT the exact scalar
/// sequence, and a re-encoding of this file must not be able to change it.
/// </summary>
public class VocabSearchModelTests
{
    private static readonly EmbeddedCommonWordsProvider Common = EmbeddedCommonWordsProvider.Shared;

    /// <summary>
    /// An NFC needle must find (and correct) an NFD span. Under the port's previous ordinal
    /// search this proposal was dropped with no range, no record and no diagnostic — the
    /// correction simply never happened. Also pins the provenance anchors as GRAPHEME counts:
    /// the NFD span is 8 UTF-16 chars but 7 Characters, which is what Mac/iOS persist.
    /// </summary>
    [Fact]
    public void CanonicallyEquivalentSpan_IsFoundAndCorrected()
    {
        const string transcript = "call shri\u0301ram now";     // NFD
        var replacements = new List<RescoreProposal>
        {
            new("shr\u00EDram", "Sriram", true, 1.0f, 0.0f),    // NFC needle
        };

        VocabularyGate.Result result = VocabularyGate.Apply(
            transcript, new RescoreOutput(transcript, replacements, true), [], Common);

        Assert.Equal("call Sriram now", result.Text);
        VocabularyGate.Proposal p = Assert.Single(result.Proposals);
        Assert.Equal("applied", p.Outcome);
        Assert.Equal(5, p.OriginalStart);
        Assert.Equal(7, p.OriginalLength);
    }

    /// <summary>
    /// KNOWN, DELIBERATE divergence from
    /// <c>VocabPortFidelityTests.WholeWordRange_MatchesCanonicallyEquivalentText</c>, which asserts
    /// the same input publishes "call Joz\u00E9 now". It does not, on EITHER platform: "jos\u00E9"
    /// is a real entry in the shipped English common-word list, so guard (4) blocks it — a common
    /// original is surfaced for review, never silently rewritten, and no margin buys past that.
    /// Swift blocks it identically (its list is the same file, LF-stored, and its `decide` has no
    /// bypass at step 4), so the port matches Mac here and the fidelity test's expected TEXT is
    /// the thing that is wrong, not this behaviour.
    ///
    /// What the search fix DID change for this input is visible below: the proposal is now FOUND,
    /// so it becomes a reviewable "kept" record and an ask candidate instead of vanishing.
    /// </summary>
    [Fact]
    public void CommonWordOriginal_IsFoundCanonically_ThenBlockedByTheBrake()
    {
        const string transcript = "call Jose\u0301 now";        // NFD
        var replacements = new List<RescoreProposal>
        {
            new("Jos\u00E9", "Joz\u00E9", true, 1.0f, 0.0f),    // NFC needle
        };

        VocabularyGate.Result result = VocabularyGate.Apply(
            transcript, new RescoreOutput(transcript, replacements, true), [], Common);

        Assert.Equal(transcript, result.Text);
        VocabularyGate.Proposal p = Assert.Single(result.Proposals);   // found, not dropped
        Assert.Equal("BLOCK", p.Decision);
        Assert.Equal("kept", p.Outcome);
        Assert.True(p.AskCandidate);
        Assert.True(Common.Words("common-words").Contains("jos\u00E9"),
            "the block above is the common-word brake; if this word ever leaves the list, revisit");
    }
}