namespace Jot.Vocabulary;

/// <summary>
/// One applied correction, as the FEEDBACK surfaces need it — the pill chip and its expand-panel
/// list. Deliberately not <c>CorrectionRecord</c>: those carry anchors and verdict identity that the
/// pill has no business knowing, and the pill outlives nothing (it is a 4-second signal).
/// </summary>
public readonly record struct VocabularyCorrection(string OriginalWord, string Term)
{
    /// <summary>The chip label. One correction shows the resulting TERM (tail-truncated at 14 chars
    /// so the auto-sized capsule can't outgrow the caption); two or more show the count.</summary>
    public static string ChipText(IReadOnlyList<VocabularyCorrection> corrections)
    {
        if (corrections.Count == 0) return "";
        if (corrections.Count > 1) return corrections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string term = corrections[0].Term;
        return term.Length > 14 ? term[..13] + "…" : term;
    }

    /// <summary>Folded into the pill WINDOW's single automation name — never announced as a separate
    /// peer, or Narrator says it twice (ux §3.3).</summary>
    public static string AutomationSuffix(IReadOnlyList<VocabularyCorrection> corrections) =>
        corrections.Count switch
        {
            0 => "",
            1 => $" Vocabulary used {corrections[0].Term}.",
            _ => $" Vocabulary used {corrections.Count} terms.",
        };
}
