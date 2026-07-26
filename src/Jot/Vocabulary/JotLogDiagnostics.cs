using Jot.Services;

namespace Jot.Vocabulary;

/// <summary>
/// Seam 3 · the shared vocabulary core's diagnostics, routed onto <see cref="JotLog"/>.
///
/// Without this the gate's per-decision trace goes to <see cref="NoopDiagnosticsSink"/> and the
/// calibration pass the feature cannot unhide without ("log every APPLY/BLOCK/OVERRIDE verdict over
/// ~20–30 real dictations, then eyeball it") has nothing to read.
/// </summary>
public sealed class JotLogDiagnosticsSink : IDiagnosticsSink
{
    public static readonly JotLogDiagnosticsSink Instance = new();

    public void Record(DiagnosticsCategory category, string message, IReadOnlyDictionary<string, string> metadata)
    {
        string meta = metadata.Count == 0
            ? ""
            : " " + string.Join(" ", metadata.Select(kv => $"{kv.Key}={kv.Value}"));
        string line = $"vocab[{category}] {message}{meta}";

        // A save refusal is a data-loss-shaped event and must not read as routine trace.
        if (category == DiagnosticsCategory.VocabularySaveFailed) JotLog.Warn(line);
        else JotLog.Info(line);
    }
}
