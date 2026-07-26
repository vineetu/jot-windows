using System.Collections.Generic;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The inflection guard (E7) on <see cref="VocabularyGate.ApplyFromDetections"/> — a FIFTH WINDOWS
/// DIVERGENCE, and the only thing that will notice if it is deleted.
///
/// E6 measured the over-correction brake in 19 languages and found its shape: the brake is a TYPE
/// lookup over a 24 000-entry frequency list, so an inflected form the list does not list walks
/// straight past it even when the list holds the very word it is an inflection of. `пирамид` is not
/// in the Russian list; `пирамиды` is, one character longer.
///
/// The cases below are rows from that measurement, not invented ones — including the two the guard
/// deliberately does NOT catch, which are pinned here so removing that limit is a decision somebody
/// makes on purpose.
/// </summary>
public class DetectionPathInflectedCommonTests
{
    private static VocabularyGate.Result Run(string text, string term, string? resource) =>
        VocabularyGate.ApplyFromDetections(
            text,
            [new VocabularyGate.Detection(term, [], -0.14f, 0.5, 1.0)],
            2.0,
            EmbeddedCommonWordsProvider.Shared,
            resource);

    [Theory]
    // MEASURED, E6 ml-rows: applied over text the engine had already got right (FP-overwrote).
    [InlineData("построили три пирамид высотой", "Пирамида", "common-words-ru")]
    [InlineData("zbudowali piramid o wysokości", "Piramida", "common-words-pl")]
    public void AnEverydayWordMinusItsEndingIsNotOverwritten(string text, string term, string resource)
    {
        VocabularyGate.Result r = Run(text, term, resource);
        Assert.Equal(text, r.Text);
        Assert.Equal(0, r.Applied);

        // Blocked, not dropped: the row still reaches review, like every other force-block, and it
        // is never a live-ask candidate because the span identity is what is in doubt.
        VocabularyGate.Proposal p = Assert.Single(r.Proposals);
        Assert.Equal("kept", p.Outcome);
        Assert.Equal("BLOCK", p.Decision);
        Assert.False(p.AskCandidate);
    }

    [Fact]
    public void WithoutAFrequencyListTheGuardIsInert()
    {
        // A language with no list runs the gate with the brake absent, and this guard is an extension
        // of the brake — it must degrade the same way rather than inventing protection from nothing.
        Assert.Equal("построили три Пирамида высотой",
            Run("построили три пирамид высотой", "Пирамида", null).Text);
    }

    [Fact]
    public void AnOrdinaryNearMissStillCorrects()
    {
        // The whole feature, on the path the guard lives on. "shriram" shares no long stem with
        // "Sriram" (they differ at the second character), so condition 1 is false before the
        // frequency list is even consulted.
        Assert.Equal("talk to Sriram", Run("talk to shriram", "Sriram", "common-words").Text);
    }

    // MARK: - The limits, pinned on purpose

    [Fact]
    public void AProperNounInAnotherCaseIsStillOverwritten_AndThatIsMeasured()
    {
        // E6's headline row, and the guard does NOT catch it: `Аризона` is absent from the Russian
        // frequency list at every truncation, so there is nothing to vouch for it. Structurally it is
        // indistinguishable from `Аризоне` → `Аризоны`, which is CORRECT and was measured in the same
        // corpus from the same term — firing on the shape alone was measured at 30 correct
        // corrections destroyed for 33 false ones prevented. Closing this needs a lexicon of
        // inflected proper nouns, which we do not ship.
        Assert.Equal("мы поехали в Аризоны летом",
            Run("мы поехали в Аризона летом", "Аризоны", "common-words-ru").Text);
    }

    [Fact]
    public void AShortSpanIsBelowTheFloor()
    {
        // "wigili" is six characters and "wigilia" is in the Polish list, so only the length floor
        // stops this — and it stops it because at six the same rule also destroys this exact
        // correction, which is a real one: the reference says "Wigilii". MEASURED: dropping the floor
        // to six blocks four correct corrections to catch two more false ones.
        Assert.Equal("w noc Wigilii spotkanie",
            Run("w noc wigili spotkanie", "Wigilii", "common-words-pl").Text);
    }

    // MARK: - The predicate itself

    [Theory]
    [InlineData("piramid", "Piramida", true)]
    // Shared stem too short: these two diverge at character 4, so no stem, no guard.
    [InlineData("pirasid", "Piramida", false)]
    // A tail longer than three on either side is two words, not one word twice inflected.
    [InlineData("piramid", "Piramidalny", false)]
    // Not one character short of any everyday word.
    [InlineData("pirimid", "Piramida", false)]
    public void ThePredicate(string span, string term, bool expected) =>
        Assert.Equal(expected, VocabularyGate.IsInflectionOfCommonWord(
            span, term, EmbeddedCommonWordsProvider.Shared.Words("common-words-pl")));

    [Fact]
    public void MultiWordSpansAndTermsAreOutOfScope()
    {
        IReadOnlySet<string> pl = EmbeddedCommonWordsProvider.Shared.Words("common-words-pl");
        Assert.False(VocabularyGate.IsInflectionOfCommonWord("w piramid", "Piramida", pl));
        Assert.False(VocabularyGate.IsInflectionOfCommonWord("piramid", "Piramida X", pl));
    }

    [Fact]
    public void AnIdenticalSkeletonIsSomebodyElsesRow()
    {
        // Same skeleton = a casing or punctuation difference. The identity no-op and the apostrophe
        // guard already own that row and they say different things about it; this guard must not
        // answer for them.
        Assert.False(VocabularyGate.IsInflectionOfCommonWord(
            "piramida", "Piramida", EmbeddedCommonWordsProvider.Shared.Words("common-words-pl")));
    }
}
