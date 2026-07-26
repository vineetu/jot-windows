using System;
using System.Collections.Generic;

namespace Jot.Transcription.Ctc;

/// <summary>
/// Casing variants of one surface form, because THE CHECKPOINT IS CASE-SENSITIVE and the DP searches
/// for exactly the id sequence it is handed.
///
/// The fact this exists to survive (measured, <c>CtcCasingTests</c>, and independently in
/// <c>vocabulary-multilingual-research.md §13.7 / F3</c>): parakeet-tdt_ctc-110m is a
/// punctuation-and-capitalisation model. Its head carries **94 uppercase-bearing pieces**, and casing
/// changes the token ids for **13 of 13** English terms probed. So a user who types their term as
/// <c>okta</c> was searching for a sequence the model never emits — zero recall, no exception, no log
/// line, and nothing in the UI to hint at it. German measured the same effect end to end: feeding the
/// lowercase form cost **70 points of recall** (96/96 → 29/96).
///
/// The mitigation is to search several casings and let the DP's own per-term suppression keep the best
/// occurrence — NOT to lower the acceptance threshold, which would trade a tokenization bug for a
/// false-positive one.
///
/// WHICH variants, and why exactly these:
///   * <b>as typed</b> — the user's spelling is a real signal about which words are proper nouns, and
///     for the canonical case it is already the right answer.
///   * <b>all lower</b> — the model lowercases mid-sentence common words, and a term typed in caps
///     (<c>OKTA</c>) is very often emitted as ordinary text.
///   * <b>per-word Title</b> — the emitted form of nearly every proper noun, and the one that rescues
///     the reported bug (<c>okta</c> ⇒ <c>Okta</c>).
///   * <b>all upper</b> — initialisms. <c>wasapi</c> is emitted <c>WASAPI</c>; nothing else recovers it.
///   * <b>sentence case</b> (multi-word only) — <c>Claude code</c>, the very common half-capitalised
///     rendering of a product name. Identical to Title for a single word, so it is not generated there.
///
/// COST is why the list stops here rather than enumerating 2^words combinations: every form is another
/// query in the DP. Deduplication is ordinal and usually collapses 5 candidates to 3, and the measured
/// bound is in <c>CtcCasingTests.English_CasingVariantsCostIsBounded</c>.
/// </summary>
public static class CtcSpotForms
{
    /// <summary>Upper bound on forms generated per surface, BEFORE dedup. The caller's query budget is
    /// this times (1 + alias count), so it is a constant on purpose and not a setting.</summary>
    public const int MaxFormsPerSurface = 5;

    /// <summary>
    /// The variants to search for <paramref name="surface"/>, as-typed FIRST and then in decreasing
    /// likelihood, deduplicated ordinally. Never empty for a non-empty input: the as-typed form is
    /// always present, so this can only add queries, never remove the one we searched before.
    /// </summary>
    public static IReadOnlyList<string> Expand(string? surface)
    {
        if (string.IsNullOrWhiteSpace(surface)) return [];

        var forms = new List<string>(MaxFormsPerSurface);
        void Add(string f)
        {
            if (f.Length == 0) return;
            foreach (string existing in forms) if (string.Equals(existing, f, StringComparison.Ordinal)) return;
            forms.Add(f);
        }

        string s = surface!;
        Add(s);
        Add(s.ToLowerInvariant());
        Add(TitleWords(s));
        Add(s.ToUpperInvariant());
        if (s.Contains(' ')) Add(SentenceCase(s));
        return forms;
    }

    /// <summary>Every word's first letter upper, the rest lower — the emitted shape of a proper noun.</summary>
    private static string TitleWords(string s)
    {
        string[] words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++) words[i] = CapWord(words[i]);
        return string.Join(' ', words);
    }

    /// <summary>First word capitalised, the rest lowercase.</summary>
    private static string SentenceCase(string s)
    {
        string[] words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "";
        words[0] = CapWord(words[0]);
        for (int i = 1; i < words.Length; i++) words[i] = words[i].ToLowerInvariant();
        return string.Join(' ', words);
    }

    // Capitalises the first CASED character rather than blindly words[0]: "(okta)" and "'tis" would
    // otherwise be left untouched, and a term list is exactly where such spellings turn up.
    private static string CapWord(string w)
    {
        string lower = w.ToLowerInvariant();
        for (int i = 0; i < lower.Length; i++)
        {
            if (!char.IsLetter(lower[i])) continue;
            return string.Concat(lower.AsSpan(0, i), char.ToUpperInvariant(lower[i]).ToString(),
                                 lower.AsSpan(i + 1));
        }
        return lower;
    }
}
