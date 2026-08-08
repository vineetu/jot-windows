using Jot.Transcription.Nemotron;

namespace Jot.Vocabulary;

/// <summary>
/// WHICH languages the model-free corrector may run in, and HOW LOOSE it may be in each — both
/// measured, per language, by experiment E6 (<c>docs/plans/vocabulary-brake-per-language.md</c>):
/// 9500 FLEURS clips of real speech through the shipping Nemotron engine and the real gate.
///
/// This table exists because the corrector's only safety net outside its own threshold is the
/// common-word brake, and the brake's strength is exactly "how much of running text a 24 000-entry
/// frequency list covers" — 88.8 % of English types, 56.9 % of Greek, 60.1 % of Finnish. The
/// corrector shipped to 19 languages on the strength of an English measurement; this is what
/// measuring the other 18 said.
///
/// Budget: **1.0 false applies per 1000 words**, on a realistic 25-term list AND on a mechanically
/// adversarial one. A language at or above 0.9 (one measured event from the line) gets the loosest
/// distance that brings both arms to ≤ 0.5; one that cannot reach that without losing half its recall
/// is not served at all. Nothing here is tuned for a language the measurement cleared — a knob fitted
/// to ±1 event in 10 000 words is noise, and the recall it costs is paid by users who had no problem.
/// </summary>
public static class VocabularyLimits
{
    /// <summary>No cap beyond the corrector's own edit budget: one edit per five term characters,
    /// which tops out at a normalized distance of 0.33. The English-measured setting.</summary>
    public const double NoLimit = 1.0;

    /// <summary>What an unmeasured language would get if one ever reached the corrector. Unreachable
    /// today (<see cref="VocabularyRunner.ModeFor"/> answers Off for anything not in this table), and
    /// deliberately the TIGHTEST shipped value rather than the loosest.</summary>
    public const double Unmeasured = 0.15;

    // measured 2026-07-26, all rates in false applies per 1000 reference words,
    // "realistic 25-term list / adversarial 25-term list":
    private static readonly IReadOnlyDictionary<string, double> Ships =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            // English is Acoustic; this is the corrector's setting for the window before the 132 MB
            // checkpoint arrives, and it is E5's measured 0.27 / 0.22. Do not tighten it here without
            // re-running E5 — that number is quoted in three documents.
            ["en"] = NoLimit,

            // INSIDE BUDGET AT THE ENGLISH SETTING — ship exactly as English does.
            ["cs"] = NoLimit,   // 0.55 / 0.88  ← the closest pass; type coverage 72.0 %
            ["da"] = NoLimit,   // 0.10 / 0.79
            ["es"] = NoLimit,   // 0.00 / 0.32
            ["fi"] = NoLimit,   // 0.00 / 0.41  ← §7.5 predicted the worst; see the doc, it is wrong
            ["fr"] = NoLimit,   // 0.00 / 0.08
            ["hu"] = NoLimit,   // 0.34 / 0.57
            ["it"] = NoLimit,   // 0.09 / 0.09
            ["nl"] = NoLimit,   // 0.00 / 0.09
            ["pt"] = NoLimit,   // 0.00 / 0.09
            ["ro"] = NoLimit,   // 0.09 / 0.81
            ["sk"] = NoLimit,   // 0.32 / 0.11
            ["sv"] = NoLimit,   // 0.29 / 0.69

            // OVER BUDGET UNCAPPED. Value = the loosest measured distance that brings both arms to
            // ≤ 0.5; the recall it costs is in the doc's table, and it is the price of the brake being
            // weaker here than in English.
            ["bg"] = 0.25,      // 1.27 adversarial → 0.10 / 0.20, recall 50.7 → 43.8
            ["de"] = 0.15,      // 0.96 adversarial → 0.00 / 0.19, recall 54.3 → 31.4 (long terms; 0.15
                                //   still allows an edit on most German words)
            ["el"] = 0.20,      // 1.42 realistic   → 0.27 / 0.09, recall 35.7 → 18.8
            ["pl"] = 0.20,      // 0.90 adversarial → 0.11 / 0.45, recall 51.8 → 33.9
            ["ru"] = 0.20,      // 1.73 adversarial → 0.32 / 0.43, recall 60.3 → 56.9
            ["uk"] = 0.20,      // 0.99 adversarial → 0.33 / 0.22, recall 59.0 → 47.5

            // sl (Slovenian) is DELIBERATELY ABSENT, and removing this comment does not make it safe.
            // 1.72 adversarial false applies per 1000 words, and the only setting that gets it under
            // 0.5 (0.20) leaves 16 % of missed terms recovered — 43 % of what it recovers uncapped.
            // A feature that corrupts at the budget line and recovers one term in six is not a feature.
            // The engine is also at 58.6 % WER there, so the premise (a mostly-right transcript with a
            // few mis-spelled terms) does not hold in the first place.
            //
            // sr (Serbian) is absent for a different reason: it HAS a 24 000-entry list and no
            // Nemotron locale, so no setting can select it. Measured on Croatian audio as a probe —
            // see the doc — but enabling a language is not something to do on a probe.
        };

    /// <summary>Whether the textual corrector is served at all in this language. Consulted by
    /// <see cref="VocabularyRunner.ModeFor"/>, which is where "no brake ⇒ no run" also lives.</summary>
    public static bool TextualShips(string? language) => Ships.ContainsKey(Key(language));

    /// <summary>The largest normalized skeleton distance the corrector may accept here.</summary>
    public static double TextualMaxDistance(string? language) =>
        Ships.TryGetValue(Key(language), out double d) ? d : Unmeasured;

    /// <summary>Every language this table serves — the one list the UI counts, so the tour card and
    /// the Store listing cannot state a number the table has stopped having.</summary>
    public static readonly IReadOnlyList<string> Languages = [.. Ships.Keys];

    /// <summary>
    /// The table's key: a primary language subtag, resolved so an unrecognised language FAILS CLOSED.
    ///
    /// <see cref="NemotronLocales.Normalize"/> alone cannot be used here: it answers the DEFAULT locale
    /// (en-US) for anything it does not recognise, which is indistinguishable from a real English hit —
    /// so a bare subtag ("de") or an unknown one ("xx") would inherit ENGLISH's uncapped setting, the
    /// loosest in the table, in the exact place <see cref="Unmeasured"/> above promises the tightest.
    /// <see cref="NemotronLocales.TryGetSlot"/> is the honest "was this recognised?", so a stored
    /// display name ("German") still resolves through the locale table, and everything else keeps its
    /// OWN subtag and simply misses.
    /// </summary>
    private static string Key(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "";
        string raw = language.Trim();
        string code = NemotronLocales.TryGetSlot(raw, out _) ? NemotronLocales.Normalize(raw) : raw;
        return code.Split('-', '_')[0].ToLowerInvariant();
    }
}
