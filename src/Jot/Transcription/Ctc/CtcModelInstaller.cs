using Jot.Services.Download;

namespace Jot.Transcription.Ctc;

/// <summary>
/// Download recipe for the optional CTC keyword-spotter assets (~132 MB). The transfer engine (retry,
/// Range-resume, stall detection, free-space guard, SHA-256) is the shared <see cref="AssetDownloader"/>;
/// this class only pins WHAT to fetch and WHERE from, exactly like the two Nemotron installers.
///
/// RELEASE PREREQUISITE — the tag below does not exist yet. The three files must be uploaded verbatim to
/// a <c>vineetu/jot-windows</c> release tagged <c>jot-model-parakeet-ctc-110m-v1</c>; the sizes and
/// hashes here were computed from the exact bytes that have to be uploaded, so a re-converted or
/// re-compressed copy WILL fail the checksum rather than install something we never measured. Until the
/// release is cut every download 404s — which is the designed-for state, not a bug: <see cref="CtcModel"/>
/// reads as not-installed, <c>CtcVocabularySpotter.IsReady</c> is false, and vocabulary is simply
/// unavailable with a log line naming the URL.
///
/// LICENCE: both the graph and the tokenizer derive from nvidia/parakeet-tdt_ctc-110m (CC-BY-4.0), so
/// re-hosting them is redistribution and the About page's attribution must name BOTH.
/// </summary>
public sealed class CtcModelInstaller : IModelInstaller
{
    /// <summary>Self-hosted on our own GitHub release for the same reason the Nemotron models are: a Store
    /// certification lab failed a first-run download from a non-Microsoft host, and GitHub's release CDN
    /// is rarely blocked. Decoupled from app version tags.</summary>
    public const string ReleaseTag = "jot-model-parakeet-ctc-110m-v1";

    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/" + ReleaseTag + "/";

    // Exact sizes + SHA-256 of the three staged files. Provenance, so a human cutting the release can
    // reproduce them byte-for-byte:
    //   model.int8.onnx  — sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2, unchanged
    //   tokens.txt       — the same archive, unchanged
    //   tokenizer.model  — parakeet-tdt_ctc-110m.nemo, member <hash>_tokenizer.model, RENAMED only
    private static readonly AssetManifest CtcManifest = new(BaseUrl,
    [
        new(CtcModel.ModelFile, 131_652_171,
            "9177a9146cf32ee0cc8152276ef95116f312018d316be37ccf57f7efea81fc1a"),
        new(CtcModel.TokensFile, 9_953,
            "450e56bd2f036fe5b6aa821865838cc5aa9d8b0106134ce9a9ba0664abe6cd10"),
        new(CtcModel.TokenizerFile, 251_875,
            "4b97fbc15390829e3b94069fdeccf0d053561d090e12d94789e4e71c531f93fc"),
    ]);

    /// <summary>Total download size in bytes — the number the consent copy has to quote honestly.</summary>
    public static long TotalBytes => CtcManifest.TotalBytes;

    private readonly CtcModel _model;

    public CtcModelInstaller(CtcModel model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    public AssetManifest Manifest => CtcManifest;

    public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        => AssetDownloader.EnsureAsync(CtcManifest, _model.Directory, progress, ct);
}
