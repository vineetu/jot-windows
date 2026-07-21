using System.Text.RegularExpressions;

namespace Jot.Text;

/// <summary>
/// Fixes the <c>&lt;unk&gt;</c> artifact the Nemotron decoder occasionally emits. Runs only on Nemotron output
/// (and on live partials). Case-exact <c>&lt;unk&gt;</c> only; idempotent. The rule-0 fast-path is load-bearing:
/// without it, rule 4's space-tidy would alter a no-op-language transcript, breaking the pipeline's
/// "strict byte-for-byte no-op" guarantee for languages that get no other cleaning.
/// </summary>
public static partial class ModelArtifactScrubber
{
    // digit(s) + optional spaces/tabs (never a newline) + literal <unk>  →  digit(s) + '%'.
    [GeneratedRegex(@"([0-9]+)[ \t]*<unk>")]
    private static partial Regex DigitUnkPercent();

    // Runs of spaces/tabs (not newlines) collapse to one.
    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpace();

    public static string Scrub(string text)
    {
        // Rule 0 — REQUIRED for byte-identity, not an optimization. The recorder always calls with
        // isNemotron:true, so a no-op-language transcript (ar/ko/ja) reaches here; without this guard,
        // rule 4 would rewrite its spacing. Case-exact: only a genuine "<unk>" defect triggers rules 1–4.
        if (string.IsNullOrEmpty(text) || !text.Contains("<unk>", StringComparison.Ordinal)) return text;

        // Rule 1 — "25<unk>" / "25 <unk>" → "25%". ASCII digits only; [ \t] never spans '\n' (rule 2:
        // "25\n\n<unk>" keeps its break because this fails, and rule 3 turns the stray <unk> into a space).
        string s = DigitUnkPercent().Replace(text, "$1%");

        // Rule 3 — any remaining "<unk>" → a single space (never glue words: "hola<unk>mundo" → "hola mundo").
        s = s.Replace("<unk>", " ", StringComparison.Ordinal);

        // Rule 4 — collapse space/tab runs (not newlines) and trim edge spaces/tabs, preserving interior \n\n.
        s = MultiSpace().Replace(s, " ");
        s = s.Trim(' ', '\t');
        return s;
    }
}
