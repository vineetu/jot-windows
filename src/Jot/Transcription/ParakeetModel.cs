using System.IO;

namespace Jot.Transcription;

/// <summary>
/// Locates the on-device Parakeet-TDT-0.6B (int8) model assets. They live under
/// <c>%LOCALAPPDATA%\Jot\models\parakeet-tdt-0.6b-v2</c> and are downloaded on first run
/// rather than bundled, so the installer stays small (the encoder alone is ~600 MB).
/// int8 is the shipping default: realtime on any modern CPU with no GPU/driver dependency.
/// </summary>
public sealed class ParakeetModel
{
    public const string PreprocessorFile = "nemo128.onnx";              // log-mel feature extractor
    public const string EncoderFile = "encoder-model.int8.onnx";        // Conformer encoder
    public const string DecoderJointFile = "decoder_joint-model.int8.onnx"; // prediction net + joint
    public const string VocabFile = "vocab.txt";

    public ParakeetModel(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jot", "models", "parakeet-tdt-0.6b-v2");
    }

    public string Directory { get; }

    public string Preprocessor => Path.Combine(Directory, PreprocessorFile);
    public string Encoder => Path.Combine(Directory, EncoderFile);
    public string DecoderJoint => Path.Combine(Directory, DecoderJointFile);
    public string Vocab => Path.Combine(Directory, VocabFile);

    /// <summary>True when every required asset is present on disk.</summary>
    public bool IsInstalled =>
        File.Exists(Preprocessor) &&
        File.Exists(Encoder) &&
        File.Exists(DecoderJoint) &&
        File.Exists(Vocab);
}
