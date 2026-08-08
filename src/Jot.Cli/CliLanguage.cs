using Jot.Transcription.Nemotron;

namespace Jot.Cli;

/// <summary>
/// Turns whatever the user typed into a code that exists in <see cref="NemotronLocales.All"/>, ONCE, up
/// front. Nothing downstream may see a bare subtag: <c>NemotronLocales.Normalize("es")</c> silently
/// returns "en-US" and <c>TryGetSlot("es")</c> falls back to the en-US slot, so "--language es" would
/// transcribe English without a word of complaint. A subtag with no locale (e.g. "no" — the codes are
/// nb-NO/nn-NO) is an error rather than a guess.
/// </summary>
internal static class CliLanguage
{
    public static bool TryCanonicalize(string? input, out string canonical)
    {
        canonical = NemotronLocales.DefaultCode;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim();

        foreach (NemotronLocale l in NemotronLocales.All)
        {
            if (string.Equals(l.Code, s, StringComparison.OrdinalIgnoreCase))
            {
                canonical = l.Code;
                return true;
            }
        }

        // First match in declaration order, so the primary locale wins: es→es-ES, en→en-US, pt→pt-BR.
        string primary = s.Split('-', '_')[0];
        foreach (NemotronLocale l in NemotronLocales.All)
        {
            if (string.Equals(l.Code.Split('-')[0], primary, StringComparison.OrdinalIgnoreCase))
            {
                canonical = l.Code;
                return true;
            }
        }
        return false;
    }

    public static bool IsAuto(string canonical) =>
        string.Equals(canonical, NemotronLocales.AutoCode, StringComparison.Ordinal);

    public static string ValidCodes => string.Join(", ", NemotronLocales.All.Select(l => l.Code));
}
