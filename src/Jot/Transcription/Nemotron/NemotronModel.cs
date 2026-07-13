using System.IO;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Locates the on-device Nemotron 3.5 ASR streaming multilingual (int4 ONNX) assets. They live under
/// <c>%LOCALAPPDATA%\Jot\models\nemotron-3.5-asr-streaming-0.6b-onnx-int4</c> and are downloaded on
/// first run (≈0.67 GB). The int4 build runs driver-free on CPU (the encoder can also go to DirectML).
/// External weights (<c>*.onnx.data</c>) sit next to each graph and load automatically.
/// </summary>
public sealed class NemotronModel
{
    public const string EncoderFile = "encoder.onnx";
    public const string DecoderFile = "decoder.onnx";
    public const string JointFile = "joint.onnx";
    public const string VocabFile = "vocab.txt";

    public NemotronModel(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jot", "models", "nemotron-3.5-asr-streaming-0.6b-onnx-int4");
    }

    public string Directory { get; }

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
