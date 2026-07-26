namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/Seams.swift` (pinned commit 5326460).
// The four platform-injection seams the shared vocabulary core exposes. Everything
// platform-specific — the engine's rescore output, common-word list loading, logging, and the
// storage directory — crosses one of these so the gate stays pure, portable, and testable.

/// <summary>One proposed replacement. Engine-neutral: whatever produces candidates (a CTC
/// spotter, a rescorer) maps its output into this at the call site, so the gate never depends
/// on the ASR engine.</summary>
public readonly record struct RescoreProposal(
    string OriginalWord,
    string? ReplacementWord,
    bool ShouldReplace,
    float? ReplacementScore,
    float OriginalScore);

/// <summary>The rescorer's full output.</summary>
public readonly record struct RescoreOutput(
    string Text,
    IReadOnlyList<RescoreProposal> Replacements,
    bool WasModified);

/// <summary>Per-word/subword timing the gate reads to protect confident words. Deliberately
/// carries <c>confidence</c> and NOT start/end times — the gate never reads the times.</summary>
public readonly record struct TokenTiming(string Token, float Confidence);

/// <summary>A user-confirmed correction. <c>Net</c> ≥ 1 = confirmed, ≤ -1 = demoted (the user
/// reverted it, so stop auto-applying). <c>AlwaysReplace</c> is the explicit "always replace X
/// with Y" grant — the only path that auto-applies over a common word.</summary>
public readonly record struct OverrideEntry(
    string OriginalWord,
    string Term,
    int Net,
    bool AlwaysReplace);

/// <summary>Seam 2 · loads the high-frequency word set for a language's resource name.</summary>
public interface ICommonWordsProvider
{
    /// <summary>Everyday-word set for <paramref name="resource"/>. A null resource means "no
    /// list ships for this language" and yields an empty set — the guard then degrades to
    /// plausibility/confidence, which is still safe.</summary>
    IReadOnlySet<string> Words(string? resource);
}

/// <summary>Seam 3 · diagnostic categories the gate emits.</summary>
public enum DiagnosticsCategory
{
    /// <summary>Gate decision trace + provenance commits.</summary>
    VocabularyGate,
    /// <summary>A vocab-data persistence refusal.</summary>
    VocabularySaveFailed,
}

/// <summary>Seam 3 · where the gate's decision trace goes. Tests capture into a list.</summary>
public interface IDiagnosticsSink
{
    void Record(DiagnosticsCategory category, string message, IReadOnlyDictionary<string, string> metadata);
}

/// <summary>Discards everything — the default for callers that don't wire diagnostics.</summary>
public sealed class NoopDiagnosticsSink : IDiagnosticsSink
{
    public static readonly NoopDiagnosticsSink Instance = new();
    public void Record(DiagnosticsCategory category, string message, IReadOnlyDictionary<string, string> metadata) { }
}
