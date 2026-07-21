using System.Text.RegularExpressions;

namespace Jot.Text;

/// <summary>
/// Strips hesitation fillers (um/uh/…) from a transcript, then lightly tidies spacing, orphan punctuation,
/// and sentence casing. English is ported 1:1 from the Mac <c>FillerWordCleaner.swift</c>; es/de/fr/it/pt are
/// spec-implemented (see docs/plans/offline-cleanup-windows.md). Any other iso is a strict byte-for-byte no-op.
/// Pure, thread-safe (all state is <c>static readonly</c>), invariant culture.
/// </summary>
public static class FillerWordCleaner
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // English fillers: um/uh/er (+ elongations) plus uhm/erm. \b anchors keep "umbrella" safe.
    private static readonly Regex EnFiller = new(
        @"[ \t]*,?[ \t]*\b(?:um(m+)?|uh(h+)?|er(r+)?|uhm|erm)\b[ \t]*,?[ \t]*", Opts);

    // Per-language hesitation tokens (non-lexical only — never discourse fillers like es "pues", pt "tipo").
    private static readonly Dictionary<string, Regex> MultiFiller = new(StringComparer.Ordinal)
    {
        ["es"] = Build("eh(h+)?", "em(m+)?"),
        ["de"] = Build("äh(h+)?", "ähm(m+)?", "öh(h+)?", "hm(m+)?"),
        ["fr"] = Build("euh(h+)?"),
        ["it"] = Build("ehm(m+)?", "m{3,}"),   // m{3,}: "mm" (millimetres) survives, "mmm" is filler
        ["pt"] = Build("hum(m+)?"),
    };

    // Spanish: a fully-wrapped interjection ("¡eh!", "¿eh?") is deleted whole, before the general strip.
    private static readonly Regex EsWrappedBang = new(@"¡[ \t]*(?:eh(h+)?|em(m+)?)[ \t]*!", Opts);
    private static readonly Regex EsWrappedQ = new(@"¿[ \t]*(?:eh(h+)?|em(m+)?)[ \t]*\?", Opts);

    private static readonly Regex MultiSpace = new(@"[ \t]{2,}", Opts);
    private static readonly Regex WsAfterBreak = new(@"\n\n[ \t]+", Opts);
    private static readonly Regex WsBeforeBreak = new(@"[ \t]+\n\n", Opts);
    private static readonly Regex OrphanPunct = new(@" [,.?!]", Opts);        // English/de/fr/it/pt orphan-drop
    private static readonly Regex EsSpaceBeforeCloser = new(@"[ \t]+([?!])", Opts); // es: keep the closing mark
    private static readonly Regex EsOrphanCommaDot = new(@" [,.]", Opts);           // es: drop only comma/period

    private static readonly char[] LeadingStrip = ['.', ',', '?', '!', ' ', '\t'];
    private static readonly char[] TrailingStrip = [' ', '\t'];

    private static Regex Build(params string[] tokens) =>
        new($@"[ \t]*,?[ \t]*\b(?:{string.Join("|", tokens)})\b[ \t]*,?[ \t]*", Opts);

    /// <param name="isoLang">Base ISO code ("en","es",…). Unknown / not one of the six = byte-for-byte no-op.</param>
    public static string Clean(string text, string isoLang)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (isoLang == "en") return CleanEnglish(text);
        if (MultiFiller.ContainsKey(isoLang)) return CleanMultilingual(text, isoLang);
        return text; // strict no-op for every other language
    }

    // --- English (ported from FillerWordCleaner.swift, verbatim step order) ---
    private static string CleanEnglish(string text)
    {
        string s = EnFiller.Replace(text, " ");          // 1. strip filler → single space
        bool fillerStripped = !ReferenceEquals(s, text) && s != text;
        s = MultiSpace.Replace(s, " ");                  // 2. collapse space/tab runs
        s = WsAfterBreak.Replace(s, "\n\n");             // 2.5 trim whitespace around \n\n (both sides)
        s = WsBeforeBreak.Replace(s, "\n\n");
        s = OrphanPunct.Replace(s, "");                  // 3. drop orphan " ," / " ." / " ?" / " !"
        s = s.TrimStart(LeadingStrip).TrimEnd(TrailingStrip); // 4. strip dangling leading punct + edge ws
        // 5. Recapitalize sentences — ONLY when a filler was actually stripped. The reference
        //    FillerWordCleanerTests.swift asserts "umbrella" → "umbrella " (unchanged casing), which the
        //    reference's unconditional recap contradicts; gating on a real strip satisfies every fixture
        //    (a stripped leading filler still recapitalizes its newly-exposed word, e.g. "Ummmm yes" → "Yes ").
        if (fillerStripped) s = Recapitalize(s);
        return s.Length == 0 ? s : s + " ";              // 6. one trailing space iff non-empty
    }

    // --- Multilingual (es/de/fr/it/pt) — same skeleton, three differences: token set, exposed-word-only
    //     capitalization (no blanket recap), and Spanish inverted punctuation. ---
    private static string CleanMultilingual(string text, string iso)
    {
        string s = text;
        if (iso == "es")                                 // wholesale-delete "¡eh!" / "¿eh?" first
        {
            s = EsWrappedBang.Replace(s, "");
            s = EsWrappedQ.Replace(s, "");
        }

        Regex filler = MultiFiller[iso];

        // Capitalize the word EXPOSED at the start of a sentence when its leading filler is removed. Done on
        // the original string (uppercasing is 1:1, so positions stay stable) so the cap survives the strip.
        var caps = new List<int>();
        foreach (Match m in filler.Matches(s))
        {
            if (!IsSentenceInitial(s, m.Index, iso)) continue;
            int j = m.Index + m.Length;
            while (j < s.Length && !char.IsLetter(s[j])) j++;
            if (j < s.Length) caps.Add(j);
        }
        if (caps.Count > 0)
        {
            char[] arr = s.ToCharArray();
            foreach (int j in caps) arr[j] = char.ToUpperInvariant(arr[j]);
            s = new string(arr);
        }

        s = filler.Replace(s, " ");                      // strip filler → single space
        s = MultiSpace.Replace(s, " ");
        s = WsAfterBreak.Replace(s, "\n\n");
        s = WsBeforeBreak.Replace(s, "\n\n");

        if (iso == "es")
        {
            // Closing-mark-safe orphan-drop: keep a terminal ?/! that closes an inverted ¿/¡ (drop only the
            // space in front of it); still drop orphan comma/period as English does.
            s = EsSpaceBeforeCloser.Replace(s, "$1");
            s = EsOrphanCommaDot.Replace(s, "");
        }
        else
        {
            s = OrphanPunct.Replace(s, "");
        }

        s = s.TrimStart(LeadingStrip).TrimEnd(TrailingStrip);
        // NB: no blanket recap here — the model's own casing stands; only the exposed word (above) changed.
        return s.Length == 0 ? s : s + " ";
    }

    /// <summary>True if the match at <paramref name="index"/> begins a sentence: preceding text (trailing
    /// whitespace skipped) is empty, ends in .!?, or — for es — ends in an opening ¡/¿.</summary>
    private static bool IsSentenceInitial(string s, int index, string iso)
    {
        int k = index - 1;
        while (k >= 0 && (s[k] == ' ' || s[k] == '\t' || s[k] == '\r' || s[k] == '\n')) k--;
        if (k < 0) return true;
        char c = s[k];
        if (c is '.' or '!' or '?') return true;
        return iso == "es" && c is '¡' or '¿';
    }

    // Recapitalize the first alphabetic char of the string and after each .!? — whitespace/newlines let the
    // "capitalize next" flag wait rather than reset. No abbreviation guard (matches the reference impl).
    private static string Recapitalize(string text)
    {
        if (text.Length == 0) return text;
        char[] chars = text.ToCharArray();
        bool capitalizeNext = true;
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (capitalizeNext && char.IsLetter(c))
            {
                chars[i] = char.ToUpperInvariant(c);
                capitalizeNext = false;
            }
            else if (c is '.' or '!' or '?')
            {
                capitalizeNext = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                // whitespace/newline doesn't reset the flag — it waits for the next letter
            }
            else if (char.IsLetter(c) || char.IsDigit(c))
            {
                capitalizeNext = false;
            }
        }
        return new string(chars);
    }
}
