using System.IO;

namespace Jot.Services;

/// <summary>
/// Jot's single activity/diagnostics log. Everything — startup, dictation stages, cleanup, errors,
/// crashes — funnels here so "View log" shows one real, chronological record instead of just a crash
/// dump (worklist D4). The file lives under the user's chosen data folder (<c>&lt;DataDir&gt;\logs\jot.log</c>),
/// so nothing scatters into %LOCALAPPDATA% regardless of where the user points their data (worklist D5).
///
/// Static + best-effort by design: a logging failure must never break a dictation. Before
/// <see cref="Initialize"/> runs (or if the provider throws) it falls back to %LOCALAPPDATA%\Jot\logs.
/// </summary>
public static class JotLog
{
    private static Func<string>? _dataDirProvider;
    private static readonly object _gate = new();
    private const long MaxBytes = 2 * 1024 * 1024; // roll at ~2 MB so the log can't grow unbounded

    /// <summary>Wire the log to the current data directory. Call once at startup.</summary>
    public static void Initialize(Func<string> dataDirProvider) => _dataDirProvider = dataDirProvider;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    /// <summary>Absolute path to the active log file (its folder is created on access).</summary>
    public static string LogFilePath => Path.Combine(LogsDir(), "jot.log");

    private static string LogsDir()
    {
        string baseDir;
        try
        {
            baseDir = _dataDirProvider?.Invoke() is { Length: > 0 } d
                ? d
                : DefaultBase();
        }
        catch { baseDir = DefaultBase(); }

        string logs = Path.Combine(baseDir, "logs");
        try { Directory.CreateDirectory(logs); } catch { /* View Log just won't find the file */ }
        return logs;
    }

    private static string DefaultBase() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");

    private static void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                string path = LogFilePath;
                Roll(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level,-5}  {message}\n");
            }
        }
        catch { /* logging is best-effort — never throw into the caller */ }
    }

    // Keep one previous generation (jot.log.1) so the log stays bounded but recent history survives.
    private static void Roll(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes)
            {
                string prev = path + ".1";
                if (File.Exists(prev)) File.Delete(prev);
                File.Move(path, prev);
            }
        }
        catch { /* if rolling fails, just keep appending */ }
    }
}
