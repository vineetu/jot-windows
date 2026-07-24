namespace Jot.Transcription.Nemotron;

/// <summary>Quality tier from the NVIDIA model card — drives picker grouping, nothing else.</summary>
public enum LocaleTier { Auto, TranscriptionReady, BroadCoverage, AdaptationReady }

/// <summary>One supported locale: settings code, picker labels, prompt slot, quality tier.</summary>
public sealed record NemotronLocale(
    string Code, string EnglishName, string NativeName, long Slot, LocaleTier Tier);

/// <summary>
/// The single source of truth for Nemotron's spoken-language support: the model card's 40 language-
/// locales plus built-in auto-detect, each mapped to its prompt slot (the encoder's <c>lang_id</c> /
/// one-hot <c>language_mask</c> index — SAME slot space in both the int4 and fp16 exports). Slots come
/// from the fp16 export's <c>languages.json</c> (authoritative) and agree with every one of the 33
/// previously shipped, ONNX-verified name→id entries. Settings store the locale CODE ("en-US"); legacy
/// installs stored display names ("English") — <see cref="TryGetSlot"/> resolves both forever, and
/// <see cref="Normalize"/> upgrades stored values once at startup.
/// </summary>
public static class NemotronLocales
{
    public const string AutoCode = "auto";
    public const long AutoSlot = 101;
    public const string DefaultCode = "en-US";

    public static readonly IReadOnlyList<NemotronLocale> All =
    [
        new(AutoCode, "Auto detect", "Auto detect", AutoSlot, LocaleTier.Auto),

        // Transcription-ready (19) — the model card's best-accuracy tier.
        new("en-US", "English (US)", "English (US)", 0, LocaleTier.TranscriptionReady),
        new("en-GB", "English (UK)", "English (UK)", 1, LocaleTier.TranscriptionReady),
        new("es-ES", "Spanish (Spain)", "Español (España)", 2, LocaleTier.TranscriptionReady),
        new("es-US", "Spanish (US)", "Español (EE. UU.)", 3, LocaleTier.TranscriptionReady),
        new("fr-FR", "French (France)", "Français (France)", 8, LocaleTier.TranscriptionReady),
        new("fr-CA", "French (Canada)", "Français (Canada)", 100, LocaleTier.TranscriptionReady),
        new("it-IT", "Italian", "Italiano", 15, LocaleTier.TranscriptionReady),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)", 12, LocaleTier.TranscriptionReady),
        new("pt-PT", "Portuguese (Portugal)", "Português (Portugal)", 13, LocaleTier.TranscriptionReady),
        new("nl-NL", "Dutch", "Nederlands", 16, LocaleTier.TranscriptionReady),
        new("de-DE", "German", "Deutsch", 9, LocaleTier.TranscriptionReady),
        new("tr-TR", "Turkish", "Türkçe", 18, LocaleTier.TranscriptionReady),
        new("ru-RU", "Russian", "Русский", 11, LocaleTier.TranscriptionReady),
        new("ar-AR", "Arabic", "العربية", 7, LocaleTier.TranscriptionReady),
        new("hi-IN", "Hindi", "हिन्दी", 6, LocaleTier.TranscriptionReady),
        new("ja-JP", "Japanese", "日本語", 10, LocaleTier.TranscriptionReady),
        new("ko-KR", "Korean", "한국어", 14, LocaleTier.TranscriptionReady),
        new("vi-VN", "Vietnamese", "Tiếng Việt", 33, LocaleTier.TranscriptionReady),
        new("uk-UA", "Ukrainian", "Українська", 19, LocaleTier.TranscriptionReady),

        // Broad-coverage (13).
        new("pl-PL", "Polish", "Polski", 17, LocaleTier.BroadCoverage),
        new("sv-SE", "Swedish", "Svenska", 24, LocaleTier.BroadCoverage),
        new("cs-CZ", "Czech", "Čeština", 22, LocaleTier.BroadCoverage),
        new("nb-NO", "Norwegian (Bokmål)", "Norsk bokmål", 103, LocaleTier.BroadCoverage),
        new("da-DK", "Danish", "Dansk", 25, LocaleTier.BroadCoverage),
        new("bg-BG", "Bulgarian", "Български", 30, LocaleTier.BroadCoverage),
        new("fi-FI", "Finnish", "Suomi", 26, LocaleTier.BroadCoverage),
        new("hr-HR", "Croatian", "Hrvatski", 29, LocaleTier.BroadCoverage),
        new("sk-SK", "Slovak", "Slovenčina", 28, LocaleTier.BroadCoverage),
        new("zh-CN", "Chinese (Simplified)", "中文（简体）", 4, LocaleTier.BroadCoverage),
        new("hu-HU", "Hungarian", "Magyar", 23, LocaleTier.BroadCoverage),
        new("ro-RO", "Romanian", "Română", 20, LocaleTier.BroadCoverage),
        new("et-EE", "Estonian", "Eesti", 60, LocaleTier.BroadCoverage),

        // Adaptation-ready (8) — supported but weakest; the picker labels the group "Basic support".
        new("el-GR", "Greek", "Ελληνικά", 21, LocaleTier.AdaptationReady),
        new("he-IL", "Hebrew", "עברית", 64, LocaleTier.AdaptationReady),
        new("lt-LT", "Lithuanian", "Lietuvių", 31, LocaleTier.AdaptationReady),
        new("sl-SI", "Slovenian", "Slovenščina", 62, LocaleTier.AdaptationReady),
        new("lv-LV", "Latvian", "Latviešu", 61, LocaleTier.AdaptationReady),
        new("mt-MT", "Maltese", "Malti", 102, LocaleTier.AdaptationReady),
        new("th-TH", "Thai", "ไทย", 32, LocaleTier.AdaptationReady),
        new("nn-NO", "Norwegian (Nynorsk)", "Norsk nynorsk", 104, LocaleTier.AdaptationReady),
    ];

    private static readonly Dictionary<string, NemotronLocale> ByCode =
        All.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);

    // Every display name older builds ever stored (the shipped 33-name list). These resolve FOREVER —
    // a settings.json written by v1.1 must keep working even if the startup migration never ran
    // (hand-edited files, restored backups). Targets preserve the exact slot each name had.
    private static readonly Dictionary<string, string> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en-US", ["Spanish"] = "es-ES", ["Chinese"] = "zh-CN", ["Hindi"] = "hi-IN",
        ["Arabic"] = "ar-AR", ["French"] = "fr-FR", ["German"] = "de-DE", ["Japanese"] = "ja-JP",
        ["Russian"] = "ru-RU", ["Portuguese"] = "pt-BR", ["Korean"] = "ko-KR", ["Italian"] = "it-IT",
        ["Dutch"] = "nl-NL", ["Polish"] = "pl-PL", ["Turkish"] = "tr-TR", ["Ukrainian"] = "uk-UA",
        ["Romanian"] = "ro-RO", ["Greek"] = "el-GR", ["Czech"] = "cs-CZ", ["Hungarian"] = "hu-HU",
        ["Swedish"] = "sv-SE", ["Danish"] = "da-DK", ["Finnish"] = "fi-FI", ["Slovak"] = "sk-SK",
        ["Croatian"] = "hr-HR", ["Bulgarian"] = "bg-BG", ["Lithuanian"] = "lt-LT",
        ["Vietnamese"] = "vi-VN", ["Estonian"] = "et-EE", ["Latvian"] = "lv-LV",
        ["Slovenian"] = "sl-SI", ["Hebrew"] = "he-IL", ["Norwegian"] = "nb-NO",
    };

    /// <summary>Resolves a stored setting — locale code, legacy display name, or junk — to a prompt
    /// slot. Unknown/empty → false with the en-US slot, so a bad value can never mistranscribe.</summary>
    public static bool TryGetSlot(string? setting, out long slot)
    {
        var locale = Find(setting);
        slot = locale?.Slot ?? ByCode[DefaultCode].Slot;
        return locale is not null;
    }

    /// <summary>Canonical locale code for any stored value ("English"→"en-US", "es-us"→"es-US",
    /// unknown/empty→"en-US"). Used by the one-time settings migration and VM seeding.</summary>
    public static string Normalize(string? setting) => Find(setting)?.Code ?? DefaultCode;

    private static NemotronLocale? Find(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting)) return null;
        string key = setting.Trim();
        if (LegacyNames.TryGetValue(key, out string? code)) key = code;
        return ByCode.TryGetValue(key, out var locale) ? locale : null;
    }
}
