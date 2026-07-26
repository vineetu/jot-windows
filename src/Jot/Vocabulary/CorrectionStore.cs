using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/CorrectionStore.swift` (pinned commit 5326460).
// Conformance-locked by correction_store_roundtrip.json.
//
// Swift's `actor` has no C# equivalent, so a private monitor around every public entry point
// carries the same single-writer guarantee. The surface stays synchronous on purpose: the Swift is
// synchronous once you remove the actor hop that is the only reason its callers `await`.
//
// The on-disk JSON must round-trip with Swift's `[String: Term]` encoding — the same file is read
// and written by the Mac/iOS build, so the camelCase names, the omitted-when-nil optional, and the
// absent-key defaults below are a wire contract, not style.

/// <summary>
/// The correction store: per-term trust plus user-confirmed wrong→right mappings, learned from the
/// owner's ✓/✗ verdicts.
///
/// Each mapping carries <c>Net = Confirmations − Reverts</c>. <see cref="Snapshot"/> feeds
/// <see cref="VocabularyGate"/>, where a confirmed mapping overrides the gate's guards for that
/// exact <c>(originalWord → term)</c> pair only.
///
/// Safety: a mapping whose original is a COMMON word never auto-applies from net alone — only via
/// the explicit <see cref="GrantAlwaysReplace"/>, which ONE revert revokes. A rare/OOV original
/// arms at net ≥ 1. Deactivation is always net ≤ 0: easy to disarm, hard to arm a dangerous one.
///
/// Storage: <c>&lt;containerRoot&gt;\Vocabulary\corrections.json</c>. Which OS directory the root is
/// stays the app's policy (<c>JotPaths.VocabularyDir</c>) — this takes an injected root, and
/// <c>null</c> means purely in-memory (no load, no persist) for tests.
/// </summary>
public sealed class CorrectionStore
{
    // MARK: - Model

    public sealed class Mapping
    {
        // Absent keys DEFAULT rather than throw: an older Mac-written corrections.json has no
        // `blockedKeeps`/`alwaysReplace`, and a throw here drops ALL learned corrections. The two
        // identity fields stay required, matching Swift's `decode` vs `decodeIfPresent` split.
        [JsonRequired] public string OriginalWord { get; set; } = "";
        [JsonRequired] public string Term { get; set; } = "";
        public int Confirmations { get; set; }
        public int Reverts { get; set; }

        /// <summary>Times the owner said "keep original" on this pair while it was BLOCKED. The
        /// learning <see cref="Net"/> deliberately ignores these — a kept-keep contributes 0 — so
        /// for a common-word proposal like "okay"→"Okta" net never moves however often the owner
        /// rejects it, and this is the ONLY signal of that repeated rejection. Read ONLY by
        /// <see cref="KeyboardSuppressedPairs"/>: suppression is keyboard-only, the gate and the
        /// transcript review ignore it.</summary>
        public int BlockedKeeps { get; set; }

        /// <summary>The owner explicitly granted "always replace" for this exact pair, so the gate
        /// auto-applies it even over a common word. Revoked by any applied-revert — one revert
        /// demotes.</summary>
        public bool AlwaysReplace { get; set; }

        // Computed, and NOT part of the wire shape (Swift's CodingKeys omits it).
        [JsonIgnore] public int Net => Confirmations - Reverts;
    }

    /// <summary>Repeated "keep original" rejections on a BLOCKED pair after which the keyboard
    /// stops re-asking it. Transcript review still surfaces it.</summary>
    public const int KeyboardKeepSuppressThreshold = 2;

    public sealed class Term
    {
        [JsonRequired] public List<Mapping> Mappings { get; set; } = [];
        [JsonRequired] public List<string> SuppressedBlocks { get; set; } = [];

        /// <summary>One-shot teach lane: normalized heard phrases this term has already spent its
        /// single merge-teach ask on. A phrase lands here once EVER — asked and never re-asked,
        /// adjudicated or not. Optional so pre-field files still decode.</summary>
        public List<string>? MergeAskedPhrases { get; set; }
    }

    // Swift's JSONEncoder omits nil optionals and does not \u-escape non-ASCII; matching both keeps
    // a Windows-written ledger byte-comparable with the Mac's for the same state.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // MARK: - State

    private readonly string? _containerRoot;
    private readonly IDiagnosticsSink _diagnostics;
    private readonly object _gate = new();

    // Ordinal-keyed on purpose (Swift's Dictionary is): the key is already folded through
    // CorrectionKey, and a case-insensitive comparer would fold a SECOND, different way.
    private readonly Dictionary<string, Term> _terms = new(StringComparer.Ordinal);

    private bool _loaded;

    // Set when the file EXISTS but failed to decode. While true Persist is a hard no-op — the store
    // must never overwrite a curated ledger it could not read. Distinct from file-absent/empty,
    // which is safe to start empty and persist over.
    private bool _loadFailed;

    /// <param name="containerRoot">Directory the <c>Vocabulary\</c> subtree lives under. <c>null</c>
    /// ⇒ purely in-memory (no load, no persist) — the test / no-persist mode.</param>
    /// <param name="diagnostics">Sink for the decode-failure guard.</param>
    public CorrectionStore(string? containerRoot, IDiagnosticsSink? diagnostics = null)
    {
        _containerRoot = containerRoot;
        _diagnostics = diagnostics ?? NoopDiagnosticsSink.Instance;
    }

    // MARK: - Reads (for the gate + review surface)

    /// <summary>All mappings as override entries. Fetched once per dictation and passed into
    /// <see cref="VocabularyGate.Apply"/>, so the per-replacement hot loop never touches this lock.</summary>
    public IReadOnlyList<OverrideEntry> Snapshot()
    {
        lock (_gate)
        {
            LoadIfNeeded();
            return _terms.Values
                .SelectMany(t => t.Mappings)
                .Select(m => new OverrideEntry(m.OriginalWord, m.Term, m.Net, m.AlwaysReplace))
                .ToList();
        }
    }

    /// <summary>Grant "always replace originalWord with term" — the explicit consent that lets the
    /// gate auto-apply a common-word pair, ending the forever-ask treadmill.</summary>
    public void GrantAlwaysReplace(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            Mutate(originalWord, term, m => m.AlwaysReplace = true);
            Persist();
        }
    }

    /// <summary>True if the owner tapped "Stop asking" on a blocked pair. Read ONLY by
    /// <see cref="KeyboardSuppressedPairs"/> — the gate and the transcript review do NOT consult it,
    /// so suppression never hides a pair from deliberate review.</summary>
    public bool IsBlockSuppressed(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            return _terms.TryGetValue(CorrectionKey.Lowercased(term), out Term? t)
                && t.SuppressedBlocks.Contains(CorrectionKey.Normalize(originalWord));
        }
    }

    /// <summary>Pair keys the keyboard should STOP asking: the owner either kept the original
    /// ≥ <see cref="KeyboardKeepSuppressThreshold"/> times on a blocked pair, or tapped "Stop
    /// asking". Keyboard-only. The shape is <see cref="CorrectionKey.PairKey"/>'s exactly — this
    /// output is piped straight into <see cref="AskPolicy.Select"/>, so produce-key and check-key
    /// must agree byte-for-byte.</summary>
    public IReadOnlySet<string> KeyboardSuppressedPairs()
    {
        lock (_gate)
        {
            LoadIfNeeded();
            var output = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string termKey, Term t) in _terms)
            {
                // Both sides are already normalized on write, so PairKey is idempotent here —
                // routing through it anyway keeps ONE key rule shared with AskPolicy.
                foreach (Mapping m in t.Mappings)
                {
                    if (m.BlockedKeeps >= KeyboardKeepSuppressThreshold)
                        output.Add(CorrectionKey.PairKey(m.OriginalWord, termKey));
                }
                foreach (string ow in t.SuppressedBlocks) output.Add(CorrectionKey.PairKey(ow, termKey));
            }
            return output;
        }
    }

    /// <summary>Every pair whose one-shot merge-teach ask has already been spent. Same key shape as
    /// <see cref="KeyboardSuppressedPairs"/>.</summary>
    public IReadOnlySet<string> MergeAskedPairs()
    {
        lock (_gate)
        {
            LoadIfNeeded();
            var output = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string termKey, Term t) in _terms)
            {
                foreach (string phrase in t.MergeAskedPhrases ?? []) output.Add(CorrectionKey.PairKey(phrase, termKey));
            }
            return output;
        }
    }

    /// <summary>Record that a merge-teach ask has been published — its single shot is spent,
    /// adjudicated or not.</summary>
    public void NoteMergeAsked(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            string key = CorrectionKey.Lowercased(term);
            string phrase = CorrectionKey.Normalize(originalWord);
            Term t = TermFor(key);
            List<string> asked = t.MergeAskedPhrases ?? [];
            if (asked.Contains(phrase)) return;
            asked.Add(phrase);
            t.MergeAskedPhrases = asked;
            _terms[key] = t;
            Persist();
        }
    }

    // MARK: - Verdicts (called by the review surface)

    /// <summary>Owner confirmed the correction should apply. Raises net; once net clears the
    /// threshold the gate's override fires.</summary>
    public void Confirm(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            Mutate(originalWord, term, m => m.Confirmations += 1);
            Persist();
        }
    }

    /// <summary>Move the mapping's net by <paramref name="delta"/> — a transcript's contribution to
    /// learning, reversible (undo passes the opposite delta). A single transcript contributes at
    /// most ±1 per mapping (clamped in <see cref="CorrectionProvenance"/>), so reviewing three
    /// "name→Jamy" occurrences can't inflate net to 3.</summary>
    public void Adjust(string originalWord, string term, int delta)
    {
        // Swift returns BEFORE loadIfNeeded on a zero delta — a no-op must not even touch disk.
        if (delta == 0) return;
        lock (_gate)
        {
            LoadIfNeeded();
            Mutate(originalWord, term, m =>
            {
                if (delta > 0) m.Confirmations += delta;
                else m.Reverts += -delta;
                // One revert revokes an "always replace" grant — the user changed their mind, so
                // the pair goes back to asking.
                if (delta < 0) m.AlwaysReplace = false;
            });
            Persist();
        }
    }

    /// <summary>Owner reverted the correction. At net ≤ 0 the override deactivates and the gate
    /// falls back to its safe default.</summary>
    public void Revert(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            Mutate(originalWord, term, m => m.Reverts += 1);
            Persist();
        }
    }

    /// <summary>Owner tapped "Stop asking" on a BLOCKED proposal — hard-suppress it from keyboard
    /// asks immediately. Transcript review is unaffected (it doesn't read this).</summary>
    public void SuppressBlock(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            string key = CorrectionKey.Lowercased(term);
            Term t = TermFor(key);
            string ow = CorrectionKey.Normalize(originalWord);
            if (!t.SuppressedBlocks.Contains(ow)) t.SuppressedBlocks.Add(ow);
            _terms[key] = t;
            Persist();
        }
    }

    /// <summary>Owner said "keep original" on a BLOCKED pair. The learning net ignores this, so it
    /// is counted separately; at <see cref="KeyboardKeepSuppressThreshold"/> the keyboard stops
    /// re-asking. Called from the single verdict choke point, so it counts keeps from both the
    /// keyboard and the in-app review.</summary>
    public void NoteBlockedKeep(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            Mutate(originalWord, term, m => m.BlockedKeeps += 1);
            Persist();
        }
    }

    /// <summary>Undo of a blocked-pair "keep original". Floored at 0 so it can never go
    /// negative.</summary>
    public void ClearBlockedKeep(string originalWord, string term)
    {
        lock (_gate)
        {
            LoadIfNeeded();
            Mutate(originalWord, term, m => m.BlockedKeeps = Math.Max(0, m.BlockedKeeps - 1));
            Persist();
        }
    }

    // MARK: - Internals

    private Term TermFor(string key) => _terms.TryGetValue(key, out Term? t) ? t : new Term();

    private void Mutate(string originalWord, string term, Action<Mapping> change)
    {
        string key = CorrectionKey.Lowercased(term);
        string ow = CorrectionKey.Normalize(originalWord);
        Term t = TermFor(key);
        Mapping? existing = t.Mappings.FirstOrDefault(m => m.OriginalWord == ow);
        if (existing is not null)
        {
            change(existing);
        }
        else
        {
            // The mapping stores the term in the CALLER's casing ("Jamy"), not the lowercased
            // dictionary key — that casing is what the gate splices into the text.
            var m = new Mapping { OriginalWord = ow, Term = term };
            change(m);
            t.Mappings.Add(m);
        }
        _terms[key] = t;
    }

    private string? FilePath()
    {
        if (_containerRoot is null) return null;
        string vocab = Path.Combine(_containerRoot, "Vocabulary");
        // Swift creates the directory here, on the READ path too — a bare Snapshot() materializes
        // the folder. Kept so both platforms leave the same footprint.
        try { Directory.CreateDirectory(vocab); } catch { /* the caller's try? equivalent */ }
        return Path.Combine(vocab, "corrections.json");
    }

    private void LoadIfNeeded()
    {
        if (_loaded) return;
        _loaded = true;
        if (FilePath() is not { } path) return;

        byte[] data;
        // No file, or unreadable → start empty; SAFE to persist later. Only a file that EXISTS but
        // fails to DECODE is the data-loss hazard: the naive path leaves `_terms` empty and lets
        // the next Persist write `{}` over the curated ledger.
        try
        {
            if (!File.Exists(path)) return;
            data = File.ReadAllBytes(path);
        }
        catch { return; }
        if (data.Length == 0) return;

        try
        {
            // A bare `null` document decodes to null here but THROWS in Swift; rethrow so both
            // platforms take the refuse-to-overwrite branch on the same bytes.
            Dictionary<string, Term> decoded =
                JsonSerializer.Deserialize<Dictionary<string, Term>>(data, Json)
                ?? throw new JsonException("corrections.json decoded to null");
            _terms.Clear();
            foreach ((string k, Term v) in decoded) _terms[k] = v;
        }
        catch (Exception ex)
        {
            // Present but undecodable — REFUSE to persist (leave the bytes intact for recovery) and
            // surface it.
            _loadFailed = true;
            _diagnostics.Record(
                DiagnosticsCategory.VocabularySaveFailed,
                "corrections.json present but failed to decode — refusing to overwrite",
                new Dictionary<string, string> { ["error"] = ex.ToString() });
        }
    }

    private void Persist()
    {
        if (_loadFailed) return;
        if (FilePath() is not { } path) return;
        try
        {
            // Write-then-rename is the .NET stand-in for Foundation's atomic write: a crash mid-save
            // must never leave a truncated ledger.
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(_terms, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* best-effort, exactly like the Swift's try? */ }
    }
}
