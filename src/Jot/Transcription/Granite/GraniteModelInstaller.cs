using Jot.Services.Download;

namespace Jot.Transcription.Granite;

/// <summary>
/// Download recipe for the Granite Speech 5.0 TurboCTC int8 ONNX — the English engine. Transfer
/// (retry, Range-resume, stall, checksum) is the shared <see cref="AssetDownloader"/>; this class
/// only pins WHAT and WHERE.
///
/// Self-hosted on our own GitHub release for the same Store-cert reason as the GGUF: a lab that
/// blocks huggingface.co failed the first-run download. Do not point this at HF.
///
/// The graph is OUR export, not an upstream artifact — IBM publishes safetensors only. It is
/// dynamic int8, per-channel, MatMul-only. That recipe is load-bearing rather than incidental: the
/// default per-tensor quantization of the same graph measured BOTH slower and worse (4.74% vs 3.83%
/// WER on LibriSpeech, dropping whole words). Re-quantizing without re-running that comparison is
/// how the English engine silently gets worse.
/// </summary>
public sealed class GraniteModelInstaller : IModelInstaller
{
    public const string ReleaseTag = "jot-model-granite-speech-5.0-int8-v1";

    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/" + ReleaseTag + "/";

    // Sizes + SHA-256 of the exported and quantized files, hashed locally 2026-09-01.
    private static readonly AssetManifest GraniteManifest = new(BaseUrl,
    [
        new(GraniteModel.ModelFile, 535_621_679,
            "78fe229f9dd31966d0b270323cd3ccc06f270efa694dabe372d5c35a158b4505"),
        new(GraniteModel.VocabFile, 177_452,
            "d4360bc580905236970fb6c95db04653b2fff7601640e1220506693a2b13bf55"),
    ]);

    public static long TotalBytes => GraniteManifest.TotalBytes;

    public static string DescribeProgress(double fraction) => GraniteManifest.DescribeProgress(fraction);

    private readonly GraniteModel _model;

    public GraniteModelInstaller(GraniteModel model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    public AssetManifest Manifest => GraniteManifest;

    public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => AssetDownloader.EnsureAsync(GraniteManifest, _model.Directory, progress, ct);
}
