using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Locates the on-device Nemotron 3.5 ASR streaming multilingual (FP16 ONNX) assets — the GPU build.
/// They live under <c>&lt;data&gt;\models\nemotron-3.5-asr-streaming-0.6b-onnx-fp16</c>. Unlike the int4
/// build (CPU-only; int4 can't run on DirectML) the FP16 graphs initialise and run correctly on
/// DirectML, so this model backs the "GPU" transcription device.
///
/// The encoder is the DirectML-safe <c>encoder_nosplit</c> variant (24 GLU channel-Split nodes
/// rewritten as Slice ops — the stock encoder gives an empty transcript on DML). It is shipped AS
/// <c>encoder.onnx</c> but its external weights keep the original name <c>encoder_nosplit.onnx.data</c>
/// (the .onnx protobuf hard-references that filename), so the locator points at that data file.
/// Detok uses <c>vocab.json</c> (id→token JSON), NOT the int4 build's <c>vocab.txt</c>.
/// </summary>
public sealed class NemotronFp16Model
{
    public const string EncoderFile = "encoder.onnx";
    public const string EncoderDataFile = "encoder_nosplit.onnx.data"; // encoder.onnx references this name internally
    public const string DecoderFile = "decoder.onnx";
    public const string JointFile = "joint.onnx";
    public const string VocabFile = "vocab.json";
    public const string LanguagesFile = "languages.json"; // locale→prompt-slot map from the export (not required to run)
    public const string ModelFolder = "nemotron-3.5-asr-streaming-0.6b-onnx-fp16";

    private readonly string? _explicitDir;
    private readonly ISettingsStore? _settings;

    public NemotronFp16Model(string? directory = null, ISettingsStore? settings = null)
    {
        _explicitDir = directory;
        _settings = settings;
    }

    /// <summary>Where the model lives: an explicit override (dev hooks) wins; otherwise it sits under the
    /// user's chosen data folder (Settings/wizard), falling back to the default models dir — same
    /// resolution as the int4 <see cref="NemotronModel"/> so a moved data folder carries BOTH models.</summary>
    public string Directory => _explicitDir ?? Path.Combine(
        _settings is not null ? JotPaths.ModelsDir(_settings.Current) : JotPaths.DefaultModelsDir,
        ModelFolder);

    public string Encoder => Path.Combine(Directory, EncoderFile);
    public string EncoderData => Path.Combine(Directory, EncoderDataFile);
    public string Decoder => Path.Combine(Directory, DecoderFile);
    public string Joint => Path.Combine(Directory, JointFile);
    public string Vocab => Path.Combine(Directory, VocabFile);
    public string Languages => Path.Combine(Directory, LanguagesFile);

    /// <summary>True when every required asset (graphs + external weights + vocab) is present.
    /// languages.json is deliberately NOT required — the engine runs without it, and gating install
    /// completeness on a metadata file would strand pre-existing manual installs.</summary>
    public bool IsInstalled =>
        File.Exists(Encoder) && File.Exists(EncoderData) &&
        File.Exists(Decoder) && File.Exists(Decoder + ".data") &&
        File.Exists(Joint) && File.Exists(Joint + ".data") &&
        File.Exists(Vocab);
}
