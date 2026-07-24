using Jot.Services.Download;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Download recipe for the Nemotron 3.5 ASR streaming multilingual (int4 ONNX) assets, so the app can
/// ship without the ~0.75 GB model and fetch it once. The actual transfer (retry, Range-resume, stall
/// detection, checksum) lives in <see cref="AssetDownloader"/> — shared with the fp16 GPU model — this
/// class only pins WHAT to fetch and WHERE it goes.
/// </summary>
public sealed class NemotronModelInstaller : IModelInstaller
{
    // Self-hosted on our own GitHub release (public CDN), mirroring FfmpegInstaller/jot-deps-v1 and
    // decoupled from app version tags. Moved OFF huggingface.co after a Store cert lab failed the
    // first-run download from HF: a lab network that blocks/throttles a non-Microsoft host, plus our old
    // single-shot fetch, surfaced as "Download failed" and rejected the app. GitHub's release CDN is
    // rarely blocked, and the fetch retries + resumes so a transient blip can't kill 0.75 GB.
    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/jot-model-nemotron-int4-v1/";

    // Exact sizes + SHA-256 taken from the release assets themselves (GitHub API digests) — NOT from a
    // local install — so progress is byte-true and a corrupted download can't be promoted to "installed".
    private static readonly AssetManifest Int4Manifest = new(BaseUrl,
    [
        new(NemotronModel.EncoderFile, 2_677_548,
            "0b05217594ec0bda442e43a90a298ac2471a3bdcea9b169de34214e61a730e17"),
        new(NemotronModel.EncoderFile + ".data", 690_089_984,
            "2f27295855aeb99ab1f8cd2254418d9ad7a087ea8dbe85f5596b4d887ea7d630"),
        new(NemotronModel.DecoderFile, 4_696,
            "6a9f608dcbab71ebd81ffa4c198e82a5b6bb10f1c1830a94c752c5f543454df3"),
        new(NemotronModel.DecoderFile + ".data", 59_785_216,
            "e5fd55cbeeb268f9d383e2ee72735b9fbbb13aea4bc7cd38cb73b8e16f1366c7"),
        new(NemotronModel.JointFile, 2_136,
            "e2c7d2fa40a243bf82eaca36c15698c52129de9361d2875d7f223f67fcd9482d"),
        new(NemotronModel.JointFile + ".data", 37_830_656,
            "2e0fb1c060f3777a1a76e78d5589dd54f01505a06dffbd2588e315508b402c12"),
        new(NemotronModel.VocabFile, 64_024,
            "ca88922ac5a92c911b79985b69634d7a4c2ef604d61b71bbe2982210dd77cd43"),
    ]);

    /// <summary>Total download size in bytes — kept static for existing callers/tests.</summary>
    public static long TotalBytes => Int4Manifest.TotalBytes;

    /// <summary>Static passthrough kept for existing callers/tests; the text lives on the manifest now.</summary>
    public static string DescribeProgress(double fraction) => Int4Manifest.DescribeProgress(fraction);

    private readonly NemotronModel _model;

    public NemotronModelInstaller(NemotronModel model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    public AssetManifest Manifest => Int4Manifest;

    /// <summary>
    /// Ensures every asset is present, downloading any that are missing. Reports overall progress in
    /// [0,1]. Safe to call when already installed (per-file skips; also self-heals newly-added assets).
    /// </summary>
    public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => AssetDownloader.EnsureAsync(Int4Manifest, _model.Directory, progress, ct);
}
