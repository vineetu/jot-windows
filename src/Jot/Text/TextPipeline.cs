namespace Jot.Text;

/// <summary>
/// The deterministic, on-device, always-on cleanup every dictation passes through before it is saved and
/// pasted. No LLM, no network. Order: (ParagraphSegmenter — deferred) → ModelArtifactScrubber (Nemotron only)
/// → FillerWordCleaner(lang) → NumberNormalizer (English only). Unknown languages pass through byte-identical.
/// Pure and thread-safe: <see cref="Clean"/> (recorder thread) and <see cref="CleanPartial"/> (pill thread)
/// share no mutable state.
/// </summary>
public static class TextPipeline
{
    /// <param name="languageName">The stored display name (ISettingsStore.Language), e.g. "English".</param>
    /// <param name="isNemotron">Gates the &lt;unk&gt; scrubber. Always true today (the wired engine is Nemotron).</param>
    public static string Clean(string text, string languageName, bool isNemotron)
    {
        if (string.IsNullOrEmpty(text)) return text;

        string iso = LanguageCode.ToIso(languageName);   // "en","es",… or "" (unknown)
        string s = text;

        if (isNemotron) s = ModelArtifactScrubber.Scrub(s); // language-agnostic; safe no-op on clean text

        bool fillerRan = false;
        if (iso is "en" or "es" or "de" or "fr" or "it" or "pt")
        {
            s = FillerWordCleaner.Clean(s, iso);         // owns its own trailing space
            fillerRan = true;
        }

        if (iso == "en")
        {
            s = NumberNormalizer.Normalize(s);           // English hard gate
            if (fillerRan) s = EnsureSingleTrailingSpace(s); // the number pass may eat the cleaner's trailing space
        }

        return s;
    }

    /// <summary>Cosmetic live-partial pass — scrubber only, so it's idempotent over the growing partial. The
    /// authoritative clean is <see cref="Clean"/> on the final transcript.</summary>
    public static string CleanPartial(string partial, bool isNemotron)
    {
        if (string.IsNullOrEmpty(partial)) return partial;
        return isNemotron ? ModelArtifactScrubber.Scrub(partial) : partial;
    }

    private static string EnsureSingleTrailingSpace(string s) =>
        s.Length == 0 ? s : s.TrimEnd(' ', '\t') + " ";
}
