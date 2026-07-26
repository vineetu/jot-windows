using System.Globalization;

namespace Jot.Vocabulary;

/// <summary>
/// Strict, whole-word span resolution against a LIVE transcript — the one splice rule shared by the
/// ask card (ux §4.5) and the review surface (ux §5.4).
///
/// There is deliberately NO nearest-match fallback. iOS shipped one and deleted it after it
/// "routinely resolved — sometimes onto the wrong occurrence": a missing review row is a non-event,
/// a row that edits the wrong word is data loss.
///
/// <c>PublishedLength</c> is deliberately NOT used to size the splice. The shared implementations'
/// strict length+equality guard dropped a "Rama"→"Ramaa" replacement whose original carried a
/// trailing period; we re-derive the span from the live text and trim edge punctuation instead.
/// </summary>
public static class VocabularySplice
{
    /// <summary>A resolved UTF-16 span in the live text.</summary>
    public readonly record struct Span(int Start, int Length);

    // CorrectionKey.Normalize trims these off the OUTER edges of a key; the splice trims the same
    // set off the live token so the two sides can agree. Kept in sync by construction, not by luck.
    private const string EdgePunctuation = ".,!?;:\"'()";

    /// <summary>
    /// Gate anchors are GRAPHEME-CLUSTER counts (Mac/iOS persist them that way and
    /// <see cref="CorrectionProvenance"/> maps them in the same units); .NET strings and WPF's
    /// TextBox index UTF-16. Converting in exactly one place is what stops an emoji in a transcript
    /// from silently shifting every later splice.
    /// </summary>
    public static int Utf16Index(string text, int graphemeOffset)
    {
        if (graphemeOffset <= 0) return 0;
        int seen = 0;
        int i = 0;
        while (i < text.Length && seen < graphemeOffset)
        {
            i += StringInfo.GetNextTextElementLength(text.AsSpan(i));
            seen++;
        }
        return i;
    }

    /// <summary>The inverse of <see cref="Utf16Index"/> — a UTF-16 index back to the grapheme count
    /// the provenance ledger speaks. Needed whenever the UI REPORTS one of its own edits.</summary>
    public static int GraphemeIndex(string text, int utf16Index)
    {
        int seen = 0;
        int i = 0;
        while (i < text.Length && i < utf16Index)
        {
            i += StringInfo.GetNextTextElementLength(text.AsSpan(i));
            seen++;
        }
        return seen;
    }

    /// <summary>Grapheme-cluster length of a string — the unit the ledger's anchors and lengths use.</summary>
    public static int GraphemeLength(string s) => GraphemeIndex(s, s.Length);

    /// <summary>
    /// True when <paramref name="word"/> sits, whole and punctuation-trimmed, exactly at
    /// <paramref name="anchor"/> (a grapheme offset). Multi-word terms are matched across the
    /// matching number of whitespace-delimited tokens.
    /// </summary>
    public static bool TryResolve(string text, int anchor, string? word, out Span span)
    {
        span = default;
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(word)) return false;

        string wanted = CorrectionKey.Normalize(word);
        if (wanted.Length == 0) return false;
        int tokens = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        int start = Utf16Index(text, anchor);
        if (start >= text.Length) return false;

        // The anchor must be AT a word start. Anything else is a stale offset pointing into the
        // middle of a word, and resolving it would be the wrong-occurrence bug.
        if (start > 0 && !char.IsWhiteSpace(text[start - 1])) return false;
        if (char.IsWhiteSpace(text[start])) return false;

        int end = start;
        for (int t = 0; t < tokens; t++)
        {
            if (t > 0)
            {
                int ws = end;
                while (ws < text.Length && char.IsWhiteSpace(text[ws])) ws++;
                if (ws == end) return false;   // no separator — fewer tokens than the term needs
                end = ws;
            }
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        }

        int s = start;
        int e = end;
        while (s < e && EdgePunctuation.Contains(text[s])) s++;
        while (e > s && EdgePunctuation.Contains(text[e - 1])) e--;
        if (e <= s) return false;

        if (CorrectionKey.Normalize(text[s..e]) != wanted) return false;
        span = new Span(s, e - s);
        return true;
    }

    /// <summary>
    /// The effective replace range for a span the USER selected in a TextBox.
    ///
    /// TRAP — do not "simplify" this back to using the raw selection offsets. WPF's double-click
    /// word selection includes the TRAILING WHITESPACE ("Venith " when the owner double-clicks
    /// "Venith"), and <see cref="VocabularyStore.SanitizeTerm"/> trims the term — so splicing the
    /// bare term over the raw range silently ate the space between words and published
    /// "My name is VineetSriram." while the stored vocabulary looked perfectly clean.
    ///
    /// Whitespace on the EDGES of a selection is never what the owner picked, so it is never part of
    /// what we replace; everything outside the trimmed core — spaces, newlines, punctuation — is
    /// left exactly as it was. Returns false when nothing but whitespace was selected.
    /// </summary>
    public static bool TrySelectionSpan(string text, int start, int length, out Span span)
    {
        span = default;
        if (text is null || start < 0 || length <= 0 || start + length > text.Length) return false;

        int s = start;
        int e = start + length;
        while (s < e && char.IsWhiteSpace(text[s])) s++;
        while (e > s && char.IsWhiteSpace(text[e - 1])) e--;
        if (e <= s) return false;

        span = new Span(s, e - s);
        return true;
    }

    /// <summary>Replace a resolved span. Separate from <see cref="TryResolve"/> so a caller can
    /// resolve, decide, and only then mutate — the ask card resolves every card before it splices
    /// any of them.</summary>
    public static string Replace(string text, Span span, string replacement) =>
        text.Remove(span.Start, span.Length).Insert(span.Start, replacement);

    /// <summary>The surrounding words, for the context line both review rows and ask cards lead
    /// with. iOS puts it FIRST "so when several rows share a word, the owner can tell WHICH
    /// occurrence each row is about" — doubly true here, where placement is proportional and can
    /// land on the wrong occurrence.</summary>
    public readonly record struct Context(string Before, string Word, string After);

    public static Context ContextAround(string text, Span span, int radius = 24)
    {
        int from = Math.Max(0, span.Start - radius);
        int to = Math.Min(text.Length, span.Start + span.Length + radius);
        string before = text[from..span.Start].ReplaceLineEndings(" ");
        string after = text[(span.Start + span.Length)..to].ReplaceLineEndings(" ");
        return new Context(
            (from > 0 ? "…" : "") + before,
            text.Substring(span.Start, span.Length),
            after + (to < text.Length ? "…" : ""));
    }
}
