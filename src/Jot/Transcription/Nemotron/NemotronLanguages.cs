namespace Jot.Transcription.Nemotron;

/// <summary>
/// Maps a UI language name to the Nemotron encoder's <c>lang_id</c> conditioning index. English is
/// confirmed = 0 from the reference. Entries are added as each language's index is verified; an
/// unknown language returns false from <see cref="TryGetId"/> and the caller leaves the current
/// (English) id in place rather than guessing a wrong index and mistranscribing.
/// </summary>
public static class NemotronLanguages
{
    // lang_id values for the encoder's language conditioning. The model has no on-disk language table;
    // these come from the known Nemotron prompt dictionary and were cross-checked against the ONNX
    // itself (feeding each id and reading the &lt;xx-XX&gt; tag the model emits). English=0, Spanish=2,
    // Hindi=6, Portuguese=12, Vietnamese=33 are empirically confirmed. The high-WER "adaptation" tier
    // (Greek, Estonian, Latvian, Slovenian, Hebrew, Norwegian) is lower-confidence — correct index,
    // but the model itself is weak there; smoke-test with real audio before relying on it.
    private static readonly Dictionary<string, long> Ids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = 0,
        ["Spanish"] = 2,
        ["Chinese"] = 4,       // zh-CN (Simplified / Mandarin)
        ["Hindi"] = 6,
        ["Arabic"] = 7,
        ["French"] = 8,
        ["German"] = 9,
        ["Japanese"] = 10,
        ["Russian"] = 11,
        ["Portuguese"] = 12,   // pt-BR (Brazilian)
        ["Korean"] = 14,
        ["Italian"] = 15,
        ["Dutch"] = 16,
        ["Polish"] = 17,
        ["Turkish"] = 18,
        ["Ukrainian"] = 19,
        ["Romanian"] = 20,
        ["Greek"] = 21,
        ["Czech"] = 22,
        ["Hungarian"] = 23,
        ["Swedish"] = 24,
        ["Danish"] = 25,
        ["Finnish"] = 26,
        ["Slovak"] = 28,
        ["Croatian"] = 29,
        ["Bulgarian"] = 30,
        ["Lithuanian"] = 31,
        ["Vietnamese"] = 33,
        ["Estonian"] = 60,
        ["Latvian"] = 61,
        ["Slovenian"] = 62,
        ["Hebrew"] = 64,
        ["Norwegian"] = 103,   // nb-NO (Bokmål)
    };

    public static bool TryGetId(string? language, out long id)
    {
        if (!string.IsNullOrWhiteSpace(language) && Ids.TryGetValue(language, out id)) return true;
        id = 0;
        return false;
    }
}
