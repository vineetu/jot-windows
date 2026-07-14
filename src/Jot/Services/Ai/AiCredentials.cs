using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jot.Services.Ai;

/// <summary>
/// Holds the AI provider API key, persisted encrypted at rest with Windows DPAPI (per-user, per-
/// machine) so cleanup/rewrite keep working across restarts. Previously in-memory only, which made a
/// configured provider silently stop working after relaunch.
/// </summary>
public sealed class AiCredentials
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot", "aikey.dat");

    private string? _apiKey;

    public AiCredentials() => _apiKey = Load();

    public string? ApiKey
    {
        get => _apiKey;
        set { _apiKey = value; Save(value); }
    }

    private static string? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; } // corrupt / different user — treat as unset
    }

    private static void Save(string? value)
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
