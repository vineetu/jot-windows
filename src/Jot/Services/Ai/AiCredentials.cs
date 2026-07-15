using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jot.Services.Abstractions;

namespace Jot.Services.Ai;

/// <summary>
/// Holds the AI provider API key, persisted encrypted at rest with Windows DPAPI (per-user, per-
/// machine) so cleanup/rewrite keep working across restarts. Previously in-memory only, which made a
/// configured provider silently stop working after relaunch. The file lives under the user's chosen
/// data folder (<c>&lt;DataDir&gt;\aikey.dat</c>) so nothing lands in %LOCALAPPDATA% (worklist D5).
/// </summary>
public sealed class AiCredentials
{
    private readonly ISettingsStore _settings;

    // Resolved from the current data directory each access, so it follows a changed save location.
    private string FilePath => Path.Combine(JotPaths.DataDir(_settings.Current), "aikey.dat");

    private string? _apiKey;

    public AiCredentials(ISettingsStore settings)
    {
        _settings = settings;
        _apiKey = Load();
    }

    public string? ApiKey
    {
        get => _apiKey;
        set { _apiKey = value; Save(value); }
    }

    private string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; } // corrupt / different user — treat as unset
    }

    private void Save(string? value)
    {
        try
        {
            if (string.IsNullOrEmpty(value))
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
        }
        catch { /* best effort — never crash on a key write */ }
    }
}
