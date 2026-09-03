using Jot.Services.Download;
using Jot.Transcription;

namespace Jot.Text;

/// <summary>
/// Download recipe for punct_cap_seg_en — the casing/punctuation half of the English engine.
///
/// It is a SEPARATE download from the Granite graph even though the two are useless apart, because
/// only English needs it: a user dictating German pays for neither, and the ggml engine punctuates
/// on its own. Keeping them separate also means the 209 MB can be re-published without re-hosting
/// the 536 MB graph.
///
/// Upstream is <c>1-800-BAD-CODE/punctuation_fullstop_truecase_english</c> (Apache-2.0), mirrored to
/// our release unchanged — same block-the-HF-domain reason as every other model here.
/// </summary>
public sealed class PunctCapSegModelInstaller : IModelInstaller
{
    public const string ReleaseTag = "jot-model-punct-cap-seg-en-v1";

    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/" + ReleaseTag + "/";

    // Sizes + SHA-256 of the upstream files as mirrored, hashed locally 2026-09-01.
    private static readonly AssetManifest PunctManifest = new(BaseUrl,
    [
        new(PunctCapSegModel.ModelFile, 209_532_928,
            "dd922d459da618cd324280889740608b76fb3e9e61d3f402291be1251f91421b"),
        new(PunctCapSegModel.TokenizerFile, 587_902,
            "9e86d0263de80b3b68327a21f5350c8cdf846e4c4400253c9baf05e3d44871c3"),
    ]);

    public static long TotalBytes => PunctManifest.TotalBytes;

    public static string DescribeProgress(double fraction) => PunctManifest.DescribeProgress(fraction);

    private readonly PunctCapSegModel _model;

    public PunctCapSegModelInstaller(PunctCapSegModel model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    public AssetManifest Manifest => PunctManifest;

    public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => AssetDownloader.EnsureAsync(PunctManifest, _model.Directory, progress, ct);
}
