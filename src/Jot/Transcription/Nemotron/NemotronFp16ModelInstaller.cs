using Jot.Services.Download;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Download recipe for the OPTIONAL Nemotron 3.5 fp16 ONNX assets (~1.22 GiB) — the GPU/DirectML build.
/// Same self-hosted GitHub-release pattern as the int4 model (see <see cref="NemotronModelInstaller"/>
/// for why we host these ourselves); the transfer engine is the shared <see cref="AssetDownloader"/>.
///
/// TRAP: the release's <c>encoder.onnx</c> is the DirectML-safe NOSPLIT encoder (GLU channel-Splits
/// rewritten as Slice — the stock encoder silently returns empty text on DML), and its external weights
/// are named <c>encoder_nosplit.onnx.data</c> because the .onnx protobuf hard-references that filename.
/// Upload/verify the release with exactly these names or the encoder will fail to load its weights.
/// </summary>
public sealed class NemotronFp16ModelInstaller : IModelInstaller
{
    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/jot-model-nemotron-fp16-v1/";

    // Exact sizes + SHA-256 of the staged release files (hashed at upload time from D:\jot-fp16).
    private static readonly AssetManifest Fp16Manifest = new(BaseUrl,
    [
        new(NemotronFp16Model.EncoderFile, 22_149_665,
            "a202b582e2b0062dce0fda846df5b33ca0e973dcb83e71252eed0eff82421b54"),
        new(NemotronFp16Model.EncoderDataFile, 1_236_396_032,
            "4e0ae7c84854c5d322f9a168283e47c4f2da603a5b1f4ce402cad66f939e4b92"),
        new(NemotronFp16Model.DecoderFile, 7_040,
            "965f7cb2fa4b04364d1562c76e9d756e7f6c50548e0436c6c379cb4189bf06fb"),
        new(NemotronFp16Model.DecoderFile + ".data", 29_880_320,
            "cafdb046f5dc849be2c0183dd3da17befa3f3a4dc5cead867793c23a69f5b929"),
        new(NemotronFp16Model.JointFile, 3_207,
            "a4ae1a699ab5aee9e85499c51907ec58714806c54638c7b5357510d7dded60db"),
        new(NemotronFp16Model.JointFile + ".data", 18_911_296,
            "35eb3b1c090cc72a451f7250b7ee0e50573cebb3bb4efd9cf90ae6c6d0374d92"),
        new(NemotronFp16Model.VocabFile, 236_127,
            "fd726dc25cc0c0b46403675127e831bab56ca56b8c7ae3ee7781f5c6d7692f4f"),
        new(NemotronFp16Model.LanguagesFile, 2_020,
            "a1efe863e1057d4658bce8175c978020ac35a63a3c9a65ae899f324a32c8e4f6"),
    ]);

    private readonly NemotronFp16Model _model;

    public NemotronFp16ModelInstaller(NemotronFp16Model model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    public AssetManifest Manifest => Fp16Manifest;

    public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => AssetDownloader.EnsureAsync(Fp16Manifest, _model.Directory, progress, ct);
}
