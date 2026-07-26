using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// THE CROSS-PLATFORM CONFORMANCE CONTRACT for the learn loop's two persistent stores, replayed from
/// the same JSON files the Swift package runs (jot-shared `Tests/JotVocabCoreTests/Fixtures`, pinned
/// commit 5326460). A failure here means the Windows store has diverged from shipping Mac/iOS
/// behaviour, and that divergence is the bug — not the fixture.
///
/// The load-bearing case is `decode-failure-must-not-overwrite`: a corrections.json that EXISTS but
/// cannot be decoded must leave the ledger's bytes untouched forever after and say so on the
/// diagnostics sink. The failure mode it guards is silent and total — the naive path starts empty and
/// the next save writes `{}` over everything the owner ever taught.
/// </summary>
public class VocabStoreProvenanceTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static T Load<T>(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vocab", file + ".json");
        Assert.True(File.Exists(path), $"Missing fixture: {path}");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)!;
    }

    private static string MakeTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "jot-vocabtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingSink : IDiagnosticsSink
    {
        private readonly object _lock = new();
        private readonly List<(DiagnosticsCategory Category, string Message)> _events = [];

        public void Record(DiagnosticsCategory category, string message, IReadOnlyDictionary<string, string> metadata)
        {
            lock (_lock) { _events.Add((category, message)); }
        }

        public IReadOnlyList<DiagnosticsCategory> Categories
        {
            get { lock (_lock) { return _events.Select(e => e.Category).ToList(); } }
        }
    }

    // MARK: - 1 · CorrectionStore round-trip + the decode-failure guard

    private sealed record StoreOp(string Op, string? OriginalWord, string? Term, int? Delta);

    private sealed record SnapshotExpect(string OriginalWord, string Term, int Net, bool AlwaysReplace);

    private sealed record StoreCase(
        string Name,
        string InitialJSON,
        List<StoreOp> Operations,
        List<SnapshotExpect>? ExpectSnapshot,
        List<string>? ExpectKeyboardSuppressed,
        List<string>? ExpectMergeAsked,
        List<string>? ExpectFileContains,
        bool? ExpectFileUnchanged,
        string? ExpectDiagnosticsCategory);

    [Fact]
    public void CorrectionStoreRoundtrip_MatchesGolden()
    {
        var cases = Load<List<StoreCase>>("correction_store_roundtrip");
        Assert.NotEmpty(cases);

        foreach (StoreCase c in cases)
        {
            string root = MakeTempRoot();
            try
            {
                string vocabDir = Path.Combine(root, "Vocabulary");
                Directory.CreateDirectory(vocabDir);
                string file = Path.Combine(vocabDir, "corrections.json");
                File.WriteAllText(file, c.InitialJSON);

                var sink = new CapturingSink();
                var store = new CorrectionStore(root, sink);

                foreach (StoreOp op in c.Operations)
                {
                    string ow = op.OriginalWord ?? "";
                    string term = op.Term ?? "";
                    switch (op.Op)
                    {
                        case "confirm": store.Confirm(ow, term); break;
                        case "revert": store.Revert(ow, term); break;
                        case "adjust": store.Adjust(ow, term, op.Delta ?? 0); break;
                        case "grantAlwaysReplace": store.GrantAlwaysReplace(ow, term); break;
                        case "suppressBlock": store.SuppressBlock(ow, term); break;
                        case "noteBlockedKeep": store.NoteBlockedKeep(ow, term); break;
                        case "clearBlockedKeep": store.ClearBlockedKeep(ow, term); break;
                        case "noteMergeAsked": store.NoteMergeAsked(ow, term); break;
                        default: Assert.Fail($"store[{c.Name}] unknown op {op.Op}"); break;
                    }
                }

                foreach (SnapshotExpect e in c.ExpectSnapshot ?? [])
                {
                    var found = store.Snapshot()
                        .Where(x => x.OriginalWord == CorrectionKey.Normalize(e.OriginalWord) && x.Term == e.Term)
                        .ToList();
                    Assert.True(found.Count > 0,
                        $"store[{c.Name}] snapshot missing {e.OriginalWord}→{e.Term}");
                    Assert.True(e.Net == found[0].Net,
                        $"store[{c.Name}] net expected {e.Net}, got {found[0].Net}");
                    Assert.True(e.AlwaysReplace == found[0].AlwaysReplace,
                        $"store[{c.Name}] alwaysReplace expected {e.AlwaysReplace}");
                }

                if (c.ExpectKeyboardSuppressed is { } suppressed)
                {
                    Assert.True(suppressed.ToHashSet().SetEquals(store.KeyboardSuppressedPairs()),
                        $"store[{c.Name}] keyboardSuppressed expected [{string.Join(", ", suppressed)}], " +
                        $"got [{string.Join(", ", store.KeyboardSuppressedPairs())}]");
                }

                if (c.ExpectMergeAsked is { } merged)
                {
                    Assert.True(merged.ToHashSet().SetEquals(store.MergeAskedPairs()),
                        $"store[{c.Name}] mergeAsked expected [{string.Join(", ", merged)}], " +
                        $"got [{string.Join(", ", store.MergeAskedPairs())}]");
                }

                if (c.ExpectFileUnchanged == true)
                {
                    Assert.True(c.InitialJSON == File.ReadAllText(file),
                        $"store[{c.Name}] file must be UNCHANGED (no overwrite)");
                }

                foreach (string needle in c.ExpectFileContains ?? [])
                {
                    Assert.True(File.ReadAllText(file).Contains(needle, StringComparison.Ordinal),
                        $"store[{c.Name}] persisted file should contain {needle}");
                }

                if (c.ExpectDiagnosticsCategory is { } cat)
                {
                    DiagnosticsCategory expected = cat == "vocabularySaveFailed"
                        ? DiagnosticsCategory.VocabularySaveFailed
                        : DiagnosticsCategory.VocabularyGate;
                    Assert.True(sink.Categories.Contains(expected), $"store[{c.Name}] should emit {cat}");
                }
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
        }
    }

    /// <summary>
    /// The data-loss guard, pinned harder than the fixture pins it: after the failed decode the store
    /// stays writable-looking (every verdict is accepted) but NOTHING reaches disk — not on the first
    /// save and not on any later one — and a fresh store still reads the owner's original bytes back.
    /// One `persist()` slipping through here silently destroys a hand-curated ledger.
    /// </summary>
    [Fact]
    public void CorruptLedger_IsNeverOverwritten_HoweverManyVerdictsFollow()
    {
        string root = MakeTempRoot();
        try
        {
            string file = Path.Combine(root, "Vocabulary", "corrections.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            const string curated = "{\"jamy\":{\"mappings\":[{\"originalWord\":\"jamie\",\"term\":\"Jamy\",TRUNCATED";
            File.WriteAllText(file, curated);

            var sink = new CapturingSink();
            var store = new CorrectionStore(root, sink);
            store.Confirm("jamie", "Jamy");
            store.GrantAlwaysReplace("jamie", "Jamy");
            store.SuppressBlock("cloud", "Claude");
            store.NoteMergeAsked("sri ram", "Sriram");
            store.Adjust("jamie", "Jamy", -1);

            Assert.Equal(curated, File.ReadAllText(file));
            Assert.Contains(DiagnosticsCategory.VocabularySaveFailed, sink.Categories);
            // No stray temp left behind either — the refusal happens before any write.
            Assert.False(File.Exists(file + ".tmp"));
            // And a brand-new store still sees the same undecodable bytes, i.e. recovery is possible.
            Assert.Equal(curated, File.ReadAllText(file));
            Assert.Empty(new CorrectionStore(root, sink).Snapshot());
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>An ABSENT or EMPTY ledger is the safe case and must NOT trip the guard — the store
    /// starts empty and persists normally, or a first-run install could never learn anything.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentOrEmptyLedger_StartsEmptyAndPersists(bool writeEmptyFile)
    {
        string root = MakeTempRoot();
        try
        {
            string file = Path.Combine(root, "Vocabulary", "corrections.json");
            if (writeEmptyFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "");
            }

            var sink = new CapturingSink();
            var store = new CorrectionStore(root, sink);
            store.Confirm("jamie", "Jamy");

            Assert.DoesNotContain(DiagnosticsCategory.VocabularySaveFailed, sink.Categories);
            Assert.Contains("jamie", File.ReadAllText(file), StringComparison.Ordinal);
            OverrideEntry entry = Assert.Single(new CorrectionStore(root).Snapshot());
            Assert.Equal(1, entry.Net);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The produce-key / check-key invariant in ONE case: pipe the store's own
    /// <c>KeyboardSuppressedPairs()</c> straight into <see cref="AskPolicy.Select"/> and assert
    /// suppression actually fires. If the two ever normalized the pair key differently the record
    /// would leak through as an ask, and the owner gets nagged about something they already answered.
    /// </summary>
    [Fact]
    public void KeyboardSuppressedPairs_AgreeByteForByteWithAskPolicy()
    {
        var store = new CorrectionStore(containerRoot: null);   // in-memory: no load, no persist
        // "Stop asking" on a punctuated, mixed-case original, so normalization is actually exercised.
        store.SuppressBlock("Cloud.", "Claude");
        IReadOnlySet<string> suppressed = store.KeyboardSuppressedPairs();
        Assert.Single(suppressed);
        Assert.Contains("cloud|claude", suppressed);

        var record = new CorrectionRecord
        {
            OriginalWord = "cloud",
            Term = "Claude",
            Decision = "APPLY",
            Outcome = "applied",
            Confidence = 0.85f,
            Margin = 3.5f,
            Unsure = false,
            OccurrenceIndex = 0,
            OriginalStart = 0,
            OriginalLength = 5,
            PublishedStart = 0,
            PublishedLength = 6,
        };

        // Control: without the suppressed set it IS selected.
        Assert.Single(AskPolicy.Select([record], [], new HashSet<string>(), new HashSet<string>()));
        Assert.Empty(AskPolicy.Select([record], [], suppressed, new HashSet<string>()));
    }

    /// <summary>A null container root is the test/no-persist mode: it must never touch the disk, so a
    /// unit test can't leave a Vocabulary folder in the user's data dir.</summary>
    [Fact]
    public void NullContainerRoot_IsPurelyInMemory()
    {
        var store = new CorrectionStore(containerRoot: null);
        store.Confirm("jamie", "Jamy");
        Assert.Single(store.Snapshot());
        Assert.Equal(1, store.Snapshot()[0].Net);
    }

    // MARK: - 2 · CorrectionProvenance — anchor mapping + verdict deltas

    private sealed record MapOffsetsCase(string Name, List<int> Offsets, string Old, string New, List<int> Expected);

    private sealed record VerdictOp(string Op, string RecordKey, string? Verdict);

    private sealed record DeltaExpect(string OriginalWord, string Term, int Delta);

    private sealed record VerdictCase(
        string Name, string SeedPayloadJSON, List<VerdictOp> Operations, List<List<DeltaExpect>> ExpectDeltas);

    private sealed record ProvenanceFixture(List<MapOffsetsCase> MapOffsets, List<VerdictCase> Verdicts);

    [Fact]
    public void ProvenanceMapOffsets_MatchesGolden()
    {
        ProvenanceFixture fixture = Load<ProvenanceFixture>("provenance_verdicts");
        Assert.NotEmpty(fixture.MapOffsets);

        foreach (MapOffsetsCase c in fixture.MapOffsets)
        {
            IReadOnlyList<int> got = CorrectionProvenance.MapOffsets(c.Offsets, c.Old, c.New);
            Assert.True(c.Expected.SequenceEqual(got),
                $"mapOffsets[{c.Name}] expected [{string.Join(", ", c.Expected)}], got [{string.Join(", ", got)}]");
        }
    }

    [Fact]
    public void ProvenanceVerdictDeltas_MatchesGolden()
    {
        ProvenanceFixture fixture = Load<ProvenanceFixture>("provenance_verdicts");
        Assert.NotEmpty(fixture.Verdicts);

        foreach (VerdictCase c in fixture.Verdicts)
        {
            string root = MakeTempRoot();
            try
            {
                var id = Guid.NewGuid();
                string dir = Path.Combine(root, "Vocabulary", "provenance");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, id.ToString("D").ToUpperInvariant() + ".json"), c.SeedPayloadJSON);

                var prov = new CorrectionProvenance(root);
                for (int i = 0; i < c.Operations.Count; i++)
                {
                    VerdictOp op = c.Operations[i];
                    CorrectionRecord? record = prov.PayloadFor(id).Records.FirstOrDefault(r => r.Key == op.RecordKey);
                    Assert.True(record is not null, $"provenance[{c.Name}] no record for key {op.RecordKey}");

                    IReadOnlyList<CorrectionProvenance.MappingDelta> deltas = op.Op switch
                    {
                        "setVerdict" => prov.SetVerdict(id, record!, op.Verdict ?? ""),
                        "clearVerdict" => prov.ClearVerdict(id, record!),
                        _ => throw new Xunit.Sdk.XunitException($"provenance[{c.Name}] unknown op {op.Op}"),
                    };

                    List<DeltaExpect> expected = c.ExpectDeltas[i];
                    Assert.True(expected.Count == deltas.Count,
                        $"provenance[{c.Name}] op{i} delta count expected {expected.Count}, got {deltas.Count}");
                    for (int j = 0; j < expected.Count && j < deltas.Count; j++)
                    {
                        Assert.True(expected[j].OriginalWord == deltas[j].OriginalWord,
                            $"provenance[{c.Name}] op{i} delta[{j}].originalWord");
                        Assert.True(expected[j].Term == deltas[j].Term,
                            $"provenance[{c.Name}] op{i} delta[{j}].term");
                        Assert.True(expected[j].Delta == deltas[j].Delta,
                            $"provenance[{c.Name}] op{i} delta[{j}].delta");
                    }
                }
            }
            finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
        }
    }

    /// <summary>
    /// The commit→reconcile chain end to end, which no fixture reaches: anchors captured against the
    /// GATE-OUTPUT text must survive a post-gate transform that shifts them. This is the whole reason
    /// the payload stores its own baseline text instead of trusting the caller to report edits.
    /// </summary>
    [Fact]
    public void Reconcile_ShiftsAnchorsAcrossAPostGateEdit()
    {
        string root = MakeTempRoot();
        try
        {
            var prov = new CorrectionProvenance(root);
            var id = Guid.NewGuid();
            const string gated = "hello Sriram now";
            prov.Record(
                [new VocabularyGate.Proposal(
                    "shriram", "Sriram", "APPLY", "applied", 0.85f, 0f, false, true, 0,
                    OriginalStart: 6, OriginalLength: 7, PublishedStart: 6, PublishedLength: 6,
                    Alternates: [], Shape: null)],
                gated);
            prov.Commit(id);

            // A filler sweep inserts a word ahead of the span; the anchor must move with it.
            CorrectionProvenance.Payload p = prov.ReconciledPayload(id, "hello there Sriram now");
            Assert.Equal(12, Assert.Single(p.Records).PublishedStart);

            // The shift is durable: the next read starts from the new baseline and does not re-shift.
            Assert.Equal(12, Assert.Single(prov.ReconciledPayload(id, "hello there Sriram now").Records).PublishedStart);

            // MappedPayload is a one-off view — it must NOT move the durable baseline.
            Assert.Equal(17, Assert.Single(prov.MappedPayload(id, "well hello there Sriram now").Records).PublishedStart);
            Assert.Equal(12, Assert.Single(prov.ReconciledPayload(id, "hello there Sriram now").Records).PublishedStart);

            prov.Discard(id);
            Assert.Empty(prov.PayloadFor(id).Records);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    // MARK: - 3 · Storage registration — the folder must be erasable and movable

    /// <summary>
    /// Asserts against the REAL purge and migration lists, not copies of them. A vocabulary folder
    /// registered in one and forgotten in the other is a shipped bug class here: forgotten by the
    /// purge it survives "Erase all data" (which the app promises removes everything the owner
    /// dictated), and forgotten by the migrator it is stranded on the old drive when the data folder
    /// moves, silently resetting everything the owner taught.
    /// </summary>
    [Fact]
    public void VocabularyFolder_IsRegisteredForBothPurgeAndMigration()
    {
        string data = Path.Combine(Path.GetTempPath(), "jot-vocab-paths", "data");
        string config = Path.Combine(Path.GetTempPath(), "jot-vocab-paths", "config");
        var settings = new JotSettings { DataDirectory = data };

        string vocab = JotPaths.VocabularyDir(settings);
        Assert.Equal(Path.Combine(data, JotPaths.VocabularyFolderName), vocab);

        Assert.Contains(vocab, JotDataPurge.ArtifactPaths(data, config));
        Assert.Contains(JotPaths.VocabularyFolderName, DataFolderMigrator.MigratedItems);
    }

    /// <summary>Registration is necessary but not sufficient — actually run the purge over a real
    /// ledger and assert the bytes are gone.</summary>
    [Fact]
    public void PurgeAll_ErasesTheVocabularyLedger()
    {
        string root = MakeTempRoot();
        try
        {
            string data = Path.Combine(root, "data");
            string config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);

            var store = new CorrectionStore(data);
            store.Confirm("jamie", "Jamy");
            var prov = new CorrectionProvenance(data);
            var id = Guid.NewGuid();
            prov.Record(
                [new VocabularyGate.Proposal(
                    "jamie", "Jamy", "APPLY", "applied", 0.85f, 0f, false, true, 0, 0, 5, 0, 4, [], null)],
                "Jamy called");
            prov.Commit(id);

            string vocab = Path.Combine(data, JotPaths.VocabularyFolderName);
            Assert.True(File.Exists(Path.Combine(vocab, "corrections.json")));
            Assert.True(Directory.EnumerateFiles(Path.Combine(vocab, "provenance")).Any());

            JotDataPurge.PurgeAll(data, config, removeLaunchEntry: false);

            Assert.False(Directory.Exists(vocab), "the vocabulary folder survived Erase all data");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }

    private sealed class FakeStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() => Changed?.Invoke(this, EventArgs.Empty);
        public void Reset() { }
        public event EventHandler? Changed;
    }

    /// <summary>Same for the move: run a real migration and assert the ledger arrived and the old copy
    /// is gone, so "registered in MigratedItems" is proven rather than inspected.</summary>
    [Fact]
    public async Task DataFolderMove_CarriesTheVocabularyLedger()
    {
        string root = MakeTempRoot();
        try
        {
            string from = Path.Combine(root, "from");
            string to = Path.Combine(root, "to");
            Directory.CreateDirectory(from);

            new CorrectionStore(from).Confirm("jamie", "Jamy");

            var settings = new FakeStore();
            settings.Current.DataDirectory = from;
            var migrator = new DataFolderMigrator(settings, configDir: Path.Combine(root, "config"));

            Assert.True(await migrator.MoveToAsync(to));

            Assert.True(File.Exists(Path.Combine(to, JotPaths.VocabularyFolderName, "corrections.json")));
            Assert.False(Directory.Exists(Path.Combine(from, JotPaths.VocabularyFolderName)));
            Assert.Equal(1, new CorrectionStore(to).Snapshot()[0].Net);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ } }
    }
}
