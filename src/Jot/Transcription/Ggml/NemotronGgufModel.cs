using System.IO;
using Jot.Services;
using Jot.Services.Abstractions;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Locates the on-device Nemotron 3.5 Q8_0 GGUF. Same resolution rule as the CTC locator:
/// an explicit directory (dev hooks) wins, otherwise <c>&lt;data&gt;\models\nemotron-3.5-asr-streaming-0.6b-Q8_0</c>.
/// <c>JOT_GGML_MODEL</c> overrides both (file or folder). The installer is
/// <see cref="NemotronGgufModelInstaller"/>; a missing file is "not installed", never a crash.
/// </summary>
public sealed class NemotronGgufModel
{
    public const string FileName = "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf";
    public const string ModelFolder = "nemotron-3.5-asr-streaming-0.6b-Q8_0";
    public const string PathEnvVar = "JOT_GGML_MODEL";

    private readonly string? _explicitDir;
    private readonly ISettingsStore? _settings;
    private readonly Func<string, string?> _env;

    public NemotronGgufModel(
        string? directory = null,
        ISettingsStore? settings = null,
        Func<string, string?>? env = null)
    {
        _explicitDir = directory;
        _settings = settings;
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Folder the GGUF is expected to sit in (env override that points at a file reports the file's directory).</summary>
    public string Directory
    {
        get
        {
            if (TryEnv(out string? envPath, out bool envIsFile))
                return envIsFile ? Path.GetDirectoryName(envPath)! : envPath!;
            return _explicitDir ?? Path.Combine(
                _settings is not null ? JotPaths.ModelsDir(_settings.Current) : JotPaths.DefaultModelsDir,
                ModelFolder);
        }
    }

    public string ModelPath
    {
        get
        {
            if (TryEnv(out string? envPath, out bool envIsFile))
                return envIsFile ? envPath! : Path.Combine(envPath!, FileName);
            return Path.Combine(Directory, FileName);
        }
    }

    public bool IsInstalled
    {
        get
        {
            try { return File.Exists(ModelPath); }
            catch { return false; }
        }
    }

    private bool TryEnv(out string? path, out bool isFile)
    {
        path = _env(PathEnvVar);
        isFile = false;
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full)) { path = full; isFile = true; return true; }
            path = full;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
