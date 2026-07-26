using System.Collections.Concurrent;
using System.IO;
using System.Reflection;

namespace Jot.Vocabulary;

// Seam 2 implementation. Port of jot-shared `BundledCommonWordsProvider` (pinned commit 5326460),
// with the bundle swapped for embedded assembly resources.

/// <summary>
/// The everyday-word sets behind the gate's over-correction brake, one per language, loaded lazily
/// from embedded resources and cached. Only the language(s) actually dictated in are ever read.
/// </summary>
public sealed class EmbeddedCommonWordsProvider : ICommonWordsProvider
{
    /// <summary>The languages a list ships for. The gate still runs without one — it just loses
    /// the common-word brake and leans on plausibility + confidence.</summary>
    public static readonly IReadOnlySet<string> SupportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "en", "bg", "cs", "da", "de", "el", "es", "fi", "fr", "hu",
        "it", "nl", "pl", "pt", "ro", "ru", "sk", "sl", "sr", "sv", "uk",
    };

    private const string ResourcePrefix = "Jot.Vocabulary.Resources.";

    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _cache = new();
    private readonly IDiagnosticsSink _diagnostics;
    private readonly HashSet<string> _reported = [];
    private readonly Lock _reportLock = new();

    public static readonly EmbeddedCommonWordsProvider Shared = new(NoopDiagnosticsSink.Instance);

    public EmbeddedCommonWordsProvider(IDiagnosticsSink? diagnostics = null)
        => _diagnostics = diagnostics ?? NoopDiagnosticsSink.Instance;

    /// <summary>Resource name for a locale ("es-ES" → "common-words-es", "en-US" →
    /// "common-words"), or null when no list ships for it — which the caller must treat as
    /// "brake off for this language", not as an error.</summary>
    public static string? ResourceFor(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        string lang = locale.Split('-', '_')[0].ToLowerInvariant();
        if (lang == "en") return "common-words";
        return SupportedLanguages.Contains(lang) ? $"common-words-{lang}" : null;
    }

    public IReadOnlySet<string> Words(string? resource)
    {
        if (resource is null) return new HashSet<string>();   // intentional "no list" — silent
        return _cache.GetOrAdd(resource, Load);
    }

    private IReadOnlySet<string> Load(string resource)
    {
        // FAIL LOUDLY. A null resource above is an intentional "no list ships for this language".
        // A NAMED resource that won't load is a BUG: it silently disables the common-word brake —
        // the exact over-correction class this subsystem exists to prevent. Report it once so the
        // miss is visible instead of invisible.
        try
        {
            Assembly asm = typeof(EmbeddedCommonWordsProvider).Assembly;
            using Stream? stream = asm.GetManifestResourceStream(ResourcePrefix + resource + ".txt");
            if (stream is null)
            {
                ReportMissing(resource, "resource not found in assembly");
                return new HashSet<string>();
            }
            // INTENTIONAL DEVIATION, and the brake depends on it. Swift splits on "\n" only,
            // which is fine for its copy — the lists are stored LF in git. Ours arrive CRLF on
            // disk (autocrlf restores CRLF on a Windows checkout), so a literal port of
            // `split("\n")` would build a set of "name\r" entries: every lookup misses and the
            // common-word guard goes silently dead — the exact over-correction failure this
            // subsystem exists to prevent. `ReadLine` is line-ending-agnostic and yields the same
            // set from either form. Do not "restore fidelity" here.
            using var reader = new StreamReader(stream);
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0) set.Add(CorrectionKey.Lowercased(line));
            }
            return set;
        }
        catch (Exception ex)
        {
            ReportMissing(resource, ex.Message);
            return new HashSet<string>();
        }
    }

    private void ReportMissing(string resource, string reason)
    {
        lock (_reportLock)
        {
            if (!_reported.Add(resource)) return;
        }
        _diagnostics.Record(
            DiagnosticsCategory.VocabularyGate,
            $"common-words resource '{resource}' missing/unreadable — common-word guard DISABLED for it",
            new Dictionary<string, string> { ["resource"] = resource, ["reason"] = reason });
    }
}
