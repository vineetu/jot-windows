using Jot.Transcription.Ctc;

namespace Jot.Services;

/// <summary>
/// Observable download state for the OPTIONAL vocabulary keyword-spotter model. A third singleton
/// alongside <see cref="ModelDownload"/> (required int4) and <see cref="GpuModelDownload"/> (optional
/// fp16), because each needs its own progress/status surface while sharing one transfer engine.
///
/// Unlike the other two this one is NEVER started by the app: the required model is a prerequisite for
/// dictating at all and the fp16 upgrade is silent-and-optional, but this is a nine-figure byte fetch
/// for a feature the user has to opt into. Settings asks first (<see cref="ShouldOffer"/> +
/// <see cref="ConsentMessage"/>) and only then calls <c>EnsureAsync</c>.
/// </summary>
public sealed class CtcModelDownload : ModelDownload
{
    public const string VocabularyInstalledText = "Vocabulary model · Installed";

    /// <summary>
    /// Download size, derived from the manifest so it can never drift from what is actually fetched.
    ///
    /// 1024-based, like <see cref="Download.AssetManifest.DescribeProgress"/> and the two Nemotron row
    /// labels — the same bytes read as ~132 MB in decimal MB (which is what the design docs quote), and
    /// three different numbers across the consent prompt, the idle row and the running progress line is
    /// exactly the kind of thing that reads as a bug.
    /// </summary>
    public static int SizeMb => (int)Math.Round(CtcModelInstaller.TotalBytes / (1024.0 * 1024.0));

    public static string VocabularyNotInstalledText => $"Not installed (~{SizeMb} MB)";

    /// <summary>Consent copy. States the size BEFORE anything starts, and says what happens if they
    /// decline — declining must not read as losing the terms they just enabled.</summary>
    public static string ConsentMessage =>
        $"Custom vocabulary needs an extra on-device model — about {SizeMb} MB, downloaded once and " +
        "kept on this PC.\n\nDownload it now? Your terms are saved either way, and you can start the " +
        "download later from the Vocabulary section in Settings.";

    public const string ConsentTitle = "Download the vocabulary model?";

    /// <summary>
    /// Whether turning the toggle on should ask for the download. Pure so the rule is testable without
    /// a dialog.
    ///
    /// Gated on the language too: when vocabulary is switched on under a non-English language the pane
    /// is simultaneously telling the user their terms will NOT be applied, and asking for ~132 MB in
    /// that same instant is noise. The row's own Download button stays available either way, so nothing
    /// is unreachable — it just isn't pushed.
    /// </summary>
    public static bool ShouldOffer(bool enabled, bool installed, bool downloading, bool languageOk) =>
        enabled && !installed && !downloading && languageOk;

    public CtcModelDownload(CtcModelInstaller installer)
        : base(installer, VocabularyInstalledText, VocabularyNotInstalledText) { }
}
