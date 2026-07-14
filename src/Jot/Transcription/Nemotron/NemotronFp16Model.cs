using System.IO;
using Jot.Services;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Locates the on-device Nemotron 3.5 ASR streaming multilingual (FP16 ONNX) assets — the GPU build.
/// They live under <c>&lt;data&gt;\models\nemotron-3.5-asr-streaming-0.6b-onnx-fp16</c>. Unlike the int4
/// build (CPU-only; int4 can't run on DirectML) the FP16 graphs initialise and run correctly on
/// DirectML, so this model backs the "GPU" transcription device.
///
/// The staged encoder is the DirectML-safe <c>encoder_nosplit.onnx</c> (24 GLU channel-Split nodes
/// rewritten as Slice ops — the stock encoder gives an empty transcript on DML). It was copied to
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

    public NemotronFp16Model(string? directory = null)
    {
        // Under the data folder (a roomy non-system drive by default) so the ~1.3 GB model stays off C:.
        Directory = directory ?? Path.Combine(
            JotPaths.DefaultModelsDir, "nemotron-3.5-asr-streaming-0.6b-onnx-fp16");
    }

    public string Directory { get; }

    public string Encoder => Path.Combine(Directory, EncoderFile);
    public string EncoderData => Path.Combine(Directory, EncoderDataFile);
    public string Decoder => Path.Combine(Directory, DecoderFile);
    public string Joint => Path.Combine(Directory, JointFile);
    public string Vocab => Path.Combine(Directory, VocabFile);

    /// <summary>True when every required asset (graphs + external weights + vocab) is present.</summary>
    public bool IsInstalled =>
        File.Exists(Encoder) && File.Exists(EncoderData) &&
        File.Exists(Decoder) && File.Exists(Decoder + ".data") &&
        File.Exists(Joint) && File.Exists(Joint + ".data") &&
        File.Exists(Vocab);
}
