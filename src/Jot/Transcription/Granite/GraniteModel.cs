using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.Transcription.Granite;

/// <summary>
/// Locates the Granite Speech 5.0 TurboCTC assets — Jot's ENGLISH engine. Same layout convention as
/// the GGUF speech model and the CTC spotter: a folder under the user's models directory, resolved
/// on each access so a data folder moved in Settings is honoured without a restart.
///
/// TWO files, both load-bearing:
///   * <c>model.int8.onnx</c> — the graph. Dynamic int8, per-channel, MatMul-only (536 MB). The
///     naive per-tensor quantization of the same graph was both slower AND dropped words; do not
///     re-quantize without re-running the WER comparison.
///   * <c>vocab.json</c>     — the id-ordered byte-level BPE piece list. Without it the engine can
///     run and produce ids it cannot turn into text.
/// </summary>
public sealed class GraniteModel
{
    public const string ModelFile = "model.int8.onnx";
    public const string VocabFile = "vocab.json";
    public const string ModelFolder = "granite-speech-5.0-470m-turboctc-onnx-int8";

    private readonly string? _explicitDir;
    private readonly ISettingsStore? _settings;

    public GraniteModel(string? directory = null, ISettingsStore? settings = null)
    {
        _explicitDir = directory;
        _settings = settings;
    }

    public string Directory => _explicitDir ?? Path.Combine(
        _settings is not null ? JotPaths.ModelsDir(_settings.Current) : JotPaths.DefaultModelsDir,
        ModelFolder);

    public string Graph => Path.Combine(Directory, ModelFile);
    public string Vocab => Path.Combine(Directory, VocabFile);

    /// <summary>True only when BOTH assets are present — a partial install must read as
    /// not-installed so the selector falls back to ggml instead of failing mid-dictation.</summary>
    public bool IsInstalled => File.Exists(Graph) && File.Exists(Vocab);
}
