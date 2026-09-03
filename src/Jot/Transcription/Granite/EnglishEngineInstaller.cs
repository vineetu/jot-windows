using Jot.Services.Download;
using Jot.Text;

namespace Jot.Transcription.Granite;

/// <summary>
/// The English engine as ONE installable thing: the Granite graph plus the punctuation model.
///
/// They are two releases but never two decisions. Granite's CTC head emits no casing and no
/// punctuation, so a user who ended up with the graph and not the punctuation model would get an
/// unbroken lowercase run of words — measurably worse than the multilingual engine they already
/// had. Presenting them as one download makes that state unreachable, and
/// <see cref="LanguageRoutedTranscriber"/> only routes English here when BOTH report installed.
/// </summary>
public sealed class EnglishEngineInstaller : IModelInstaller
{
    private readonly GraniteModelInstaller _granite;
    private readonly PunctCapSegModelInstaller _punct;

    public EnglishEngineInstaller(GraniteModelInstaller granite, PunctCapSegModelInstaller punct)
    {
        _granite = granite;
        _punct = punct;
    }

    public static long TotalBytes => GraniteModelInstaller.TotalBytes + PunctCapSegModelInstaller.TotalBytes;

    public bool IsInstalled => _granite.IsInstalled && _punct.IsInstalled;

    /// <summary>
    /// The two manifests flattened into one so the shared progress surface reports a single total.
    /// The base URLs differ per asset in reality, which is why the download is driven by
    /// <see cref="EnsureInstalledAsync"/> below rather than by handing this manifest to the
    /// downloader — this exists for the size/progress text only.
    /// </summary>
    public AssetManifest Manifest => new(_granite.Manifest.BaseUrl,
        [.. _granite.Manifest.Assets, .. _punct.Manifest.Assets]);

    /// <summary>
    /// Fetches both, reporting one continuous 0..1 fraction weighted by real byte counts — not two
    /// bars, and not a bar that jumps back to zero halfway through.
    /// </summary>
    public async Task EnsureInstalledAsync(IProgress<double>? progress = null,
                                           CancellationToken ct = default)
    {
        long total = TotalBytes;
        double graniteShare = GraniteModelInstaller.TotalBytes / (double)total;

        await _granite.EnsureInstalledAsync(Scaled(progress, 0, graniteShare), ct)
                      .ConfigureAwait(false);

        await _punct.EnsureInstalledAsync(Scaled(progress, graniteShare, 1 - graniteShare), ct)
                    .ConfigureAwait(false);
    }

    private static IProgress<double>? Scaled(IProgress<double>? inner, double offset, double span) =>
        inner is null ? null : new ScaledProgress(inner, offset, span);

    /// <summary>
    /// Maps one installer's 0..1 onto its slice of the combined bar, forwarding SYNCHRONOUSLY.
    ///
    /// Not <see cref="Progress{T}"/>: that posts each report to the captured context, so with no
    /// dispatcher (the CLI, any background caller) the two phases' reports land via unordered
    /// threadpool work items. A late granite report arriving after punct has started then shows the
    /// bar stepping BACKWARDS across the phase boundary — the one thing this composite exists to
    /// prevent — and leaves reports in flight after the download's await has already returned.
    /// Forwarding inline preserves the downloader's own ordering; the caller (ModelDownload) still
    /// marshals to the UI thread with its own Progress.
    /// </summary>
    private sealed class ScaledProgress(IProgress<double> inner, double offset, double span)
        : IProgress<double>
    {
        public void Report(double value) => inner.Report(offset + value * span);
    }
}
