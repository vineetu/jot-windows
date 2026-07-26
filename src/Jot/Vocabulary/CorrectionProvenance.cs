using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/CorrectionProvenance.swift` (pinned commit 5326460).
// Conformance-locked by provenance_verdicts.json.
//
// Same actor→lock substitution as CorrectionStore. Two things carry real porting risk here:
//
//  1. OFFSETS ARE GRAPHEME-CLUSTER COUNTS, not UTF-16 indices — VocabularyGate emits them that way
//     because Mac/iOS persist them that way. So MapOffsets diffs sequences of grapheme clusters,
//     and cluster equality is canonical (Swift's Character ==), not ordinal.
//  2. `Record` is a Swift STRUCT, so `p.records[i].publishedStart = …` mutates a COPY held by the
//     payload. CorrectionRecord is a class here, so the same statement mutates a shared object —
//     safe only because every payload is decoded fresh from disk on each call. Do not start caching
//     payloads without revisiting MappedPayload, which mutates a payload it deliberately never saves.

/// <summary>
/// Per-transcript correction provenance plus per-occurrence resolution.
///
/// Three ledgers, deliberately separate:
/// <list type="bullet">
/// <item><b>records</b> — what the gate did, per OCCURRENCE. Each carries a STABLE identity
/// (<c>OriginalStart</c>) plus a LIVE anchor (<c>PublishedStart</c>) into the current text.</item>
/// <item><b>verdicts</b> — the owner's pick per occurrence, keyed by that stable identity. A verdict
/// HIDES the row and drives the this-occurrence text edit. It is the ONLY thing that decides
/// "answered".</item>
/// <item><b>contributions</b> — this transcript's ±1 contribution to each mapping's learning net, so
/// a mapping feeds <see cref="CorrectionStore"/> at most once per transcript however many
/// occurrences the owner adjudicates.</item>
/// </list>
///
/// Timing: the gate runs BEFORE the transcript id exists, so it fills a transient pending slot at
/// rescore time and <see cref="Commit"/> moves it to a side-JSON keyed by the real id once the
/// transcript is saved. Dictation is serial, so the single pending slot is safe.
///
/// Storage: <c>&lt;containerRoot&gt;\Vocabulary\provenance\&lt;transcriptID&gt;.json</c>; the root is
/// injected and <c>null</c> ⇒ in-memory (no persistence) — the test mode.
/// </summary>
public sealed class CorrectionProvenance
{
    // MARK: - Model

    /// <summary>On-disk shape: proposals + per-occurrence verdicts + this transcript's current
    /// contribution to each mapping's learning net.</summary>
    public sealed class Payload
    {
        [JsonRequired] public List<CorrectionRecord> Records { get; set; } = [];

        /// <summary><c>record.Key</c> → "term" | "original" | "alt0".</summary>
        [JsonRequired] public Dictionary<string, string> Verdicts { get; set; } = new(StringComparer.Ordinal);

        /// <summary><c>record.MappingKey</c> → this transcript's net contribution.</summary>
        [JsonRequired] public Dictionary<string, int> Contributions { get; set; } = new(StringComparer.Ordinal);

        /// <summary>The transcript text the records' anchors are valid for.
        /// <see cref="ReconciledPayload"/> diffs this against the live text and shifts anchors, so an
        /// edit made ANYWHERE (verdict pick, hand-edit, keyboard-verdict drain) is accounted exactly
        /// once — the reconcile is state-based (a fingerprint), not event-based. Null on payloads
        /// written before this field existed; adopted on first read.</summary>
        public string? AnchoredText { get; set; }
    }

    /// <summary>What a verdict change does to a mapping's global learning net. The caller applies it
    /// to <see cref="CorrectionStore.Adjust"/>; <c>Delta == 0</c> is never emitted.</summary>
    public readonly record struct MappingDelta(string OriginalWord, string Term, int Delta);

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

    private List<CorrectionRecord> _pending = [];

    // The gate-output text `_pending`'s anchors index into — the ONLY text those offsets are valid
    // for. Becomes the payload's AnchoredText baseline at commit; the post-gate transform chain
    // (segmenter / filler sweep / number normalizer) changes the text BEFORE it is saved, and the
    // first reconcile absorbs that drift exactly by diffing from this baseline.
    private string _pendingAnchorText = "";

    /// <param name="containerRoot">Directory the <c>Vocabulary\provenance\</c> subtree lives under.
    /// <c>null</c> ⇒ purely in-memory (no persistence) — the test mode.</param>
    /// <param name="diagnostics">Sink for the commit trace.</param>
    public CorrectionProvenance(string? containerRoot, IDiagnosticsSink? diagnostics = null)
    {
        _containerRoot = containerRoot;
        _diagnostics = diagnostics ?? NoopDiagnosticsSink.Instance;
    }

    // MARK: - Gate side (write)

    /// <summary>Stash this dictation's proposals until the transcript is saved.
    /// <paramref name="gatedText"/> is the gate's output text — the string the proposals'
    /// <c>PublishedStart</c> offsets point into.</summary>
    public void Record(IReadOnlyList<VocabularyGate.Proposal> proposals, string gatedText)
    {
        lock (_gate)
        {
            _pendingAnchorText = gatedText;
            _pending = proposals.Select(p => new CorrectionRecord
            {
                OriginalWord = p.OriginalWord,
                Term = p.Term,
                Decision = p.Decision,
                Outcome = p.Outcome,
                Confidence = p.Confidence,
                Margin = p.Margin,
                Unsure = p.Unsure,
                OccurrenceIndex = p.OccurrenceIndex,
                OriginalStart = p.OriginalStart,
                OriginalLength = p.OriginalLength,
                PublishedStart = p.PublishedStart,
                PublishedLength = p.PublishedLength,
                // Empty collapses to null so a no-alternates record encodes the same as one written
                // before the field existed.
                Alternates = p.Alternates.Count == 0 ? null : p.Alternates,
                Shape = p.Shape,
            }).ToList();
        }
    }

    /// <summary>ADDITIVE to the shared port (no Swift counterpart): the records just stashed by
    /// <see cref="Record"/>, so the app-side ask deck reads the SAME Proposal→Record mapping that
    /// gets committed. Rebuilding that mapping in the caller is how the deck and the review surface
    /// would silently drift apart.</summary>
    public IReadOnlyList<CorrectionRecord> PendingRecords
    {
        get { lock (_gate) { return _pending.ToList(); } }
    }

    /// <summary>Clear the pending slot. Called at the START of every transcription so a
    /// no-proposal dictation — or a non-saving caller that filled the slot — can never have a stale
    /// pending list committed under the NEXT transcript's id.</summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            _pending = [];
            _pendingAnchorText = "";
        }
    }

    /// <summary>Persist the pending records under the saved transcript id. The anchor baseline is
    /// the GATE-OUTPUT text, the one string those offsets are valid for — NOT the published text,
    /// whose post-gate transforms have already shifted it.</summary>
    public void Commit(Guid transcriptId)
    {
        lock (_gate)
        {
            List<CorrectionRecord> records = _pending;
            string anchorText = _pendingAnchorText;
            _pending = [];
            _pendingAnchorText = "";
            if (records.Count == 0 || FilePath(transcriptId) is not { } path) return;

            // Commit is once-per-fresh-id, but be defensive: on a re-commit/retry PRESERVE the
            // owner's verdicts and the contributions guard — only the records are rewritten. A blind
            // overwrite would silently wipe answered rows.
            Payload existing = LoadPayload(transcriptId);
            var payload = new Payload
            {
                Records = records,
                Verdicts = existing.Verdicts,
                Contributions = existing.Contributions,
                AnchoredText = anchorText,
            };
            bool wrote = Write(path, payload);
            _diagnostics.Record(DiagnosticsCategory.VocabularyGate, "provenance committed",
                new Dictionary<string, string>
                {
                    ["records"] = records.Count.ToString(CultureInfo.InvariantCulture),
                    ["wrote"] = wrote ? "true" : "false",
                    ["id"] = FileStem(transcriptId),
                });
        }
    }

    // MARK: - Review side (read + verdict)

    /// <summary>Full payload for a transcript (proposals + verdicts). Named <c>PayloadFor</c>
    /// because C# forbids a member sharing its name with the nested <see cref="Payload"/> type;
    /// the shared implementation calls it <c>payload(transcriptID:)</c>.</summary>
    public Payload PayloadFor(Guid transcriptId)
    {
        lock (_gate) { return LoadPayload(transcriptId); }
    }

    /// <summary>
    /// Payload with every record's anchor reconciled to <paramref name="currentText"/>. THE
    /// anchor-maintenance entry point — every reader that will resolve spans goes through here.
    ///
    /// State-based, not event-based: the payload remembers the text its anchors are valid for, and
    /// if the live text differs the offsets are re-mapped through the diff. That accounts for ANY
    /// edit exactly once, no matter which surface made it or whether this instance saw it happen. An
    /// anchor inside a changed region keeps its offset and is left to strict span resolution: if the
    /// word still starts there it resolves, otherwise resolution fails and the surfaces fail SAFE.
    /// </summary>
    public Payload ReconciledPayload(Guid transcriptId, string currentText)
    {
        lock (_gate)
        {
            Payload p = LoadPayload(transcriptId);
            if (p.Records.Count == 0) return p;
            if (p.AnchoredText is not { } anchored)
            {
                // Legacy payload: adopt the live text as the baseline WITHOUT shifting — the anchors
                // are as good as they ever were, and strict resolution backstops any prior drift.
                p.AnchoredText = currentText;
                Persist(transcriptId, p);
                return p;
            }
            if (anchored == currentText) return p;
            Reanchor(p, anchored, currentText);
            p.AnchoredText = currentText;
            Persist(transcriptId, p);
            return p;
        }
    }

    /// <summary>Records mapped into <paramref name="text"/> WITHOUT persisting — for a one-off read
    /// against a text that is NOT the transcript's stored text. Persisting that hop would route the
    /// durable anchor chain through a derived text and permanently strand any record whose word that
    /// text rewrote away. The durable chain stays gate-output → transcript text, owned by
    /// <see cref="ReconciledPayload"/>.</summary>
    public Payload MappedPayload(Guid transcriptId, string text)
    {
        lock (_gate)
        {
            Payload p = LoadPayload(transcriptId);
            if (p.Records.Count == 0 || p.AnchoredText is not { } anchored || anchored == text) return p;
            Reanchor(p, anchored, text);
            return p;   // NOT persisted — AnchoredText keeps the durable baseline
        }
    }

    /// <summary>
    /// Account EXACTLY for one of OUR OWN verdict edits (a pick/undo replacing
    /// <paramref name="recordKey"/>'s span at <paramref name="start"/>). A text diff cannot do this
    /// safely: when the replacement shares a suffix with the replaced word ("nathan" →
    /// "Ramanathan") the diff is genuinely ambiguous and can shift the edited record's own anchor
    /// off its word, breaking Undo. Self edits therefore REPORT their span; the blind diff in
    /// <see cref="ReconciledPayload"/> is only for EXTERNAL edits, where strict span resolution
    /// backstops it.
    ///
    /// Race-tolerant: if a reconcile already absorbed this edit (the fingerprint already matches),
    /// the bulk shift is done and only the self record — the one span the diff can get wrong — is
    /// pinned.
    /// </summary>
    public void NoteSelfEdit(Guid transcriptId, string recordKey, int start, int oldLength, int newLength, string newText)
    {
        lock (_gate)
        {
            Payload p = LoadPayload(transcriptId);
            if (p.Records.Count == 0) return;
            if (p.AnchoredText == newText)
            {
                foreach (CorrectionRecord r in p.Records)
                {
                    if (r.Key == recordKey) r.PublishedStart = start;
                }
            }
            else
            {
                int delta = newLength - oldLength;
                foreach (CorrectionRecord r in p.Records)
                {
                    if (r.Key == recordKey) r.PublishedStart = start;
                    else if (r.PublishedStart >= start + oldLength) r.PublishedStart += delta;
                }
                p.AnchoredText = newText;
            }
            Persist(transcriptId, p);
        }
    }

    /// <summary>Record a per-occurrence verdict and return how the mapping's global net should move
    /// (the caller applies them to <see cref="CorrectionStore"/>). <paramref name="verdict"/> ∈
    /// {"term", "original", "alt0"}. A transcript contributes at most ±1 per mapping — recomputed
    /// from ALL its verdicts — so three "name→Jamy" picks can't inflate net to 3 and a mixed
    /// transcript gives 0. An "alt0" pick moves the ALTERNATE's mapping through the same clamp, so
    /// one call may return one delta per affected mapping.</summary>
    public IReadOnlyList<MappingDelta> SetVerdict(Guid transcriptId, CorrectionRecord record, string verdict)
    {
        lock (_gate)
        {
            Payload p = LoadPayload(transcriptId);
            p.Verdicts[record.Key] = verdict;
            return Finalize(p, transcriptId, record);
        }
    }

    /// <summary>Undo a verdict — clears the pick (the row re-surfaces) and reverses this
    /// transcript's contribution if no sibling verdict still supports it.</summary>
    public IReadOnlyList<MappingDelta> ClearVerdict(Guid transcriptId, CorrectionRecord record)
    {
        lock (_gate)
        {
            Payload p = LoadPayload(transcriptId);
            p.Verdicts.Remove(record.Key);
            return Finalize(p, transcriptId, record);
        }
    }

    /// <summary>Drop a transcript's provenance (e.g. when the transcript is deleted).</summary>
    public void Discard(Guid transcriptId)
    {
        lock (_gate)
        {
            if (FilePath(transcriptId) is not { } path) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
        }
    }

    // MARK: - Contribution ledger

    // One verdict can move EITHER the base mapping (term/original picks) OR an alternate mapping
    // ("alt0" in the 3-option ask). Reconcile every mapping this record can affect through the same
    // ±1-per-transcript clamp — an alt pick that adjusted the store directly would bypass it.
    private IReadOnlyList<MappingDelta> Finalize(Payload p, Guid transcriptId, CorrectionRecord record)
    {
        var targets = new List<(string Term, string Key, string? Legacy)>
        {
            (record.Term, record.MappingKey, record.LegacyMappingKey),
        };
        foreach (VocabularyGate.Alternate alt in record.Alternates ?? [])
        {
            targets.Add((alt.Term, AltMappingKey(record.OriginalWord, alt.Term), null));
        }

        var deltas = new List<MappingDelta>();
        foreach ((string term, string key, string? legacy) in targets)
        {
            // Legacy-key fallback: a payload persisted before the shared normalization may hold this
            // mapping's contribution under the old raw-lowercased key. Migrate in place, or an
            // undo/re-pick double-counts.
            if (!p.Contributions.ContainsKey(key) && legacy is not null && legacy != key
                && p.Contributions.TryGetValue(legacy, out int carried))
            {
                p.Contributions[key] = carried;
                p.Contributions.Remove(legacy);
            }

            int desired = DesiredContribution(p, key);
            int old = p.Contributions.GetValueOrDefault(key, 0);
            if (desired == 0) p.Contributions.Remove(key);
            else p.Contributions[key] = desired;
            int delta = desired - old;
            if (delta != 0) deltas.Add(new MappingDelta(record.OriginalWord, term, delta));
        }

        Persist(transcriptId, p);
        return deltas;
    }

    // +1 if the owner meant the term for this mapping (any "term" pick — or an "alt0" pick whose
    // alternate IS this mapping — with no revert), −1 if they reverted an APPLIED one, 0 if mixed or
    // only kept-original. A kept-original on a BLOCKED record carries no learning signal, which is
    // exactly why CorrectionStore counts blockedKeeps separately.
    private static int DesiredContribution(Payload p, string mappingKey)
    {
        bool anyTerm = false;
        bool anyDemote = false;
        foreach (CorrectionRecord occ in p.Records)
        {
            string? v = p.Verdicts.GetValueOrDefault(occ.Key);
            if (occ.MappingKey == mappingKey)
            {
                if (v == "term") anyTerm = true;
                if (v == "original" && occ.Outcome == "applied") anyDemote = true;
            }
            if (v == "alt0" && occ.Alternates is { Count: > 0 } alts
                && AltMappingKey(occ.OriginalWord, alts[0].Term) == mappingKey)
            {
                anyTerm = true;
            }
        }
        if (anyTerm && !anyDemote) return 1;
        if (anyDemote && !anyTerm) return -1;
        return 0;
    }

    private static string AltMappingKey(string originalWord, string altTerm) =>
        $"{CorrectionKey.Normalize(originalWord)}|{CorrectionKey.Normalize(altTerm)}";

    // MARK: - Anchor mapping

    private static void Reanchor(Payload p, string anchored, string text)
    {
        IReadOnlyList<int> mapped = MapOffsets(p.Records.Select(r => r.PublishedStart).ToList(), anchored, text);
        for (int i = 0; i < p.Records.Count; i++) p.Records[i].PublishedStart = mapped[i];
    }

    /// <summary>
    /// Exact old→new offset mapping via a real minimal diff — it handles MULTI-region changes (the
    /// post-gate segmenter / filler / number chain makes several small edits in one hop, and a
    /// hand-edit Save can too), which a single prefix/suffix region cannot. An offset inside a
    /// removed region maps to the removal point and relies on the caller's strict whole-word
    /// resolution to fail safe.
    ///
    /// Units are GRAPHEME CLUSTERS, matching the anchors <see cref="VocabularyGate"/> emits.
    /// </summary>
    public static IReadOnlyList<int> MapOffsets(IReadOnlyList<int> offsets, string oldText, string newText)
    {
        (List<int> removals, List<int> insertions) = Difference(Graphemes(oldText), Graphemes(newText));
        removals.Sort();
        insertions.Sort();

        var output = new int[offsets.Count];
        for (int i = 0; i < offsets.Count; i++)
        {
            int anchor = offsets[i];
            int pos = anchor;
            foreach (int rem in removals)
            {
                if (rem >= anchor) break;
                pos--;
            }
            foreach (int ins in insertions)
            {
                if (ins > pos) break;
                pos++;
            }
            output[i] = Math.Max(0, pos);
        }
        return output;
    }

    // Swift's CollectionDifference contract, which MapOffsets walks: removal offsets index the OLD
    // sequence, insertion offsets the NEW one.
    private static (List<int> Removals, List<int> Insertions) Difference(string[] oldItems, string[] newItems)
    {
        var removals = new List<int>();
        var insertions = new List<int>();

        // Trim the common prefix/suffix first so the O(ND) search only ever sees the changed middle
        // — a transform-chain edit touches a few clusters of a multi-KB transcript.
        int minLen = Math.Min(oldItems.Length, newItems.Length);
        int prefix = 0;
        while (prefix < minLen && oldItems[prefix] == newItems[prefix]) prefix++;
        int suffix = 0;
        while (suffix < minLen - prefix
               && oldItems[^(suffix + 1)] == newItems[^(suffix + 1)]) suffix++;

        Myers(oldItems[prefix..(oldItems.Length - suffix)],
              newItems[prefix..(newItems.Length - suffix)],
              prefix, removals, insertions);
        return (removals, insertions);
    }

    // Cap on the edit-script length the O(ND) search will explore. Beyond it the trace's memory is
    // quadratic, and a change that large is a wholesale rewrite (AI cleanup, a paste-over) where a
    // minimal script buys nothing — every anchor in it is stranded either way.
    private const int MaxEditScript = 1024;

    private static void Myers(string[] a, string[] b, int offset, List<int> removals, List<int> insertions)
    {
        int n = a.Length;
        int m = b.Length;
        if (n == 0 || m == 0 || n + m > 2 * MaxEditScript)
        {
            ReplaceWholly(n, m, offset, removals, insertions);
            return;
        }

        int max = Math.Min(n + m, MaxEditScript);
        int off = max;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();

        for (int d = 0; d <= max; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = (k == -d || (k != d && v[off + k - 1] < v[off + k + 1]))
                    ? v[off + k + 1]
                    : v[off + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[off + k] = x;
                if (x >= n && y >= m)
                {
                    Backtrack(trace, off, d, n, m, offset, removals, insertions);
                    return;
                }
            }
        }
        ReplaceWholly(n, m, offset, removals, insertions);
    }

    private static void Backtrack(
        List<int[]> trace, int off, int dEnd, int n, int m, int offset,
        List<int> removals, List<int> insertions)
    {
        int x = n;
        int y = m;
        for (int d = dEnd; d > 0; d--)
        {
            int[] v = trace[d];
            int k = x - y;
            int prevK = (k == -d || (k != d && v[off + k - 1] < v[off + k + 1])) ? k + 1 : k - 1;
            int prevX = v[off + prevK];
            int prevY = prevX - prevK;
            while (x > prevX && y > prevY) { x--; y--; }
            if (x == prevX) insertions.Add(offset + prevY);
            else removals.Add(offset + prevX);
            x = prevX;
            y = prevY;
        }
    }

    private static void ReplaceWholly(int n, int m, int offset, List<int> removals, List<int> insertions)
    {
        for (int i = 0; i < n; i++) removals.Add(offset + i);
        for (int i = 0; i < m; i++) insertions.Add(offset + i);
    }

    // Grapheme clusters, NFC-precomposed: Swift compares `Character`s under canonical equivalence,
    // so an NFD "é" and its NFC twin must diff as EQUAL or a re-normalizing edit would look like a
    // full rewrite and strand every anchor after it. (See VocabularyGate's "Swift string model"
    // section — same axis, different operation.)
    private static string[] Graphemes(string s)
    {
        var output = new List<string>(s.Length);
        for (int i = 0; i < s.Length;)
        {
            int len = StringInfo.GetNextTextElementLength(s.AsSpan(i));
            output.Add(s.Substring(i, len).Normalize(NormalizationForm.FormC));
            i += len;
        }
        return output.ToArray();
    }

    // MARK: - Storage

    private Payload LoadPayload(Guid transcriptId)
    {
        if (FilePath(transcriptId) is not { } path) return new Payload();
        try
        {
            if (!File.Exists(path)) return new Payload();
            return JsonSerializer.Deserialize<Payload>(File.ReadAllBytes(path), Json) ?? new Payload();
        }
        catch { return new Payload(); }
    }

    // No loadFailed guard here, unlike CorrectionStore: provenance is per-transcript display/anchor
    // state that the gate can regenerate, whereas corrections.json is the owner's curated learning
    // and is irreplaceable. Swift makes the same split.
    private void Persist(Guid transcriptId, Payload p)
    {
        if (FilePath(transcriptId) is { } path) Write(path, p);
    }

    private static bool Write(string path, Payload p)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(p, Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    private string? FilePath(Guid transcriptId)
    {
        if (_containerRoot is null) return null;
        string dir = Path.Combine(_containerRoot, "Vocabulary", "provenance");
        try { Directory.CreateDirectory(dir); } catch { /* the caller's try? equivalent */ }
        return Path.Combine(dir, FileStem(transcriptId) + ".json");
    }

    // Swift names these files after `UUID.uuidString`, which is UPPERCASE. Not a case fold of any
    // identity key (so not CorrectionKey's business) — it is the shared file-naming contract, and
    // getting it wrong would orphan a ledger written by the Mac build on a shared folder.
    private static string FileStem(Guid transcriptId) => transcriptId.ToString("D").ToUpperInvariant();
}
