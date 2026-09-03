using Jot.Transcription.Granite;

namespace Jot.Services;

/// <summary>
/// Observable download state for the OPTIONAL English engine (Granite + punctuation).
///
/// Optional in the same sense as the vocabulary model, and for the same reason it is never started
/// automatically: English already works on the required GGUF, so this is a ~710 MB fetch that buys
/// accuracy and speed rather than capability. The user is told the size before anything begins.
/// </summary>
public sealed class EnglishModelDownload : ModelDownload
{
    public const string EnglishInstalledText = "Granite Speech 5.0 (English) · Installed";

    /// <summary>1024-based, matching every other size label in the app so the consent prompt, the
    /// idle row and the running progress line never quote three different numbers.</summary>
    public static int SizeMb => (int)Math.Round(EnglishEngineInstaller.TotalBytes / (1024.0 * 1024.0));

    public static string EnglishNotInstalledText => $"Not installed (~{SizeMb} MB)";

    public const string ConsentTitle = "Download the English engine?";

    public static string ConsentMessage =>
        $"A faster, more accurate English-only engine is available — about {SizeMb} MB, downloaded " +
        "once and kept on this PC.\n\nEnglish dictation keeps working without it, on the engine you " +
        "already have. You can start the download later from Settings.";

    /// <summary>
    /// Whether offering the download makes sense: only for English, only when it is not already
    /// there, and never while one is running. Pure so the rule is testable without a dialog.
    /// </summary>
    public static bool ShouldOffer(bool languageIsEnglish, bool installed, bool downloading) =>
        languageIsEnglish && !installed && !downloading;

    public EnglishModelDownload(EnglishEngineInstaller installer)
        : base(installer, EnglishInstalledText, EnglishNotInstalledText) { }
}
