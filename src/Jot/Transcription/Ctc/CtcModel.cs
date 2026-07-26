using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.Transcription.Ctc;

/// <summary>
/// Locates the OPTIONAL Parakeet CTC 110M keyword-spotter assets — the second, small model the
/// vocabulary feature runs post-stop purely to find where a term was spoken. Same layout convention as
/// <see cref="Nemotron.NemotronModel"/>: a folder under the user's <c>models</c> directory, downloaded
/// only when vocabulary is switched on, so nobody pays ~132 MB for a feature they never enable.
///
/// THREE files, and all three are load-bearing:
///   * <c>model.int8.onnx</c>  — the graph (mel-in, log-probs-out; the front-end is ours, not its).
///   * <c>tokens.txt</c>       — the id space and, critically, the BLANK id. Read, never assumed.
///   * <c>tokenizer.model</c>  — SentencePiece BPE, the term→ids direction. NOT in the sherpa-onnx
///     archive; it was extracted from the 459 MB .nemo checkpoint. Without it the spotter can decode
///     and cannot encode, i.e. it can never look for anything.
/// </summary>
public sealed class CtcModel
{
    public const string ModelFile = "model.int8.onnx";
    public const string TokensFile = "tokens.txt";
    public const string TokenizerFile = "tokenizer.model";
    public const string ModelFolder = "parakeet-ctc-110m-onnx-int8";

    private readonly string? _explicitDir;
    private readonly ISettingsStore? _settings;

    public CtcModel(string? directory = null, ISettingsStore? settings = null)
    {
        _explicitDir = directory;
        _settings = settings;
    }

    /// <summary>Resolved on each access, so a data folder chosen (or moved) after construction is
    /// honoured — the same rule the Nemotron locator follows.</summary>
    public string Directory => _explicitDir ?? Path.Combine(
        _settings is not null ? JotPaths.ModelsDir(_settings.Current) : JotPaths.DefaultModelsDir,
        ModelFolder);

    public string Graph => Path.Combine(Directory, ModelFile);
    public string Tokens => Path.Combine(Directory, TokensFile);
    public string Tokenizer => Path.Combine(Directory, TokenizerFile);

    /// <summary>True only when ALL three assets are present. A partial install must read as
    /// not-installed: the spotter's contract is "no model ⇒ no spotter, no error", and a missing
    /// tokenizer would otherwise surface as a term list that silently never matches.</summary>
    public bool IsInstalled =>
        File.Exists(Graph) && File.Exists(Tokens) && File.Exists(Tokenizer);
}
