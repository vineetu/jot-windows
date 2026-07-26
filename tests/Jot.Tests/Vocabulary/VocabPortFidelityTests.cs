using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// PORT-FIDELITY probes for the Swift→C# vocabulary port, covering inputs the shared golden
/// fixtures never exercise. Every assertion below encodes what
/// <c>jot-shared/Sources/JotVocabCore</c> does, not what this port currently does — a failure here
/// means Windows has diverged from Mac/iOS on that input.
///
/// The fixtures are all ASCII, single-space-separated and NFC. That is exactly the region where
/// Swift's grapheme/scalar string model and .NET's UTF-16 char model coincide, so they cannot see
/// any of this. Each test names the Swift line it pins.
///
/// Non-ASCII inputs are written as \u escapes on purpose: the whole point of these cases is the
/// exact scalar sequence, and a re-encoding of this file must not be able to change it.
/// </summary>
public class VocabPortFidelityTests
{
    private static readonly EmbeddedCommonWordsProvider Common = EmbeddedCommonWordsProvider.Shared;

    // MARK: - 1 · skeleton(): CharacterSet.alphanumerics is L* + M* + N*, char.IsLetterOrDigit is L* + Nd

    /// <summary>
    /// Swift `skeleton` filters scalars through `CharacterSet.alphanumerics` = Unicode general
    /// categories L*, **M***, and N*. The port filters with `char.IsLetterOrDigit` = L* + Nd only,
    /// so every Devanagari vowel sign (category Mc) is silently deleted and the term
    /// "रामा" skeletons identically to the heard "रम".
    ///
    /// Swift: lev(2-scalar span, 4-scalar term) = 2 / max(2,4) = 0.50 &gt; the 0.45 ceiling ⇒ the
    /// word does not align ⇒ AlignmentWindow is .blocked. The port scores 0.0 and lets it through.
    /// </summary>
    [Fact]
    public void Skeleton_KeepsCombiningMarks_SoAHindiTermCannotFalselyAlign()
    {
        const string ram = "रम";                          // राम minus its vowel signs
        const string raamaa = "रामा";           // two Mc vowel signs
        const string kod = "कोड";                    // identical on both sides

        (bool unique, _) = VocabularyGate.AlignmentWindow(
            termWords: [raamaa, kod],
            spanWords: [ram, kod]);

        Assert.False(unique, "Swift blocks: the Mc vowel signs are part of the skeleton, ratio 0.50 > 0.45");
    }

    /// <summary>
    /// Same root cause on the number side: `CharacterSet.alphanumerics` includes Nl/No
    /// (superscripts, fractions, Roman numerals); `char.IsLetterOrDigit` accepts only Nd. Swift's
    /// skeleton of "R²" is 2 scalars, so a heard "r" scores 1/2 = 0.50 and blocks; the port's
    /// skeleton is "r", scores 0.0, and aligns.
    /// </summary>
    [Fact]
    public void Skeleton_KeepsNonDecimalNumbers()
    {
        (bool unique, _) = VocabularyGate.AlignmentWindow(
            termWords: ["R²", "Score"],
            spanWords: ["r", "score"]);

        Assert.False(unique, "Swift blocks: U+00B2 (category No) counts in the skeleton, ratio 0.50 > 0.45");
    }

    /// <summary>
    /// Swift filters UNICODE SCALARS; the port filters UTF-16 chars. A non-BMP letter (here CJK
    /// Extension B U+2000B, a real Han ideograph) arrives as a surrogate pair and
    /// `char.IsLetterOrDigit` is false for both halves — so the port's skeleton is EMPTY and
    /// `WordAligns` short-circuits to false. A term therefore fails to align against a span that
    /// is scalar-for-scalar identical to it.
    /// </summary>
    [Fact]
    public void Skeleton_KeepsAstralLetters()
    {
        const string han = "𠀋𠀋";   // U+2000B ×2

        (bool unique, _) = VocabularyGate.AlignmentWindow(
            termWords: [han, "code"],
            spanWords: [han, "code"]);

        Assert.True(unique, "identical words must align; Swift skeletons them to 2 scalars, the port to \"\"");
    }

    // MARK: - 2 · perWordMinConfidence(): CharacterSet.whitespaces excludes newlines

    /// <summary>
    /// Swift trims each token with `trimmingCharacters(in: .whitespaces)` — Unicode Zs plus TAB
    /// only, NOT newlines (`.whitespacesAndNewlines` is the set that includes them). The port uses
    /// `string.Trim()`, which also strips \n \r \v \f U+0085 U+2028 U+2029.
    ///
    /// So for a token "ram\n" Swift keys the confidence map under "shriram\n", which `decide`
    /// never looks up: `measured` stays nil ⇒ confidence falls back to LowConfidence (0.85) ⇒ the
    /// 0.998-protector cannot fire ⇒ APPLY. The port keys it under "shriram", finds 0.99, and
    /// BLOCKS at guard (3). Same input, different transcript.
    /// </summary>
    [Fact]
    public void PerWordMinConfidence_DoesNotTrimNewlinesFromTokens()
    {
        const string transcript = "call shriram now";
        var replacements = new List<RescoreProposal>
        {
            new("shriram", "Sriram", true, 1.0f, 0.0f),   // margin 1.0, well under EarnedMargin
        };
        var timings = new List<TokenTiming>
        {
            new(" call", 0.99f),
            new(" shri", 0.99f),
            new("ram\n", 0.99f),     // Swift keeps the \n in the key; the port strips it
            new(" now", 0.99f),
        };

        VocabularyGate.Result result = VocabularyGate.Apply(
            transcript, new RescoreOutput(transcript, replacements, true), timings, Common);

        Assert.Equal("call Sriram now", result.Text);
        VocabularyGate.Proposal p = Assert.Single(result.Proposals);
        Assert.Equal(VocabularyGate.LowConfidence, p.Confidence);   // unknown, never measured
        Assert.Equal("applied", p.Outcome);
    }

    // MARK: - 3 · CorrectionKey.normalize(): full vs simple case mapping

    /// <summary>
    /// Swift's `String.lowercased()` applies FULL Unicode case mapping and may change length:
    /// U+0130 (İ) lowercases to "i" + U+0307 COMBINING DOT ABOVE (SpecialCasing.txt, unconditional).
    /// `ToLowerInvariant` is a length-preserving SIMPLE mapping and loses the dot.
    ///
    /// This is a STORE-KEY divergence, the one thing CorrectionKey exists to prevent: the same
    /// Turkish original yields different correction-store keys on Mac and Windows, so a learned
    /// override recorded on one platform never matches on the other. The shared fixture pins only
    /// the ASCII "I" row, so it cannot see this.
    /// </summary>
    [Fact]
    public void Normalize_UsesFullUnicodeLowercasing_NotSimpleMapping()
    {
        Assert.Equal("i̇stanbul", CorrectionKey.Normalize("İstanbul"));
    }

    // MARK: - 4 · wholeWordRange(): canonical-equivalent search vs Ordinal search

    /// <summary>
    /// Swift searches with `text.range(of:options:[.caseInsensitive])`. Without `.literal` that is
    /// NSString loose matching, which compares under CANONICAL EQUIVALENCE — an NFC needle matches
    /// NFD text and the returned range covers the NFD span. The port uses
    /// `IndexOf(..., StringComparison.OrdinalIgnoreCase)`, which is code-unit exact.
    ///
    /// Mixed normalization is reachable: transcript text comes from the engine, but vocabulary
    /// terms — and the proposals built from them — come from user typing, and macOS keyboards emit
    /// NFD. When the two forms disagree the port silently drops the proposal: no diagnostic, no
    /// record, the correction simply never happens.
    /// </summary>
    // SUPERSEDED — this probe originally asserted the text became "call Joze now" (accented). That
    // can never happen on EITHER platform: the accented "jose" is a literal entry in the shipped
    // English common-word list (verified: 24,059 entries, present), so gate guard (4) protects it —
    // a common original is surfaced for review, never silently rewritten, and no margin buys past
    // that step in the Swift either. The probe conflated "the search finds the span" (a real bug,
    // now fixed by the canonical-equivalent search in WholeWordRange) with "the gate applies it"
    // (never true).
    //
    // Both halves are pinned properly in VocabSearchModelTests: the search fix on an OOV original
    // that genuinely applies, and this exact input pinned as found-then-blocked-by-the-brake.

    // MARK: - 5 · provenance offsets: Character distance vs UTF-16 index

    /// <summary>
    /// Swift's `originalStart` / `publishedStart` are `String.distance` and `String.count` —
    /// GRAPHEME CLUSTER counts. The port uses UTF-16 indices. The port's header comment scopes the
    /// risk to "text outside the BMP (emoji)", but the commonest divergence on Windows needs no
    /// emoji at all: "\r\n" is a SINGLE Character in Swift and two chars in .NET, so every CRLF
    /// ahead of a span shifts both anchors by one.
    ///
    /// These anchors are the provenance ledger's identity and its live anchor, so a mismatch
    /// degrades review anchoring rather than the transcript — but on plain Windows text, not on an
    /// exotic input.
    /// </summary>
    [Fact]
    public void ProvenanceOffsets_CountGraphemeClusters_SoCrlfIsOne()
    {
        const string transcript = "hi\r\ncall shriram now";
        var detections = new List<VocabularyGate.Detection>
        {
            new("Sriram", [], -8.0f, 0.5, 1.0),
        };

        VocabularyGate.Result result = VocabularyGate.ApplyFromDetections(
            transcript, detections, 2.0, Common);

        VocabularyGate.Proposal p = Assert.Single(result.Proposals);
        Assert.Equal("hi\r\ncall Sriram now", result.Text);   // splicing is correct either way
        Assert.Equal(8, p.OriginalStart);                     // Swift counts "\r\n" as one Character
        Assert.Equal(8, p.PublishedStart);
    }

    // MARK: - 6 · preservingEdgePunctuation(): Character.isNumber vs char.IsLetterOrDigit

    /// <summary>
    /// Swift scans the edges with `!$0.isLetter &amp;&amp; !$0.isNumber`, and `Character.isNumber` is
    /// true for EVERY Unicode number (its own documentation lists "⅚"). `char.IsLetterOrDigit`
    /// accepts Nd only, so the port classifies a leading fraction/superscript as PUNCTUATION and
    /// carries it onto the replacement.
    ///
    /// This one changes the published TEXT, not just an anchor: Swift emits "Sriram", the port
    /// emits "½Sriram".
    /// </summary>
    [Fact]
    public void PreservingEdgePunctuation_TreatsAllUnicodeNumbersAsContent()
    {
        const string transcript = "call ½shriram now";
        var detections = new List<VocabularyGate.Detection>
        {
            new("Sriram", [], -8.0f, 0.5, 1.0),
        };

        VocabularyGate.Result result = VocabularyGate.ApplyFromDetections(
            transcript, detections, 2.0, Common);

        Assert.Equal("call Sriram now", result.Text);
    }
}
