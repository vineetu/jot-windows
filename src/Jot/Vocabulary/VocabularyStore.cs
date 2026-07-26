using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jot.Services;

namespace Jot.Vocabulary;

/// <summary>
/// The user's term list: what Jot should spell their way, plus the "sounds like" aliases that give
/// the gate something to judge plausibility against.
///
/// Storage: <c>&lt;containerRoot&gt;\Vocabulary\vocabulary.json</c> — the SAME root
/// <see cref="CorrectionStore"/> and <see cref="CorrectionProvenance"/> take, so all three of the
/// subsystem's files land in the one folder that is registered in <c>JotDataPurge</c> and
/// <c>DataFolderMigrator</c> (D7). The root is resolved ONCE at construction, like
/// <c>JsonRecordingStore</c>, or this store disagrees with the library store after a folder move.
///
/// Every writer goes through <see cref="SanitizeTerm"/>. That is not tidiness: iOS shipped a bug
/// where a typed term containing <c>:</c> or a leading <c>#</c> corrupted the interchange format,
/// and separately where the Settings editor bypassed the sanitizer that the add-dialog used.
/// </summary>
public sealed class VocabularyStore
{
    /// <summary>Terms longer than this stop being vocabulary and start being dictation.</summary>
    public const int MaxTermWords = 4;

    /// <summary>Cap on the list. This is a LATENCY budget — every term is a spotter query — so
    /// re-derive it against M2's timings before the feature unhides.</summary>
    public const int MaxTerms = 200;

    // Match the sibling stores' wire style: camelCase, no \u-escaping of non-ASCII, nulls omitted.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly string? _dir;
    private readonly string? _filePath;
    private readonly IDiagnosticsSink _diagnostics;
    private bool _loading;

    /// <summary>Terms in author order. Observable so a later VocabularyPage binds straight to it.</summary>
    public ObservableCollection<VocabularyTerm> Terms { get; } = [];

    /// <param name="containerRoot">Directory the <c>Vocabulary\</c> subtree lives under — pass
    /// <c>JotPaths.DataDir</c>, not <c>VocabularyDir</c>. <c>null</c> ⇒ purely in-memory (no load,
    /// no persist), matching <see cref="CorrectionStore"/>'s test mode.</param>
    public VocabularyStore(string? containerRoot, IDiagnosticsSink? diagnostics = null)
    {
        _diagnostics = diagnostics ?? NoopDiagnosticsSink.Instance;
        if (containerRoot is not null)
        {
            _dir = Path.Combine(containerRoot, JotPaths.VocabularyFolderName);
            _filePath = Path.Combine(_dir, "vocabulary.json");
        }
        Load();
    }

    // MARK: - Reads

    public bool IsEmpty => Terms.Count == 0;

    /// <summary>The term whose text or any alias folds to <paramref name="text"/>, else null.</summary>
    public VocabularyTerm? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string key = CorrectionKey.Normalize(text);
        return Terms.FirstOrDefault(t =>
            CorrectionKey.Normalize(t.Text) == key ||
            t.Aliases.Any(a => CorrectionKey.Normalize(a) == key));
    }

    /// <summary>
    /// The alias set the GATE should see for a term — the stored aliases plus, for a multi-word
    /// term, its space-stripped form.
    ///
    /// This is the `enrichedAliases` fix Mac and iOS both filed against the same bug: "Ramaa Nathan"
    /// heard as the merged "Ramanathan" was replaced by the SHORTER term "Ramaa", because the right
    /// term was never plausible against the merged word and so never competed. Feed-time only —
    /// vocabulary.json is untouched and the UI never shows the synthetic alias.
    /// </summary>
    public static IReadOnlyList<string> FeedAliases(VocabularyTerm term)
    {
        string merged = string.Concat(term.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (merged.Length == 0 || merged.Length == term.Text.Trim().Length) return term.Aliases;
        if (term.Aliases.Any(a => CorrectionKey.Normalize(a) == CorrectionKey.Normalize(merged)))
            return term.Aliases;
        return [.. term.Aliases, merged];
    }

    // MARK: - Writes (every one funnels through SanitizeTerm)

    /// <summary>
    /// THE choke point. Precomposes, collapses whitespace runs, and strips the characters that break
    /// the interchange format (<c>:</c> <c>,</c> <c>;</c> and a leading <c>#</c>). Never
    /// case-folds — the whole point of a term is the spelling the user chose.
    /// </summary>
    public static string SanitizeTerm(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Normalize(System.Text.NormalizationForm.FormC);
        s = new string(s.Where(c => c is not (':' or ',' or ';') && !char.IsControl(c)).ToArray());
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.TrimStart('#').Trim();
    }

    /// <summary>Word count after sanitizing — the &gt;<see cref="MaxTermWords"/> warning's input.</summary>
    public static int WordCount(string? raw) =>
        SanitizeTerm(raw).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Add a term, or MERGE into the existing one when it is already in the list (never a second
    /// row). Returns the live term, or null when the text sanitizes away or the list is at
    /// <see cref="MaxTerms"/>.
    /// </summary>
    public VocabularyTerm? Add(string? text, IEnumerable<string>? aliases = null)
    {
        string clean = SanitizeTerm(text);
        // D12: a term under MinTermLength is not merely useless, it is invisible — the checkpoint's
        // tokenizer returns an EMPTY id list for a single character, so the row would sit in the list
        // looking healthy and never match anything. Refuse it at the writer rather than in the
        // sanitizer, because the sanitizer also cleans ALIASES, where a short form is harmless.
        if (clean.Length < VocabularyTermRules.MinTermLength) return null;

        VocabularyTerm? existing = Terms.FirstOrDefault(
            t => CorrectionKey.Normalize(t.Text) == CorrectionKey.Normalize(clean));
        if (existing is not null)
        {
            if (AddAliases(existing, aliases)) Save();
            return existing;
        }

        if (Terms.Count >= MaxTerms) return null;
        var term = new VocabularyTerm { Text = clean };
        AddAliases(term, aliases);
        Terms.Add(term);
        Save();
        return term;
    }

    /// <summary>The right-click "when Jot hears X, spell it Y" gesture: one call creates the term or
    /// appends the heard form as an alias of the existing one.</summary>
    public VocabularyTerm? AddMapping(string? heard, string? term) => Add(term, [heard ?? ""]);

    public bool Remove(VocabularyTerm term)
    {
        if (!Terms.Remove(term)) return false;
        Save();
        return true;
    }

    /// <summary>Replace a term's text and alias list wholesale (the edit form's Save).</summary>
    public void Update(VocabularyTerm term, string? text, IEnumerable<string>? aliases)
    {
        string clean = SanitizeTerm(text);
        if (clean.Length < VocabularyTermRules.MinTermLength) return;   // same rule as Add — see there
        term.Text = clean;
        term.Aliases = Clean(aliases, clean);
        Save();
    }

    // An alias belonging to two terms makes the gate's plausibility lever ambiguous, so a duplicate
    // never persists: last write wins and the alias moves.
    private bool AddAliases(VocabularyTerm term, IEnumerable<string>? aliases)
    {
        List<string> incoming = Clean(aliases, term.Text);
        bool changed = false;
        foreach (string alias in incoming)
        {
            string key = CorrectionKey.Normalize(alias);
            foreach (VocabularyTerm other in Terms)
            {
                if (ReferenceEquals(other, term)) continue;
                changed |= other.Aliases.RemoveAll(a => CorrectionKey.Normalize(a) == key) > 0;
            }
            if (term.Aliases.Any(a => CorrectionKey.Normalize(a) == key)) continue;
            term.Aliases.Add(alias);
            changed = true;
        }
        return changed;
    }

    // Sanitize, drop blanks/dupes, and drop an alias identical to the term itself (it would make
    // the gate propose a word onto itself).
    private static List<string> Clean(IEnumerable<string>? aliases, string termText)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { CorrectionKey.Normalize(termText) };
        foreach (string raw in aliases ?? [])
        {
            string clean = SanitizeTerm(raw);
            if (clean.Length == 0) continue;
            if (!seen.Add(CorrectionKey.Normalize(clean))) continue;
            result.Add(clean);
        }
        return result;
    }

    // MARK: - Persistence

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;
        _loading = true;
        try
        {
            var file = JsonSerializer.Deserialize<VocabularyFileDto>(File.ReadAllText(_filePath), Json);
            foreach (VocabularyTerm t in file?.Terms ?? [])
            {
                string clean = SanitizeTerm(t.Text);
                if (clean.Length == 0) continue;
                Terms.Add(new VocabularyTerm { Id = t.Id, Text = clean, Aliases = Clean(t.Aliases, clean) });
            }
        }
        catch (Exception ex)
        {
            // Unlike the corrections ledger this is re-authorable by hand, so an unreadable file
            // degrades to an empty list rather than latching persistence off — but it is still a
            // data-loss-shaped event, so it is reported, not swallowed.
            Terms.Clear();
            _diagnostics.Record(DiagnosticsCategory.VocabularySaveFailed,
                "vocabulary.json unreadable — starting with an empty term list",
                new Dictionary<string, string> { ["reason"] = ex.Message });
        }
        finally { _loading = false; }
    }

    private void Save()
    {
        if (_loading || _filePath is null || _dir is null) return;
        try
        {
            Directory.CreateDirectory(_dir);
            var dto = new VocabularyFileDto { Terms = Terms.Where(t => !t.IsBlank).ToList() };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(dto, Json));
        }
        catch (Exception ex)
        {
            _diagnostics.Record(DiagnosticsCategory.VocabularySaveFailed, "vocabulary.json write failed",
                new Dictionary<string, string> { ["reason"] = ex.Message });
        }
    }

    // Wrapped in an object rather than a bare array so a later schema version has somewhere to live.
    private sealed class VocabularyFileDto
    {
        public int Version { get; set; } = 1;
        public List<VocabularyTerm> Terms { get; set; } = [];
    }
}
