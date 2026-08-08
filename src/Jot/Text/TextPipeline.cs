namespace Jot.Text;

/// <summary>
/// The deterministic, on-device, always-on cleanup every dictation passes through before it is saved and
/// pasted. No LLM, no network. Order: (ParagraphSegmenter — deferred) → ModelArtifactScrubber (Nemotron only)
/// → FillerWordCleaner(lang) → NumberNormalizer (English only) → PostProcessing (Latin scripts only).
/// Unknown languages pass through byte-identical.
/// Pure and thread-safe: <see cref="Clean"/> (recorder thread) and <see cref="CleanPartial"/> (pill thread)
/// share no mutable state.
/// </summary>
public static class TextPipeline
{
    /// <summary>
    /// Who gets <see cref="PostProcessing"/>. Its rules — collapse whitespace runs, drop the space before
    /// sentence punctuation — read spacing as a Latin-script writer would, so they are wrong for a
    /// SPACELESS script (zh/ja/th/km/lo/my: every collapsed run there is meaningful or absent) and are
    /// simply unwanted for every other non-Latin script the engine ships, where the pipeline's promise is
    /// that it returns the transcript byte-identical. An ALLOWLIST rather than a CJK denylist so a newly
    /// added locale keeps that promise until someone deliberately opts it in.
    /// </summary>
    private static readonly IReadOnlySet<string> LatinScript = new HashSet<string>(StringComparer.Ordinal)
    {
        "en", "es", "de", "fr", "it", "pt", "nl", "pl", "ro", "cs", "hu", "sv", "da", "fi", "sk",
        "hr", "sl", "lt", "lv", "et", "tr", "vi", "mt", "nb", "nn", "no",
    };

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

        if (iso == "en") s = NumberNormalizer.Normalize(s);   // English hard gate

        if (LatinScript.Contains(iso)) s = PostProcessing.Apply(s);

        // LAST, and it must stay last: PostProcessing and the number pass both trim the trailing space
        // FillerWordCleaner appends, and that space is the paste contract ("this dictation joins the
        // next one"). Gated on the cleaner having run, so a language it skips does not gain a trailing
        // space it never had.
        if (fillerRan) s = EnsureSingleTrailingSpace(s);

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
