using System.IO;
using System.Text.Json;
using Jot.Transcription.Ggml;

namespace Jot.Services;

/// <summary>
/// Deletes leftover int4 / fp16 ONNX model folders AFTER the Q8_0 GGUF is on disk and ggml has
/// actually loaded. Crash-safe: a marker in the config root records the in-flight cleanup so a
/// kill mid-delete resumes on the next launch. The GGUF is never touched.
///
/// The ONNX engine is gone (K3 accepted: Q8_0 beats int4 at 4+ threads; the 1–2 thread tail is
/// recoverable from git). This only removes files. A user whose GGUF is not yet verified keeps
/// the leftover folders — they are not an engine, but they must not be deleted first.
///
/// Folder names are constants here so this does not depend on the deleted locators.
/// </summary>
public static class OrtModelCleanup
{
    public const string MarkerFile = "ort-cleanup.json";

    /// <summary>Historical int4 ONNX folder. Kept so leftover installs are found after the locator is gone.</summary>
    public const string LeftoverInt4Folder = "nemotron-3.5-asr-streaming-0.6b-onnx-int4";

    /// <summary>Historical fp16 ONNX folder. Same reason as <see cref="LeftoverInt4Folder"/>.</summary>
    public const string LeftoverFp16Folder = "nemotron-3.5-asr-streaming-0.6b-onnx-fp16";

    internal sealed record Marker(string GgufPath, long GgufBytes, string Int4Dir, string Fp16Dir);

    public static bool HasPending(string configDir) => File.Exists(Path.Combine(configDir, MarkerFile));

    /// <summary>Arm cleanup. Idempotent. No-op when the GGUF is missing — never delete the only engine.</summary>
    public static void Request(string configDir, NemotronGgufModel gguf, string modelsParent)
    {
        if (!gguf.IsInstalled) return;
        long size;
        try { size = new FileInfo(gguf.ModelPath).Length; }
        catch { return; }
        if (size <= 0) return;

        Directory.CreateDirectory(configDir);
        var marker = new Marker(
            gguf.ModelPath, size,
            Path.Combine(modelsParent, LeftoverInt4Folder),
            Path.Combine(modelsParent, LeftoverFp16Folder));
        File.WriteAllText(Path.Combine(configDir, MarkerFile), JsonSerializer.Serialize(marker));
        Run(configDir, marker);
    }

    /// <summary>Resume a cleanup interrupted by a crash/kill. No-op when no marker or GGUF vanished.</summary>
    public static void ResumePending(string configDir)
    {
        Marker? m = Read(configDir);
        if (m is null) return;
        JotLog.Info($"resuming ONNX model cleanup after GGUF ready ({m.GgufPath})");
        Run(configDir, m);
    }

    internal static void Run(string configDir, Marker m)
    {
        if (!GgufStillIntact(m))
        {
            JotLog.Info("ort-cleanup: GGUF missing or size changed — leaving ONNX models in place");
            return;
        }

        bool int4Gone = RemoveTree(m.Int4Dir, "int4");
        bool fp16Gone = RemoveTree(m.Fp16Dir, "fp16");
        if (int4Gone && fp16Gone) Clear(configDir);
    }

    internal static Marker? Read(string configDir)
    {
        string path = Path.Combine(configDir, MarkerFile);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    internal static void Clear(string configDir)
    {
        try
        {
            string path = Path.Combine(configDir, MarkerFile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* leftover marker just retries next launch */ }
    }

    internal static bool GgufStillIntact(Marker m)
    {
        try
        {
            return File.Exists(m.GgufPath) && new FileInfo(m.GgufPath).Length == m.GgufBytes && m.GgufBytes > 0;
        }
        catch { return false; }
    }

    private static bool RemoveTree(string dir, string label)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        if (!Directory.Exists(dir)) return true;
        try
        {
            Directory.Delete(dir, recursive: true);
            JotLog.Info($"ort-cleanup: removed {label} model at {dir}");
            return true;
        }
        catch (Exception ex)
        {
            // Sharing violation while something still has files mapped — leave the marker.
            JotLog.Info($"ort-cleanup: could not remove {label} at {dir}: {ex.Message}");
            return false;
        }
    }
}
