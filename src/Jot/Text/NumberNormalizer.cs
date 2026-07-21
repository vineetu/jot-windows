using System.Globalization;

namespace Jot.Text;

/// <summary>
/// Post-transcription spoken-number → digit normalizer ("fifteen pages" → "15 pages", "fifty dollars" → "$50").
/// Byte-exact 1:1 port of the Mac <c>NumberNormalizer.swift</c> (parity gate: NumberNormalizerTests, 51 fixtures).
/// Pure, regex-free (hand-written char/token walker), invariant culture everywhere. No internal language gate —
/// the caller applies English-only. Runs AFTER filler cleaning so filler can never split a spelled cardinal.
/// </summary>
/// <remarks>
/// Deterministic AP-style-ish rules: convert spelled numbers to digits only when context calls for digits;
/// leave idioms / bare ones-ordinals / lone single-digit cardinals alone; bail out wholesale on anything that
/// looks like a dictated phone number. Any number-word run containing million/billion/trillion is left as words
/// (top-priority pass-through that overrides money/percent/cardinal rules).
/// </remarks>
public static class NumberNormalizer
{
    // === Public entry point ===

    /// <summary>
    /// Walk <paramref name="text"/> token-by-token, find maximal spelled-cardinal runs, and rewrite each by
    /// the first matching context rule. Whitespace, punctuation, and paragraph breaks (\n\n) are preserved.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var tokens = Tokenize(text);
        if (tokens.Count == 0) return text;

        // Phone-shape guard: ≥7 consecutive spelled single-digit words anywhere → bail out entirely.
        if (ContainsPhoneShape(tokens)) return text;

        var outp = new List<Token>(tokens.Count);

        int i = 0;
        while (i < tokens.Count)
        {
            Token tok = tokens[i];

            // Top-priority pass-through: any word that IS or CONTAINS million/billion/trillion is emitted
            // verbatim (catches bare "million", the "300 million" tail, hyphenated compounds).
            if (tok.Kind == Kind.Word && IsLargeScaleToken(tok.Core))
            {
                outp.Add(tok);
                i += 1;
                continue;
            }

            // Tens-ordinal combiner runs BEFORE the cardinal branch so ordinal forms the cardinal branch skips
            // ("twenty-third", "twentieth", split "twenty third") don't leak "20 third".
            if (tok.Kind == Kind.Word)
            {
                var combined = ParseTensOrdinal(tokens, i);
                if (combined is (var comboText, var comboEnd))
                {
                    int lastIdx = Math.Max(0, Math.Min(comboEnd - 1, tokens.Count - 1));
                    outp.Add(new Token(Kind.Word, comboText, comboText, tokens[lastIdx].Trailing));
                    i = comboEnd;
                    continue;
                }
            }

            // Only words open a cardinal run; ordinals stop the walker immediately.
            if (tok.Kind == Kind.Word && !IsOrdinal(tok.Core))
            {
                string[] dash = tok.Core.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
                string firstPiece = dash.Length > 0 ? dash[0] : "";
                bool opensCardinal = TryCardinal(firstPiece, out _) || IsSplittableCardinalWord(tok.Core);

                if (opensCardinal)
                {
                    string? prevWordLower = PreviousWord(outp)?.ToLowerInvariant();

                    // Year-shape FIRST (only in year context). "nineteen ninety-eight" / "twenty twenty-six" are
                    // not valid plain cardinals, so recognize them up front and emit the year directly.
                    if (IsYearContext(prevWordLower))
                    {
                        var year = ParseYearShape(tokens, i);
                        if (year is (var yearValue, var yearEnd))
                        {
                            string yTrailing = tokens[yearEnd - 1].Trailing;
                            string yText = yearValue.ToString(CultureInfo.InvariantCulture);
                            outp.Add(new Token(Kind.Word, yText, yText, yTrailing));
                            i = yearEnd;
                            continue;
                        }
                    }

                    // Address digit-run (only when prev is an address word). "four oh seven" → literal "407";
                    // falls back to the standard cardinal parser otherwise ("two hundred and three" → 203).
                    if (prevWordLower is not null && AddressContextWords.Contains(prevWordLower))
                    {
                        var addr = ParseAddressDigitRun(tokens, i);
                        if (addr is (var addrDigits, var addrEnd))
                        {
                            string aTrailing = tokens[addrEnd - 1].Trailing;
                            outp.Add(new Token(Kind.Word, addrDigits, addrDigits, aTrailing));
                            i = addrEnd;
                            continue;
                        }
                    }

                    var parsed = ParseCardinalSequence(tokens, i);
                    if (parsed is (var sequence, var value, var consumed))
                    {
                        int endExclusive = i + consumed;

                        // Top-priority pass-through: a run containing million/billion/trillion is emitted
                        // verbatim, overriding money / percent / cardinal rules.
                        if (SequenceContainsLargeScale(sequence))
                        {
                            for (int k = i; k < endExclusive; k++) outp.Add(tokens[k]);
                            i = endExclusive;
                            continue;
                        }

                        var result = RewriteSequence(sequence, value, i, endExclusive, tokens, prevWordLower, outp);
                        if (result is (var resText, var consumedUpTo))
                        {
                            int lastIdx = Math.Max(0, Math.Min(consumedUpTo - 1, tokens.Count - 1));
                            string trailing = tokens[lastIdx].Trailing;
                            outp.Add(new Token(Kind.Word, resText, resText, trailing));
                            i = consumedUpTo;
                            continue;
                        }

                        // Idiom / skip: emit the original tokens verbatim.
                        for (int k = i; k < endExclusive; k++) outp.Add(tokens[k]);
                        i = endExclusive;
                        continue;
                    }
                }
            }

            outp.Add(tok);
            i += 1;
        }

        return Reassemble(outp);
    }

    // === Token model ===

    private enum Kind { Word, Whitespace, Other }

    /// <summary>Mutable-by-construction token. <c>Raw</c> is the exact original substring (whitespace lives in
    /// its own token); <c>Core</c> strips trailing punct for clean matching; <c>Trailing</c> is the stripped
    /// punct, reattached on reassembly. Synthetic emit tokens reassign Raw/Core/Trailing.</summary>
    private sealed class Token
    {
        public Kind Kind;
        public string Raw;
        public string Core;
        public string Trailing;
        public bool HasTrailingPunct => Trailing.Length != 0;

        public Token(Kind kind, string raw, string core, string trailing)
        {
            Kind = kind;
            Raw = raw;
            Core = core;
            Trailing = trailing;
        }
    }

    // Trailing-edge punctuation we strip into Token.Trailing for clean matching. Includes curly ”/’ and ».
    private static readonly HashSet<char> TrailingPunct = new()
    {
        ',', '.', '!', '?', ';', ':', '"', '\'', ')', ']', '}', '”', '’', '»'
    };

    // Pure-text tokenizer: split on whitespace runs but keep the EXACT whitespace (so "\n\n" survives) by
    // emitting a whitespace token between words. NOTE (Swift divergence): Swift walks graphemes; .NET walks
    // UTF-16 chars. Fine for English transcripts.
    private static List<Token> Tokenize(string text)
    {
        var outp = new List<Token>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                int start = i;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i += 1;
                outp.Add(new Token(Kind.Whitespace, text[start..i], "", ""));
                continue;
            }

            int wordStart = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i += 1;
            string raw = text[wordStart..i];
            string trailing = "";
            while (raw.Length > 0 && TrailingPunct.Contains(raw[^1]))
            {
                trailing = raw[^1] + trailing;   // prepend to keep original order
                raw = raw[..^1];
            }

            if (raw.Length == 0)
                outp.Add(new Token(Kind.Other, trailing, trailing, ""));   // pure punctuation
            else
                outp.Add(new Token(IsWordish(raw) ? Kind.Word : Kind.Other, raw, raw, trailing));
        }
        return outp;
    }

    // True if s is hyphen/letter/apostrophe-only — looks like a single English word ("twenty-five", "o'clock").
    private static bool IsWordish(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!(char.IsLetter(c) || c == '-' || c == '\'' || c == '’')) return false;
        return true;
    }

    // Whitespace emits Raw verbatim; word/other emit Core + Trailing.
    private static string Reassemble(List<Token> tokens)
    {
        var sb = new System.Text.StringBuilder();
        foreach (Token t in tokens)
        {
            if (t.Kind == Kind.Whitespace) sb.Append(t.Raw);
            else { sb.Append(t.Core); sb.Append(t.Trailing); }
        }
        return sb.ToString();
    }

    // === Vocabulary ===

    // "oh" = 0 only inside address context (the parser accepts it anywhere; the context checker rejects "oh"
    // sequences outside address mode). billion/trillion are deliberately NOT here — they route to pass-through.
    private static readonly Dictionary<string, int> CardinalWords = new(StringComparer.Ordinal)
    {
        ["zero"] = 0, ["oh"] = 0,
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
        ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19,
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
        ["hundred"] = 100, ["thousand"] = 1_000, ["million"] = 1_000_000
    };

    private static bool TryCardinal(string key, out int value) => CardinalWords.TryGetValue(key, out value);

    private static readonly HashSet<string> SingleDigitWords = new(StringComparer.Ordinal)
    {
        "zero", "oh", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"
    };

    private static readonly HashSet<string> OrdinalWords = new(StringComparer.Ordinal)
    {
        "first", "second", "third", "fourth", "fifth", "sixth",
        "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth",
        "thirteenth", "fourteenth", "fifteenth", "sixteenth",
        "seventeenth", "eighteenth", "nineteenth", "twentieth",
        "thirtieth", "fortieth", "fiftieth", "sixtieth", "seventieth",
        "eightieth", "ninetieth", "hundredth", "thousandth", "millionth"
    };

    private static readonly HashSet<string> AddressContextWords = new(StringComparer.Ordinal)
    {
        "apartment", "apt", "room", "suite", "floor", "building", "unit", "office"
    };

    private static readonly HashSet<string> YearContextWords = new(StringComparer.Ordinal)
    {
        "in", "since", "back", "year", "from", "until", "before", "after"
    };

    private static readonly HashSet<string> MonthWords = new(StringComparer.Ordinal)
    {
        "january", "february", "march", "april", "may", "june", "july",
        "august", "september", "october", "november", "december"
    };

    private static readonly HashSet<string> TimePrecedingWords = new(StringComparer.Ordinal)
    {
        "at", "by", "around", "about"
    };

    private const int PhoneShapeThreshold = 7;

    private static readonly HashSet<string> LargeScaleWords = new(StringComparer.Ordinal)
    {
        "million", "billion", "trillion"
    };

    // === Detection helpers ===

    // True if the token core (lowercased, hyphen-split) contains any of million/billion/trillion.
    private static bool IsLargeScaleToken(string s)
    {
        string lower = s.ToLowerInvariant();
        if (LargeScaleWords.Contains(lower)) return true;
        foreach (string part in lower.Split('-', StringSplitOptions.RemoveEmptyEntries))
            if (LargeScaleWords.Contains(part)) return true;
        return false;
    }

    private static bool SequenceContainsLargeScale(List<string> sequence)
    {
        foreach (string p in sequence)
            if (LargeScaleWords.Contains(p.ToLowerInvariant())) return true;
        return false;
    }

    // Word-form ordinal: known vocab, hyphen compound ending in a known ordinal, or digit-form ("21st").
    private static bool IsOrdinal(string s)
    {
        string lower = s.ToLowerInvariant();
        if (OrdinalWords.Contains(lower)) return true;
        int dash = lower.LastIndexOf('-');
        if (dash >= 0)
        {
            string tail = lower[(dash + 1)..];
            if (OrdinalWords.Contains(tail)) return true;
        }
        foreach (string sfx in new[] { "st", "nd", "rd", "th" })
        {
            if (lower.EndsWith(sfx, StringComparison.Ordinal) && lower.Length > sfx.Length)
            {
                string head = lower[..^sfx.Length];
                bool allDigits = head.Length > 0;
                foreach (char c in head) if (!char.IsDigit(c)) { allDigits = false; break; }
                if (allDigits) return true;
            }
        }
        return false;
    }

    // True if s is a cardinal word or a hyphen compound whose every piece is a cardinal word. Cheap gate.
    private static bool IsSplittableCardinalWord(string s)
    {
        string[] parts = s.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (string p in parts) if (!TryCardinal(p, out _)) return false;
        return true;
    }

    private static bool ContainsPhoneShape(List<Token> tokens)
    {
        int run = 0;
        foreach (Token t in tokens)
        {
            if (t.Kind != Kind.Word) continue;
            if (SingleDigitWords.Contains(t.Core.ToLowerInvariant()))
            {
                run += 1;
                if (run >= PhoneShapeThreshold) return true;
            }
            else run = 0;
        }
        return false;
    }

    // Core of the last word token already in out (skipping whitespace/other).
    private static string? PreviousWord(List<Token> outp)
    {
        for (int j = outp.Count - 1; j >= 0; j--)
            if (outp[j].Kind == Kind.Word) return outp[j].Core;
        return null;
    }

    // Next word-or-other token AFTER index start.
    private static (int index, Token token)? NextWord(int start, List<Token> tokens)
    {
        for (int j = start + 1; j < tokens.Count; j++)
            if (tokens[j].Kind == Kind.Word || tokens[j].Kind == Kind.Other) return (j, tokens[j]);
        return null;
    }

    // Next non-whitespace token AFTER index start.
    private static (int index, Token token)? NextNonWhitespace(int start, List<Token> tokens)
    {
        for (int j = start + 1; j < tokens.Count; j++)
            if (tokens[j].Kind != Kind.Whitespace) return (j, tokens[j]);
        return null;
    }

    // === Year-shape pre-parser ===

    // Shapes: "nineteen NN"→1900+NN, "twenty NN"→2000+NN, "two thousand [and] NN"→2000+NN, bare "two thousand"→2000.
    // Only called when prev word is a year-context trigger.
    private static (int value, int endExclusive)? ParseYearShape(List<Token> tokens, int start)
    {
        if (start >= tokens.Count || tokens[start].Kind != Kind.Word) return null;
        string firstCore = tokens[start].Core.ToLowerInvariant();

        if (firstCore == "nineteen" || firstCore == "twenty")
        {
            int baseVal = firstCore == "nineteen" ? 1900 : 2000;
            if (tokens[start].HasTrailingPunct) return null;   // "in twenty," can't form a year
            var nn = ReadTwoDigitWord(tokens, start + 1);
            if (nn is (var nnValue, var nnEnd) && nnValue >= 0 && nnValue <= 99)
                return (baseVal + nnValue, nnEnd);
            return null;
        }

        if (firstCore == "two")
        {
            if (tokens[start].HasTrailingPunct) return null;
            var nextOne = NextNonWhitespace(start, tokens);
            if (nextOne is not (var thousandIdx, _) || tokens[thousandIdx].Core.ToLowerInvariant() != "thousand")
                return null;
            if (tokens[thousandIdx].HasTrailingPunct) return (2000, thousandIdx + 1);   // bare "two thousand"

            int nnStart = thousandIdx + 1;
            var afterThou = NextNonWhitespace(thousandIdx, tokens);
            if (afterThou is (var andIdx, _)
                && tokens[andIdx].Core.ToLowerInvariant() == "and"
                && !tokens[andIdx].HasTrailingPunct)
                nnStart = andIdx + 1;

            var nn2 = ReadTwoDigitWord(tokens, nnStart);
            if (nn2 is (var nn2Value, var nn2End) && nn2Value >= 0 && nn2Value <= 99)
                return (2000 + nn2Value, nn2End);
            return (2000, thousandIdx + 1);   // "two thousand" with no NN tail
        }

        return null;
    }

    // Read a two-digit word: teen/ones/tens, hyphenated tens+ones ("twenty-six"), or space tens+ones.
    private static (int value, int endExclusive)? ReadTwoDigitWord(List<Token> tokens, int start)
    {
        int idx = start;
        while (idx < tokens.Count && tokens[idx].Kind == Kind.Whitespace) idx += 1;
        if (idx >= tokens.Count || tokens[idx].Kind != Kind.Word) return null;

        string core = tokens[idx].Core.ToLowerInvariant();
        string[] parts = core.Split('-', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 2
            && TryCardinal(parts[0], out int tens) && tens % 10 == 0 && tens >= 20 && tens <= 90
            && TryCardinal(parts[1], out int ones) && ones >= 1 && ones <= 9)
            return (tens + ones, idx + 1);

        if (parts.Length == 1 && TryCardinal(core, out int v))
        {
            if (v >= 0 && v <= 19) return (v, idx + 1);   // teens/ones don't extend
            if (v % 10 == 0 && v >= 20 && v <= 90)
            {
                if (!tokens[idx].HasTrailingPunct)
                {
                    var nextOne = NextNonWhitespace(idx, tokens);
                    if (nextOne is (var nextIdx, _))
                    {
                        string nc = tokens[nextIdx].Core.ToLowerInvariant();
                        if (TryCardinal(nc, out int ones2) && ones2 >= 1 && ones2 <= 9)
                            return (v + ones2, nextIdx + 1);
                    }
                }
                return (v, idx + 1);
            }
        }
        return null;
    }

    // Address-mode: greedily consume ≥2 single-digit words ("four oh seven") or a run containing "oh", and
    // concatenate their digits. Returns null if the run is a lone single digit (let the cardinal parser try).
    private static (string digits, int endExclusive)? ParseAddressDigitRun(List<Token> tokens, int start)
    {
        if (start >= tokens.Count || tokens[start].Kind != Kind.Word) return null;
        string firstCore = tokens[start].Core.ToLowerInvariant();
        if (firstCore.Contains('-')) return null;   // "four-oh" isn't a real input
        if (!TryCardinal(firstCore, out int firstVal) || firstVal < 0 || firstVal > 9) return null;

        string digits = firstVal.ToString(CultureInfo.InvariantCulture);
        int lastIdx = start;
        bool hasOh = firstCore == "oh";

        if (tokens[start].HasTrailingPunct) return null;   // single digit — no run

        int i = start + 1;
        while (i < tokens.Count)
        {
            if (tokens[i].Kind != Kind.Whitespace) break;
            if (i + 1 >= tokens.Count || tokens[i + 1].Kind != Kind.Word) break;
            Token nextTok = tokens[i + 1];
            string nextCore = nextTok.Core.ToLowerInvariant();
            if (nextCore.Contains('-')) break;
            if (!TryCardinal(nextCore, out int v) || v < 0 || v > 9) break;
            digits += v.ToString(CultureInfo.InvariantCulture);
            lastIdx = i + 1;
            if (nextCore == "oh") hasOh = true;
            if (nextTok.HasTrailingPunct) break;
            i += 2;
        }

        int consumed = lastIdx - start + 1;
        if (hasOh || consumed >= 2) return (digits, lastIdx + 1);
        return null;
    }

    // === Tens-ordinal combiner ===

    // Ones-range ordinals → 1..9. Standalone ones-ordinals ("first".."ninth") are NOT rewritten; they only
    // participate as the ones-piece of a tens+ones ordinal.
    private static readonly Dictionary<string, int> OnesOrdinalValues = new(StringComparer.Ordinal)
    {
        ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
        ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9
    };

    // Standalone tens-ordinals → 20..90. Emit form is always "<value>th".
    private static readonly Dictionary<string, int> TensOrdinalValues = new(StringComparer.Ordinal)
    {
        ["twentieth"] = 20, ["thirtieth"] = 30, ["fortieth"] = 40, ["fiftieth"] = 50,
        ["sixtieth"] = 60, ["seventieth"] = 70, ["eightieth"] = 80, ["ninetieth"] = 90
    };

    // Ordinal suffix from the units digit: 1→st, 2→nd, 3→rd, else→th. Only called with 20..99 (units drives it).
    private static string OrdinalSuffix(int units) => units switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th"
    };

    // Shapes: hyphen "twenty-third"→"23rd", standalone "twentieth"→"20th", split "twenty third"→"23rd".
    // Does NOT fire for hyphenated standalone tens-ordinals (none exist) or bare "first".."ninth".
    private static (string text, int endExclusive)? ParseTensOrdinal(List<Token> tokens, int start)
    {
        if (start >= tokens.Count || tokens[start].Kind != Kind.Word) return null;
        string core = tokens[start].Core.ToLowerInvariant();

        if (core.Contains('-'))
        {
            string[] parts = core.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2
                && TryCardinal(parts[0], out int tensVal) && tensVal % 10 == 0 && tensVal >= 20 && tensVal <= 90
                && OnesOrdinalValues.TryGetValue(parts[1], out int onesVal))
            {
                int combined = tensVal + onesVal;
                return ($"{combined}{OrdinalSuffix(onesVal)}", start + 1);
            }
            // Hyphenated but not a tens+ones ordinal — let the main loop handle it.
            return null;
        }

        if (TensOrdinalValues.TryGetValue(core, out int standaloneVal))
            return ($"{standaloneVal}th", start + 1);

        // Space-separated "twenty third": plain tens cardinal (no trailing punct) + a single ones-ordinal.
        if (!TryCardinal(core, out int tensC) || tensC % 10 != 0 || tensC < 20 || tensC > 90
            || tokens[start].HasTrailingPunct)
            return null;
        var next = NextNonWhitespace(start, tokens);
        if (next is not (var nextIdx, _) || tokens[nextIdx].Kind != Kind.Word) return null;
        string nextCore = tokens[nextIdx].Core.ToLowerInvariant();
        if (!OnesOrdinalValues.TryGetValue(nextCore, out int onesC)) return null;
        int combined2 = tensC + onesC;
        return ($"{combined2}{OrdinalSuffix(onesC)}", nextIdx + 1);
    }

    // === Cardinal parser ===

    // Parse a maximal spelled-cardinal run from token start. Grammar: hundred after 1–19; thousand/million after
    // any sub-scale value; after a scale any ≥1; after tens only ones 1–9; "and" only after a scale word;
    // trailing punct ends the run; interior whitespace is consumed. Returns lowercase pieces, value, and the
    // token count consumed (incl. interior whitespace + "and").
    private static (List<string> sequence, long value, int consumed)? ParseCardinalSequence(List<Token> tokens, int start)
    {
        if (start >= tokens.Count || tokens[start].Kind != Kind.Word) return null;
        string[]? firstParts = SplitCardinalPieces(tokens[start].Core);
        if (firstParts is null || firstParts.Length == 0 || firstParts[0] == "and") return null;

        var pieces = new List<string>(firstParts);
        int lastConsumedIdx = start;

        // Validate the internal hyphen compound of the first token ("twenty-five" = valid tens+ones).
        for (int k = 0; k < firstParts.Length - 1; k++)
            if (!CanExtend(firstParts[k], firstParts[k + 1])) return null;

        // First token has trailing punct → stop immediately.
        if (tokens[start].HasTrailingPunct)
        {
            long? v0 = ComputeValue(pieces);
            if (v0 is null) return null;
            return (pieces, v0.Value, 1);
        }

        int i = start + 1;
        while (i < tokens.Count)
        {
            if (tokens[i].Kind != Kind.Whitespace) break;
            if (i + 1 >= tokens.Count || tokens[i + 1].Kind != Kind.Word) break;
            Token nextTok = tokens[i + 1];
            string nextCore = nextTok.Core.ToLowerInvariant();

            // "and" connector — only valid after a scale word ("two hundred AND thirty").
            if (nextCore == "and")
            {
                string last = pieces[^1];
                if (!TryCardinal(last, out int lastVal) || !(lastVal == 100 || lastVal == 1_000 || lastVal == 1_000_000))
                    break;
                if (nextTok.HasTrailingPunct) break;
                var after = NextNonWhitespace(i + 1, tokens);
                if (after is not (var afterIdx, _)
                    || tokens[afterIdx].Kind != Kind.Word
                    || IsOrdinal(tokens[afterIdx].Core))
                    break;
                string[]? afterParts = SplitCardinalPieces(tokens[afterIdx].Core);
                if (afterParts is null) break;
                if (!CanExtend(last, afterParts[0])) break;
                bool validInternalA = true;
                for (int k = 0; k < afterParts.Length - 1; k++)
                    if (!CanExtend(afterParts[k], afterParts[k + 1])) { validInternalA = false; break; }
                if (!validInternalA) break;
                pieces.Add("and");
                pieces.AddRange(afterParts);
                lastConsumedIdx = afterIdx;
                if (tokens[afterIdx].HasTrailingPunct) break;
                i = afterIdx + 1;
                continue;
            }

            // Ordinary cardinal-piece extension.
            if (IsOrdinal(nextTok.Core)) break;
            string[]? nextParts = SplitCardinalPieces(nextTok.Core);
            if (nextParts is null) break;
            string lastP = pieces[^1];
            if (!CanExtend(lastP, nextParts[0])) break;
            bool validInternal = true;
            for (int k = 0; k < nextParts.Length - 1; k++)
                if (!CanExtend(nextParts[k], nextParts[k + 1])) { validInternal = false; break; }
            if (!validInternal) break;

            pieces.AddRange(nextParts);
            lastConsumedIdx = i + 1;
            if (nextTok.HasTrailingPunct) break;
            i += 2;
        }

        long? value = ComputeValue(pieces);
        if (value is null) return null;
        return (pieces, value.Value, lastConsumedIdx - start + 1);
    }

    // Can `currentLast` be followed by `nextFirst` in valid number grammar?
    private static bool CanExtend(string currentLast, string nextFirst)
    {
        if (!TryCardinal(currentLast, out int lastVal) || !TryCardinal(nextFirst, out int nextVal)) return false;
        if (nextVal == 100) return lastVal >= 1 && lastVal <= 19;               // hundred after ones/teens
        if (nextVal == 1_000 || nextVal == 1_000_000) return lastVal >= 1 && lastVal < nextVal; // scale after sub-scale
        if (lastVal == 100 || lastVal == 1_000 || lastVal == 1_000_000) return nextVal >= 1;    // after scale, any ≥1
        if (lastVal % 10 == 0 && lastVal >= 20 && lastVal <= 90) return nextVal >= 1 && nextVal <= 9; // tens + ones
        return false;   // after ones/teens only a scale may follow (handled above)
    }

    // Split a core on hyphen, requiring EVERY piece be a cardinal word. Null if any piece isn't.
    private static string[]? SplitCardinalPieces(string s)
    {
        string[] parts = s.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        foreach (string p in parts) if (!TryCardinal(p, out _)) return null;
        return parts;
    }

    // Compute numeric value from cardinal pieces (hundreds/thousands/millions + "and"). long, per portspec.
    private static long? ComputeValue(List<string> pieces)
    {
        var filtered = new List<string>();
        foreach (string p in pieces) if (p != "and") filtered.Add(p);
        if (filtered.Count == 0) return null;

        long total = 0;
        long current = 0;
        foreach (string p in filtered)
        {
            if (!TryCardinal(p, out int v)) return null;
            if (v == 100)
            {
                if (current == 0) current = 1;
                current *= 100;
            }
            else if (v == 1_000 || v == 1_000_000)
            {
                if (current == 0) current = 1;
                total += current * v;
                current = 0;
            }
            else current += v;
        }
        total += current;
        return total;
    }

    // === Context-aware rewriter ===

    // Apply the first matching context rule → (emit text, new advance position). Null = idiom/skip (caller emits
    // original tokens verbatim). `outp` is mutated so article-drop rules can remove a preceding "a".
    private static (string text, int consumedUpTo)? RewriteSequence(
        List<string> sequence, long value, int startIndex, int endExclusive,
        List<Token> tokens, string? prevWordLower, List<Token> outp)
    {
        bool containsOh = sequence.Contains("oh");

        // Bare "a"/"one" article before a one-piece scale — used by money/percent/idiom overrides to drop it.
        int? articlePrefixIdx = ArticleIndexBeforeIdiom(startIndex, tokens, sequence);

        // === Rule 1: Money ===
        var unit = NextWord(endExclusive - 1, tokens);
        if (value >= 1 && unit is (var unitIdx, var unitTok))
        {
            string unitTrimmed = unitTok.Core.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ':');

            if (unitTrimmed is "dollars" or "dollar" or "bucks" or "buck")
            {
                // "dollars and <m> cents" extension.
                var andTok = NextWord(unitIdx, tokens);
                if (andTok is (var andIdx, var andT) && andT.Core.ToLowerInvariant() == "and")
                {
                    var cents = NextCardinal(andIdx, tokens);
                    if (cents is (_, var centsValue, var centsEnd))
                    {
                        var centsUnit = NextWord(centsEnd - 1, tokens);
                        if (centsUnit is (var centsUnitIdx, var centsUnitTok)
                            && centsUnitTok.Core.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ':') is "cents" or "cent")
                        {
                            DropArticleIfPresent(articlePrefixIdx, outp);
                            return ($"${FormatThousands(value)}.{centsValue.ToString("D2", CultureInfo.InvariantCulture)}",
                                    centsUnitIdx + 1);
                        }
                    }
                }
                DropArticleIfPresent(articlePrefixIdx, outp);
                return ($"${FormatThousands(value)}", unitIdx + 1);
            }

            if (unitTrimmed is "cents" or "cent")
                return ($"{value}¢", unitIdx + 1);

            // === Rule 2: Percent ===
            if (unitTrimmed == "percent")
            {
                DropArticleIfPresent(articlePrefixIdx, outp);
                return ($"{value}%", unitIdx + 1);
            }
        }

        // === Rule 3: Year-shape fallback === ("two thousand twenty-six" is BOTH a valid cardinal and a year).
        if (IsYearContext(prevWordLower) && value >= 1000 && value <= 2999)
        {
            var filtered = new List<string>();
            foreach (string p in sequence) if (p != "and") filtered.Add(p);
            if (filtered.Count >= 2 && filtered[0] == "two" && filtered[1] == "thousand" && !containsOh)
                return (value.ToString(CultureInfo.InvariantCulture), endExclusive);
        }

        // === Rule 4: Time-of-day ===
        if (value >= 1 && value <= 12 && sequence.Count == 1 && !containsOh)
        {
            // (a) Compound time.
            var next = NextWord(endExclusive - 1, tokens);
            if (next is (var nextIdx, var nextTok))
            {
                string nextCore = nextTok.Core.ToLowerInvariant();
                if (nextCore is "thirty" or "fifteen" or "forty-five")
                {
                    int minutes = nextCore == "thirty" ? 30 : nextCore == "fifteen" ? 15 : 45;
                    string baseTxt = $"{value}:{minutes.ToString("D2", CultureInfo.InvariantCulture)}";
                    var meridiem = TrailingMeridiem(nextIdx, tokens);
                    if (meridiem is (var mText, var mConsumed)) return ($"{baseTxt} {mText}", mConsumed);
                    return (baseTxt, nextIdx + 1);
                }
                if (nextCore is "o'clock" or "o’clock")
                    return ($"{value} o'clock", nextIdx + 1);
            }

            // (b) Sub-10 override.
            if (value >= 1 && value <= 9 && prevWordLower is not null && TimePrecedingWords.Contains(prevWordLower))
            {
                var meridiem = TrailingMeridiem(endExclusive - 1, tokens);
                if (meridiem is (var mText, var mConsumed)) return ($"{value} {mText}", mConsumed);
                return ($"{value}", endExclusive);
            }
        }

        // === Rule 5: Address / room number ===
        if (prevWordLower is not null && AddressContextWords.Contains(prevWordLower))
        {
            string? addr = AddressDigitString(sequence);
            if (addr is not null) return (addr, endExclusive);
            return null;
        }

        // === Rule 8: Idiom exception (bare standalone) ===
        if (IsStandaloneIdiom(startIndex, tokens, value)) return null;

        // === Rule 6: Cardinals ≥ 10 ===
        if (value >= 10)
        {
            if (containsOh) return null;
            DropArticleIfPresent(articlePrefixIdx, outp);   // drop leading "a" when emitting digits
            return (FormatThousands(value), endExclusive);
        }

        // === Rule 7: Cardinals 1–9 stay as words ===
        return null;
    }

    // Bare "hundred"/"thousand"/"million" preceded by "a", or "one"+scale → index of the "a"/"one" to drop.
    private static int? ArticleIndexBeforeIdiom(int startIndex, List<Token> tokens, List<string> sequence)
    {
        string startCore = tokens[startIndex].Core.ToLowerInvariant();
        if (startCore is "hundred" or "thousand" or "million")
        {
            for (int j = startIndex - 1; j >= 0; j--)
            {
                if (tokens[j].Kind == Kind.Whitespace) continue;
                return tokens[j].Core.ToLowerInvariant() == "a" ? j : (int?)null;   // first non-ws token decides
            }
        }
        if (startCore == "one" && sequence.Count == 2
            && TryCardinal(sequence[1], out int scaleVal)
            && (scaleVal == 100 || scaleVal == 1_000 || scaleVal == 1_000_000))
            return startIndex;   // "one" IS startIndex; not yet emitted, so drop is a no-op but keeps parity
        return null;
    }

    // Remove the article at `index` from outp iff the tail non-ws word is "a" (and its trailing whitespace).
    private static void DropArticleIfPresent(int? index, List<Token> outp)
    {
        if (index is null || index.Value < 0) return;
        int i = outp.Count - 1;
        while (i >= 0 && outp[i].Kind == Kind.Whitespace) i -= 1;
        if (i < 0 || outp[i].Kind != Kind.Word) return;
        if (outp[i].Core.ToLowerInvariant() != "a") return;
        outp.RemoveRange(i, outp.Count - i);
    }

    // Standalone idiom "a hundred"/"one hundred"/"a thousand"/… — money/percent/time/address/year ran first.
    private static bool IsStandaloneIdiom(int startIndex, List<Token> tokens, long value)
    {
        if (!(value == 100 || value == 1_000 || value == 1_000_000)) return false;
        string startCore = tokens[startIndex].Core.ToLowerInvariant();
        if (startCore is "hundred" or "thousand" or "million")
        {
            for (int j = startIndex - 1; j >= 0; j--)
            {
                if (tokens[j].Kind == Kind.Whitespace) continue;
                return tokens[j].Core.ToLowerInvariant() == "a";
            }
            return false;
        }
        if (startCore == "one") return true;
        return false;
    }

    // "four oh seven" → "407"; else thousands-formatted cardinal ("two hundred and three" → "203").
    private static string? AddressDigitString(List<string> sequence)
    {
        var filtered = new List<string>();
        foreach (string p in sequence) if (p != "and") filtered.Add(p);
        bool allSingle = true;
        foreach (string p in filtered)
            if (!(TryCardinal(p, out int v) && v >= 0 && v <= 9)) { allSingle = false; break; }
        if (allSingle)
        {
            var sb = new System.Text.StringBuilder();
            foreach (string p in filtered) sb.Append(CardinalWords[p].ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        long? v2 = ComputeValue(sequence);
        if (v2 is not null) return FormatThousands(v2.Value);
        return null;
    }

    private static bool IsYearContext(string? prevWordLower)
    {
        if (prevWordLower is null) return false;
        return YearContextWords.Contains(prevWordLower) || MonthWords.Contains(prevWordLower);
    }

    // Next non-ws token is AM/PM (dots optional) → canonical uppercase form + advance-past index.
    private static (string text, int consumedUpTo)? TrailingMeridiem(int index, List<Token> tokens)
    {
        var next = NextWord(index, tokens);
        if (next is not (var nextIdx, var nextTok)) return null;
        string normalized = nextTok.Core.ToLowerInvariant().Replace(".", "");
        if (normalized == "am" || normalized == "pm") return (normalized.ToUpperInvariant(), nextIdx + 1);
        return null;
    }

    // Parse a cardinal starting at the next non-ws token after `index`.
    private static (List<string> sequence, long value, int endExclusive)? NextCardinal(int index, List<Token> tokens)
    {
        int j = index + 1;
        while (j < tokens.Count && tokens[j].Kind == Kind.Whitespace) j += 1;
        if (j >= tokens.Count || tokens[j].Kind != Kind.Word) return null;
        var parsed = ParseCardinalSequence(tokens, j);
        if (parsed is (var sequence, var value, var consumed)) return (sequence, value, j + consumed);
        return null;
    }

    // US-style thousands grouping, invariant culture (NOT current culture).
    private static string FormatThousands(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);
}
