using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jot.Services;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Covers the data-folder move: it carries model + recordings + library to the new folder, repoints the
/// setting, cleans up the old folder, and — the load-bearing part — resumes a crash-interrupted move
/// idempotently from its marker. All against temp dirs (configDir override) so the real user config is safe.
/// </summary>
public class DataFolderMigratorTests : IDisposable
{
    private readonly string _root;

    public DataFolderMigratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jot-migtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    private sealed class FakeStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public int SaveCount;
        public void Save() { SaveCount++; Changed?.Invoke(this, EventArgs.Empty); }
        public void Reset() { }
        public event EventHandler? Changed;
    }

    private string Dir(string name)
    {
        string p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // A model folder (graph + external weights), a recording, and the library — the three things a move carries.
    private static void SeedData(string dir)
    {
        WriteFile(Path.Combine(dir, "models", "nemotron", "encoder.onnx"), "ENCODER");
        WriteFile(Path.Combine(dir, "models", "nemotron", "encoder.onnx.data"), new string('x', 5000));
        WriteFile(Path.Combine(dir, "recordings", "rec1.wav"), "AUDIO");
        WriteFile(Path.Combine(dir, "library.json"), "[]");
    }

    private DataFolderMigrator NewMigrator(FakeStore store) => new(store, configDir: Dir("config"));

    [Fact]
    public async Task MoveToAsync_MigratesEverything_FlipsSetting_RemovesOld()
    {
        string from = Dir("from"), to = Path.Combine(_root, "to");
        SeedData(from);
        var store = new FakeStore();
        store.Current.DataDirectory = from;
        var mig = NewMigrator(store);

        bool ok = await mig.MoveToAsync(to);

        Assert.True(ok);
        // New folder has everything, byte-identical.
        Assert.Equal("ENCODER", File.ReadAllText(Path.Combine(to, "models", "nemotron", "encoder.onnx")));
        Assert.Equal(5000, new FileInfo(Path.Combine(to, "models", "nemotron", "encoder.onnx.data")).Length);
        Assert.True(File.Exists(Path.Combine(to, "recordings", "rec1.wav")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(to, "library.json")));
        // Old folder cleaned up.
        Assert.False(Directory.Exists(Path.Combine(from, "models")));
        Assert.False(Directory.Exists(Path.Combine(from, "recordings")));
        Assert.False(File.Exists(Path.Combine(from, "library.json")));
        // Setting repointed and marker cleared.
        Assert.Equal(to, store.Current.DataDirectory);
        Assert.False(mig.HasPendingMigration);
        Assert.False(File.Exists(Path.Combine(to, "models", "nemotron", "encoder.onnx.data.part"))); // no temp left behind
    }

    [Fact]
    public async Task MoveToAsync_IsNoOp_WhenTargetEqualsCurrent()
    {
        string from = Dir("from");
        SeedData(from);
        var store = new FakeStore();
        store.Current.DataDirectory = from;
        var mig = NewMigrator(store);

        Assert.True(await mig.MoveToAsync(from));
        Assert.True(File.Exists(Path.Combine(from, "library.json"))); // untouched — nothing moved or deleted
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task MoveToAsync_Rejects_MovingIntoOwnSubfolder()
    {
        string from = Dir("from");
        SeedData(from);
        var store = new FakeStore();
        store.Current.DataDirectory = from;
        var mig = NewMigrator(store);

        Assert.False(await mig.MoveToAsync(Path.Combine(from, "inner")));
        Assert.Equal(from, store.Current.DataDirectory);           // setting unchanged
        Assert.True(File.Exists(Path.Combine(from, "library.json")));
    }

    [Fact]
    public void ResumePending_WithNoMarker_IsNoOp()
    {
        var mig = NewMigrator(new FakeStore());
        mig.ResumePending();                 // must not throw
        Assert.False(mig.HasPendingMigration);
    }

    [Fact]
    public void ResumePending_FinishesInterruptedMove_AndCorrectsPartialDest()
    {
        // Simulate a crash mid-move: marker present, source intact, dest holds a stale/partial file the
        // resume must overwrite from the source (size differs → not treated as already-copied).
        string from = Dir("from"), to = Path.Combine(_root, "to"), config = Dir("config");
        SeedData(from);
        var store = new FakeStore();
        store.Current.DataDirectory = from;

        File.WriteAllText(Path.Combine(config, "migration.json"),
            JsonSerializer.Serialize(new { From = from, To = to }));
        WriteFile(Path.Combine(to, "library.json"), "STALE-PARTIAL");

        var mig = new DataFolderMigrator(store, configDir: config);
        Assert.True(mig.HasPendingMigration);

        mig.ResumePending();

        Assert.Equal("[]", File.ReadAllText(Path.Combine(to, "library.json")));   // corrected from source
        Assert.Equal("ENCODER", File.ReadAllText(Path.Combine(to, "models", "nemotron", "encoder.onnx")));
        Assert.Equal(to, store.Current.DataDirectory);
        Assert.False(Directory.Exists(Path.Combine(from, "models")));             // old cleaned up
        Assert.False(mig.HasPendingMigration);                                    // marker cleared
    }

    [Fact]
    public void ResumePending_IsIdempotent_WhenSourceAlreadyDeleted()
    {
        // Crash during the cleanup phase: setting already flipped to `to`, `to` complete, `from` gone.
        // Resume must finish cleanly (flip is a no-op, nothing left to copy) and just clear the marker.
        string from = Path.Combine(_root, "from-gone"), to = Dir("to"), config = Dir("config");
        SeedData(to);
        var store = new FakeStore();
        store.Current.DataDirectory = to;

        File.WriteAllText(Path.Combine(config, "migration.json"),
            JsonSerializer.Serialize(new { From = from, To = to }));

        var mig = new DataFolderMigrator(store, configDir: config);
        mig.ResumePending();

        Assert.Equal("[]", File.ReadAllText(Path.Combine(to, "library.json"))); // dest preserved intact
        Assert.Equal(to, store.Current.DataDirectory);
        Assert.False(mig.HasPendingMigration);
    }
}
