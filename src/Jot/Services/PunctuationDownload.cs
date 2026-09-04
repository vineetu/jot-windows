using Jot.Text;

namespace Jot.Services;

/// <summary>
/// Observable download state for the punctuation model ON ITS OWN.
///
/// Separate from <see cref="EnglishModelDownload"/>, which fetches the same model bundled with the
/// Granite graph, because the two answer different questions. Granite CANNOT run without
/// punctuation — it emits no marks at all — so there it is half of one 712 MB decision. But the
/// punctuation model is useful by itself: restored over the multilingual engine's own output it is a
/// quality upgrade for English at 209 MB, no engine swap.
///
/// Both surfaces install to the SAME folder and check the same files, so whichever arrives first
/// satisfies the other: a user who takes the English engine gets this row already ticked, and a user
/// who takes this one has that much less left to fetch.
/// </summary>
public sealed class PunctuationDownload : ModelDownload
{
    public const string PunctuationInstalledText = "Better punctuation · Installed";

    /// <summary>1024-based, matching every other size label in the app.</summary>
    public static int SizeMb => (int)Math.Round(PunctCapSegModelInstaller.TotalBytes / (1024.0 * 1024.0));

    public static string PunctuationNotInstalledText => $"Not installed (~{SizeMb} MB)";

    public const string ConsentTitle = "Download better punctuation?";

    public static string ConsentMessage =>
        $"Restores punctuation, capitalization and sentence breaks on English dictation — about " +
        $"{SizeMb} MB, downloaded once and kept on this PC.\n\nDictation keeps working without it, " +
        "using the speech model's own punctuation.";

    public PunctuationDownload(PunctCapSegModelInstaller installer)
        : base(installer, PunctuationInstalledText, PunctuationNotInstalledText) { }
}
