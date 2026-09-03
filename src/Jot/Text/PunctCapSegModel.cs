using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.Text;

/// <summary>
/// Locates the punctuation / true-casing / sentence-segmentation model
/// (<c>1-800-BAD-CODE/punctuation_fullstop_truecase_english</c>, Apache-2.0).
///
/// This is not an optional polish step for the Granite English engine — that engine's CTC head has
/// no casing or punctuation in its vocabulary at all, so without this the user gets an unbroken
/// lowercase run of words. It stays a separate model (and a separate download) because the ggml
/// engine punctuates on its own and must not pay for it.
///
/// TWO files:
///   * <c>punct_cap_seg_en.onnx</c>  — 209 MB graph, ids in / four label streams out.
///   * <c>spe_32k_lc_en.model</c>    — the SentencePiece model. The graph is fed ids, so without
///     this the model cannot be used at all; and the reconstruction needs id->piece too.
/// </summary>
public sealed class PunctCapSegModel
{
    public const string ModelFile = "punct_cap_seg_en.onnx";
    public const string TokenizerFile = "spe_32k_lc_en.model";
    public const string ModelFolder = "punct-cap-seg-en-onnx";

    private readonly string? _explicitDir;
    private readonly ISettingsStore? _settings;

    public PunctCapSegModel(string? directory = null, ISettingsStore? settings = null)
    {
        _explicitDir = directory;
        _settings = settings;
    }

    public string Directory => _explicitDir ?? Path.Combine(
        _settings is not null ? JotPaths.ModelsDir(_settings.Current) : JotPaths.DefaultModelsDir,
        ModelFolder);

    public string Graph => Path.Combine(Directory, ModelFile);
    public string Tokenizer => Path.Combine(Directory, TokenizerFile);

    public bool IsInstalled => File.Exists(Graph) && File.Exists(Tokenizer);
}
