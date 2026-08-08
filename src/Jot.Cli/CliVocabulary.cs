using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jot.Vocabulary;

namespace Jot.Cli;

/// <summary>
/// Custom-vocabulary correction, model-free. The gating rule is deliberately NOT
/// <c>VocabularyRunner.ModeFor</c>: that routes English to the 283 MB acoustic spotter, which the CLI
/// does not ship, so "ModeFor + no spotter" would turn vocabulary off in the default language. The rule
/// here is "the textual corrector is measured safe in this locale AND a common-word list ships for it".
///
/// DOCUMENTED DIVERGENCE from the app: in English the CLI runs the textual corrector where the app runs
/// the acoustic spotter — same term file, same gate, different detector, so recall differs slightly.
///
/// Nothing here writes: no learn ledger, no provenance, no logs.
/// </summary>
internal sealed class CliVocabulary
{
    private readonly IReadOnlyList<VocabularyTerm> _terms;
    private readonly IReadOnlyList<OverrideEntry> _overrides;
    private readonly string _locale;
    private readonly VocabularyCorrector _corrector = new();

    private CliVocabulary(
        IReadOnlyList<VocabularyTerm> terms, IReadOnlyList<OverrideEntry> overrides, string locale)
    {
        _terms = terms;
        _overrides = overrides;
        _locale = locale;
    }

    /// <summary>Null = vocabulary does not run (off, unsupported locale, or no terms) — never an error.
    /// A non-null <paramref name="error"/> is a runtime failure (exit 1).</summary>
    public static CliVocabulary? Create(
        ResolvedPaths paths, string canonicalLocale, bool noVocab, string? explicitFile, out string? error)
    {
        error = null;
        if (noVocab) return null;
        // Asking for a specific term file IS asking for vocabulary, so it overrides the app's master
        // toggle; otherwise the toggle (default off) decides.
        if (!paths.Settings.VocabularyEnabled && explicitFile is null) return null;

        // auto-detect is excluded by TextualShips: the engine throws the resolved locale away, so there
        // is no language to gate the corrector's per-language ceiling on.
        if (!VocabularyLimits.TextualShips(canonicalLocale)) return null;
        if (EmbeddedCommonWordsProvider.ResourceFor(canonicalLocale) is null) return null;

        IReadOnlyList<VocabularyTerm> terms;
        if (explicitFile is not null)
        {
            if (!TryReadTermFile(explicitFile, out terms, out string? reason))
            {
                error = $"could not read vocabulary file {explicitFile}: {reason}";
                return null;
            }
        }
        else
        {
            // Safe to construct: the ctor loads and Save is guarded. An absent default file is simply
            // an empty list — vocabulary off, silently.
            terms = [.. new VocabularyStore(paths.DataRoot, NoopDiagnosticsSink.Instance).Terms];
        }
        if (terms.Count == 0) return null;

        return new CliVocabulary(terms, ReadOverrides(paths), canonicalLocale);
    }

    public string Apply(string text, double durationSeconds)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        IReadOnlyList<VocabularyGate.Detection> detections =
            _corrector.Spot(text, _terms, durationSeconds, _locale);
        if (detections.Count == 0) return text;

        return VocabularyGate.ApplyFromDetections(
            text,
            VocabularyRunner.Enrich(detections, _terms),
            durationSeconds,
            EmbeddedCommonWordsProvider.Shared,
            EmbeddedCommonWordsProvider.ResourceFor(_locale),
            _overrides,
            NoopDiagnosticsSink.Instance).Text;
    }

    // CorrectionStore must NOT be constructed — its FilePath() runs Directory.CreateDirectory on the
    // READ path, and the CLI writes no app state. Shared record types + shared options, so a schema
    // change can't silently mean something different here.
    private static IReadOnlyList<OverrideEntry> ReadOverrides(ResolvedPaths paths)
    {
        string file = Path.Combine(paths.VocabularyDir, "corrections.json");
        try
        {
            if (!File.Exists(file)) return [];
            var decoded = JsonSerializer.Deserialize<Dictionary<string, CorrectionStore.Term>>(
                File.ReadAllBytes(file), CorrectionStore.Json);
            if (decoded is null) return [];
            return decoded.Values
                .SelectMany(t => t.Mappings)
                .Select(m => new OverrideEntry(m.OriginalWord, m.Term, m.Net, m.AlwaysReplace))
                .ToList();
        }
        catch (Exception ex)
        {
            // Fail soft: learned overrides are an enhancement, and a schema drift must not take the
            // whole run down.
            Console.Error.WriteLine($"jot: note: ignoring corrections.json ({ex.Message})");
            return [];
        }
    }

    private static bool TryReadTermFile(
        string path, out IReadOnlyList<VocabularyTerm> terms, out string? reason)
    {
        terms = [];
        try
        {
            var file = JsonSerializer.Deserialize<TermFile>(File.ReadAllText(path), TermFileJson);
            var list = new List<VocabularyTerm>();
            foreach (VocabularyTerm t in file?.Terms ?? [])
            {
                string clean = VocabularyStore.SanitizeTerm(t.Text);
                if (clean.Length == 0) continue;
                list.Add(new VocabularyTerm { Id = t.Id, Text = clean, Aliases = t.Aliases });
            }
            terms = list;
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    // Mirrors VocabularyStore's wire shape (its DTO is private); the term type itself is the shared one.
    private static readonly JsonSerializerOptions TermFileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class TermFile
    {
        public int Version { get; set; } = 1;
        public List<VocabularyTerm> Terms { get; set; } = [];
    }
}
