using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Locates the on-device Nemotron 3.5 ASR streaming multilingual (int4 ONNX) assets. They live under the
/// <c>models</c> subfolder of the user's data folder (default <c>%LOCALAPPDATA%\Jot\models</c>) and are
/// downloaded on first run (≈0.75 GB). The int4 build runs driver-free on CPU (the encoder can also go to
/// DirectML). External weights (<c>*.onnx.data</c>) sit next to each graph and load automatically.
/// </summary>
public sealed class NemotronModel
{
    public const string EncoderFile = "encoder.onnx";
    public const string DecoderFile = "decoder.onnx";
    public const string JointFile = "joint.onnx";
    public const string VocabFile = "vocab.txt";
    public const string ModelFolder = "nemotron-3.5-asr-streaming-0.6b-onnx-int4";

    private readonly string? _explicitDir;
    private readonly ISettingsStore? _settings;

    public NemotronModel(string? directory = null, ISettingsStore? settings = null)
    {
        _explicitDir = directory;
        _settings = settings;
    }

    /// <summary>Where the model lives: an explicit override (dev hooks) wins; otherwise it sits under the
    /// user's chosen data folder (Settings/wizard), falling back to <c>%LOCALAPPDATA%\Jot\models</c>. Resolved
    /// on each access so a folder picked during setup is honoured when the download runs.</summary>
    public string Directory => _explicitDir ?? Path.Combine(
        _settings is not null ? JotPaths.ModelsDir(_settings.Current) : JotPaths.DefaultModelsDir,
        ModelFolder);

    public string Encoder => Path.Combine(Directory, EncoderFile);
    public string Decoder => Path.Combine(Directory, DecoderFile);
    public string Joint => Path.Combine(Directory, JointFile);
    public string Vocab => Path.Combine(Directory, VocabFile);

    /// <summary>True when every required asset (graphs + external weights + vocab) is present.</summary>
    public bool IsInstalled =>
        File.Exists(Encoder) && File.Exists(Encoder + ".data") &&
        File.Exists(Decoder) && File.Exists(Decoder + ".data") &&
        File.Exists(Joint) && File.Exists(Joint + ".data") &&
        File.Exists(Vocab);
}
