using System.IO;
using System.Text.Json;

namespace Jot.Transcription.Ggml;

/// <summary>
/// Where official transcribe.cpp natives live. Never the Handy install — that DLL is the same
/// version string and a different binary (Round 2). Product code must not load it.
/// </summary>
internal static class GgmlNativeLocator
{
    internal const string DllFileName = "transcribe.dll";
    internal const string EnvVar = "JOT_GGML_NATIVE";

    internal static string HandyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Handy");

    internal static bool IsForbidden(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        string full;
        try { full = Path.GetFullPath(dir); }
        catch { return false; }
        string handy;
        try { handy = Path.GetFullPath(HandyDir); }
        catch { return false; }
        return full.Equals(handy, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(handy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(handy + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool LooksPresent(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, DllFileName));

    /// <summary>First existing <c>transcribe.dll</c> that is not under Handy, or null.</summary>
    internal static string? TryResolve(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        foreach (string? candidate in Candidates(env))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }
            if (IsForbidden(full)) continue;
            if (LooksPresent(full)) return full;
        }
        return null;
    }

    internal static bool IsPresent(Func<string, string?>? env = null) => TryResolve(env) is not null;

    internal static IEnumerable<string?> Candidates(Func<string, string?> env)
    {
        yield return env(EnvVar);
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "natives");
        yield return Path.Combine(baseDir, "ggml");
    }

    internal static (string? Version, string? HeaderHash) ReadContract(string nativeDir)
    {
        string path = Path.Combine(nativeDir, "contract.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            string? version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            string? hash = doc.RootElement.TryGetProperty("header_hash", out var h) ? h.GetString() : null;
            return (version, hash);
        }
        catch (Exception ex)
        {
            throw new TranscribeAbiException(
                $"transcribe.cpp contract.json at {path} could not be read: {ex.Message}");
        }
    }
}
