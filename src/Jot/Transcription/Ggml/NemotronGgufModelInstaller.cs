using Jot.Services.Download;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Download recipe for the Nemotron 3.5 Q8_0 GGUF — the one model that replaces the int4+fp16 pair
/// when ggml is the engine. Transfer (retry, Range-resume, stall, checksum) is the shared
/// <see cref="AssetDownloader"/>; this class only pins WHAT and WHERE, same as the ONNX installers.
///
/// Self-hosted on our GitHub release for the same Store-cert reason as int4: a lab that blocks
/// huggingface.co failed the first-run download. Do not point this at HF.
/// </summary>
public sealed class NemotronGgufModelInstaller : IModelInstaller
{
    public const string ReleaseTag = "jot-model-nemotron-gguf-q8-v1";

    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/" + ReleaseTag + "/";

    // Size + SHA-256 of the published Q8_0 file (HF blob b94545b3…, hashed locally 2026-08-14).
    private static readonly AssetManifest GgufManifest = new(BaseUrl,
    [
        new(NemotronGgufModel.FileName, 751_094_240,
            "b94545b313b3223fda7b2857a52681da813935c2127643d1e9ff0c23d988089c"),
    ]);

    public static long TotalBytes => GgufManifest.TotalBytes;

    public static string DescribeProgress(double fraction) => GgufManifest.DescribeProgress(fraction);

    private readonly NemotronGgufModel _model;

    public NemotronGgufModelInstaller(NemotronGgufModel model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    public AssetManifest Manifest => GgufManifest;

    public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => AssetDownloader.EnsureAsync(GgufManifest, _model.Directory, progress, ct);
}
