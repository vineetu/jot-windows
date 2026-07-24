using System.IO;
using System.Text;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Builds the plain-text diagnostics report the feedback window shows and sends. Deliberately lean and
/// fully user-visible: the user's note, app/OS/hardware facts, the engine decision state, and the tail
/// of the activity log. NOTHING personal by construction — dictated text is redacted from log lines and
/// the Windows username is scrubbed from paths — and the window shows the exact text before anything
/// leaves the machine (seeing it IS the consent).
/// </summary>
public static class FeedbackReport
{
    public const string SubjectTag = "[Jot Windows test]"; // the "this is the Windows test build" marker

    /// <summary>Log lines included in the report — enough to reconstruct a session's decisions
    /// (engine choice, probe verdict, warm-up, stop timings) without shipping the whole file.</summary>
    public const int LogTailLines = 120;

    /// <summary>Hard size cap for the whole report: the feedback API takes one message string and its
    /// limits are unpublished — stay comfortably small. Oldest tail lines get trimmed first.</summary>
    public const int MaxChars = 6_000;

    public static string Build(JotSettings s, string? userNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{SubjectTag} feedback — {DateTime.Now:yyyy-MM-dd HH:mm} (local)");
        if (!string.IsNullOrWhiteSpace(userNote))
        {
            sb.AppendLine();
            sb.AppendLine("What happened (user's words):");
            sb.AppendLine(userNote.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("-- app / machine --");
        sb.AppendLine($"app: {typeof(FeedbackReport).Assembly.GetName().Version}  " +
                      $"flavor: {BuildFlavor.Name}  packaged: {PackagePaths.IsPackaged}");
        sb.AppendLine($"os: {Environment.OSVersion.VersionString}  64-bit: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"cpu: {Environment.ProcessorCount} logical cores  " +
                      $"ram: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):0.0} GB");
        var gpu = Platform.GpuInfo.TryGetPrimaryAdapter();
        sb.AppendLine(gpu is null
            ? "gpu: (not detected)"
            : $"gpu: {gpu.Description}  vram: {gpu.DedicatedVideoMemoryBytes / (1024.0 * 1024 * 1024):0.#} GB  " +
              $"driver: {Platform.GpuInfo.FormatDriverVersion(gpu.UmdDriverVersion)}  software: {gpu.IsSoftwareAdapter}");

        sb.AppendLine();
        sb.AppendLine("-- engine --");
        sb.AppendLine($"device: {s.TranscriptionDevice}  probeVerdict: {s.GpuProbeVerdict ?? "none"}  " +
                      $"probeAvgChunkMs: {s.GpuProbeAvgChunkMs:0}");
        sb.AppendLine($"probeReason: {s.GpuProbeReason ?? "-"}");
        sb.AppendLine($"language: {s.Language}  liveCaptions: {s.LiveCaptions}  pasteMethod: {s.PasteMethod}");

        sb.AppendLine();
        sb.AppendLine($"-- recent activity log (last {LogTailLines} lines; transcripts redacted, username scrubbed) --");
        var tail = TailOfLog().Select(l => Scrub(l, Environment.UserName)).ToList();
        // Trim oldest-first until the whole report fits the API-safe cap; newest lines matter most.
        int budget = MaxChars - sb.Length;
        while (tail.Count > 1 && tail.Sum(l => l.Length + 1) > budget)
            tail.RemoveAt(0);
        if (tail.Count > 0 && sb.Length < MaxChars)
        {
            if (TailWasTrimmed()) sb.AppendLine("(older lines trimmed to fit)");
            foreach (string line in tail) sb.AppendLine(line);
        }
        return sb.ToString();

        bool TailWasTrimmed() => tail.Count < LogTailLines;
    }

    private static IEnumerable<string> TailOfLog()
    {
        try
        {
            string path = JotLog.LogFilePath;
            if (!File.Exists(path)) return ["(no log file)"];
            return File.ReadLines(path).TakeLast(LogTailLines); // file is rotation-capped at 256 KB — cheap
        }
        catch (Exception ex)
        {
            return [$"(log unreadable: {ex.Message})"];
        }
    }

    /// <summary>Redacts what must never leave the machine: the quoted dictated text in SAVED lines
    /// (replaced by its length) and the Windows account name inside paths.</summary>
    public static string Scrub(string line, string? username)
    {
        string result = line;

        // SAVED: "the user's actual words…" (17 chars) → SAVED: "[redacted 17 chars]" (17 chars)
        int marker = result.IndexOf("SAVED: \"", StringComparison.Ordinal);
        if (marker >= 0)
        {
            int open = marker + "SAVED: ".Length;             // points at the opening quote
            int close = result.LastIndexOf('"');
            if (close > open)
                result = result[..(open + 1)] + $"[redacted {close - open - 1} chars]" + result[close..];
        }

        if (!string.IsNullOrEmpty(username))
        {
            result = result.Replace($"\\{username}\\", "\\<user>\\", StringComparison.OrdinalIgnoreCase);
            result = result.Replace($"\\{username}", "\\<user>", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }
}
