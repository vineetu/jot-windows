using System.Globalization;
using System.Text;

namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/CorrectionKey.swift` (pinned commit 5326460).
// Conformance-locked by correction_key_normalize.json + pair_key.json.

/// <summary>
/// ONE normalization for every correction-learning identity key. EXACT-string semantics under:
/// NFC precompose → case-fold → collapse internal whitespace runs → trim OUTER punctuation only.
/// Internal punctuation and word boundaries are PRESERVED (can't ≠ cant, "a part" ≠ "apart") —
/// skeleton-canonical keys were rejected upstream for exactly those collisions.
/// </summary>
public static class CorrectionKey
{
    private const string OuterPunctuation = " .,!?;:\"'()";

    /// <summary>
    /// Locale-INVARIANT lowercasing is load-bearing: keys must fold identically on every machine
    /// or stored corrections stop matching. `ToLowerInvariant` never produces the Turkish dotless
    /// "ı" for "I" — the shared fixture pins that case deliberately, so a future switch to a
    /// culture-aware overload fails the test instead of silently corrupting keys in the field.
    /// </summary>
    public static string Normalize(string s)
    {
        string nfc = Lowercased(s.Normalize(NormalizationForm.FormC));
        string collapsed = string.Join(' ', nfc.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim(OuterPunctuation.ToCharArray());
    }

    /// <summary>
    /// Swift's no-argument <c>String.lowercased()</c> — the ONE case-fold every identity key,
    /// skeleton and override compare in this subsystem must use, so two code paths can never
    /// disagree about the same row.
    ///
    /// Swift lowercases with FULL Unicode case mapping; `ToLowerInvariant` is the length-preserving
    /// SIMPLE mapping, and the gap is not academic: U+0130 "İ" must fold to "i" + U+0307, or a
    /// Turkish original learned on a Mac keys differently here and its override never fires. U+0130
    /// is the only UNCONDITIONAL multi-character lowercase mapping in Unicode's SpecialCasing.txt,
    /// so special-casing it covers the whole gap.
    ///
    /// DELIBERATE NON-FIX — Greek final sigma. Swift's ICU fold applies the Final_Sigma condition
    /// ("ΟΔΟΣ" → …ς, U+03C2); this returns …σ (U+03C3). Do NOT "fix" it: the shipped
    /// common-words-el.txt contains no U+03C2 at all, so σ is the form our own data matches, and
    /// switching would silently disable the Greek common-word brake.
    /// </summary>
    // Written as escapes on purpose: the replacement is "i" + a COMBINING dot, which no editor
    // renders distinguishably from a plain "i".
    public static string Lowercased(string s) =>
        (s.Contains('\u0130') ? s.Replace("\u0130", "i\u0307") : s).ToLowerInvariant();

    /// <summary>
    /// The one "&lt;originalWord&gt;|&lt;term&gt;" membership-key shape the stores and publishers use.
    /// **Asymmetric on purpose:** the original side is fully normalized, the term side is only
    /// lowercased — matching what the shared stores produce. Locked by pair_key.json.
    /// </summary>
    public static string PairKey(string originalWord, string term) =>
        $"{Normalize(originalWord)}|{Lowercased(term)}";
}
