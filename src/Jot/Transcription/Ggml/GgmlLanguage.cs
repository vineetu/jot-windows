using Jot.Transcription.Nemotron;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Maps a Jot language setting onto the transcribe.cpp 0.1.3 language pointer.
/// The GGUF hard-errors the literal string <c>"auto"</c>, the empty string, and any
/// code not in <c>capabilities.languages</c> (the 8 AdaptationReady locales on the
/// published Q8_0). Autodetect is a C NULL. This method never returns a string the
/// runtime will reject.
/// </summary>
public static class GgmlLanguage
{
    public const string AutoSentinel = "auto";

    /// <summary>
    /// The 8 AdaptationReady codes Jot's picker still offers. The published Q8_0 GGUF
    /// rejects every one of them. Kept public so tests can lock the list without loading a model.
    /// </summary>
    public static readonly string[] AdaptationReady =
    [
        "el-GR", "he-IL", "lt-LT", "sl-SI", "lv-LV", "mt-MT", "th-TH", "nn-NO",
    ];

    /// <returns>
    /// A BCP-47 code to pass as <c>run_params.language</c>, or <c>null</c> for C NULL (autodetect).
    /// Never <c>"auto"</c>, never <c>""</c>, never an AdaptationReady code unless that code is
    /// actually listed in <paramref name="supported"/>.
    /// </returns>
    public static string? Map(string? setting, IReadOnlyCollection<string> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);

        if (string.IsNullOrWhiteSpace(setting))
            return null;

        string trimmed = setting.Trim();
        if (trimmed.Equals(AutoSentinel, StringComparison.OrdinalIgnoreCase))
            return null;

        // Resolve legacy display names ("English") and case ("en-us") to the canonical code.
        bool known = NemotronLocales.TryGetSlot(trimmed, out _);
        string code = NemotronLocales.Normalize(trimmed);
        if (code.Equals(AutoSentinel, StringComparison.OrdinalIgnoreCase))
            return null;

        if (Contains(supported, code))
            return code;

        // nn-NO is the one AdaptationReady code with a real sibling in the GGUF (nb-NO).
        if (code.Equals("nn-NO", StringComparison.OrdinalIgnoreCase) &&
            Contains(supported, "nb-NO"))
            return "nb-NO";

        // The other 7 AdaptationReady codes: C NULL (autodetect), not a silent en-US and
        // never a tag the runtime will hard-error.
        if (known) return null;

        return Contains(supported, NemotronLocales.DefaultCode) ? NemotronLocales.DefaultCode : null;
    }

    private static bool Contains(IReadOnlyCollection<string> supported, string code)
    {
        foreach (string s in supported)
        {
            if (s.Equals(code, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
