namespace Jot.Text;

/// <summary>
/// Maps the app's stored language <em>display name</em> (e.g. "English", "German") to a base ISO-639 code.
/// Total over every name in the transcription language list; only the six active codes (en/es/de/fr/it/pt)
/// drive cleanup behavior, the rest return their code harmlessly (the cleaner no-ops on them), and anything
/// unknown / null / "None" returns "" so the orchestrator's byte-identity path kicks in.
/// </summary>
public static class LanguageCode
{
    // Case-insensitive display-name → base code. Region subtags are collapsed to the base code (the app
    // stores "Portuguese"/"Chinese"/"Norwegian", never pt-BR/Mandarin/nb), so a base code always suffices.
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en", ["Spanish"] = "es", ["German"] = "de", ["French"] = "fr",
        ["Italian"] = "it", ["Portuguese"] = "pt",
        ["Chinese"] = "zh", ["Hindi"] = "hi", ["Arabic"] = "ar", ["Japanese"] = "ja",
        ["Russian"] = "ru", ["Korean"] = "ko", ["Dutch"] = "nl", ["Polish"] = "pl",
        ["Turkish"] = "tr", ["Ukrainian"] = "uk", ["Romanian"] = "ro", ["Greek"] = "el",
        ["Czech"] = "cs", ["Hungarian"] = "hu", ["Swedish"] = "sv", ["Danish"] = "da",
        ["Finnish"] = "fi", ["Slovak"] = "sk", ["Croatian"] = "hr", ["Bulgarian"] = "bg",
        ["Lithuanian"] = "lt", ["Vietnamese"] = "vi", ["Estonian"] = "et", ["Latvian"] = "lv",
        ["Slovenian"] = "sl", ["Hebrew"] = "he", ["Norwegian"] = "nb",
    };

    /// <summary>Base ISO code for a display name, or "" when unmapped / null / "None".</summary>
    public static string ToIso(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "";
        return Map.TryGetValue(displayName.Trim(), out string? iso) ? iso : "";
    }
}
