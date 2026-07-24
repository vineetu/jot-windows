using System;
using System.IO;
using System.Linq;
using Jot.Services;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The unified wipe: one artifact list covering data (any drive) + config, a purge that removes every one
/// of them, a safety guard that never nukes a shared folder, and the deferred-wipe marker roundtrip that
/// makes "Erase all data" reliable. All against temp dirs; registry removal is disabled (removeLaunchEntry:false)
/// so the tests never touch the real HKCU Run key.
/// </summary>
public class JotDataPurgeTests : IDisposable
{
    private readonly string _root;
    private readonly string _data;
    private readonly string _config;

    public JotDataPurgeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jot-purgetest-" + Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_root, "data");
        _config = Path.Combine(_root, "config");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_config);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Seed one of every artifact a real install has, split across a custom data dir and the config dir.
    private void SeedEverything()
    {
        Write(Path.Combine(_data, "models", "nemotron", "encoder.onnx"), "M");
        Write(Path.Combine(_data, "recordings", "r1.wav"), "A");
        Write(Path.Combine(_data, "logs", "jot.log"), "L");
        Write(Path.Combine(_data, "library.json"), "[]");
        Write(Path.Combine(_data, "aikey.dat"), "KEY");     // the credential store (incl. the PFB token)
        Write(Path.Combine(_data, "stats.json"), "{}");
        Write(Path.Combine(_config, "tools", "ffmpeg.exe"), "F");
        Write(Path.Combine(_config, "settings.json"), "{}");
        Write(Path.Combine(_config, "prompts.json"), "[]");
        Write(Path.Combine(_config, "migration.json"), "{}");
    }

    [Fact]
    public void ArtifactPaths_CoverEveryKnownArtifact()
    {
        var paths = JotDataPurge.ArtifactPaths(_data, _config).ToList();
        // Data folder (any drive).
        Assert.Contains(Path.Combine(_data, "models"), paths);
        Assert.Contains(Path.Combine(_data, "recordings"), paths);
        Assert.Contains(Path.Combine(_data, "logs"), paths);
        Assert.Contains(Path.Combine(_data, "library.json"), paths);
        Assert.Contains(Path.Combine(_data, "aikey.dat"), paths);       // credential store must be in the list
        Assert.Contains(Path.Combine(_data, "stats.json"), paths);
        // Config folder.
        Assert.Contains(Path.Combine(_config, "tools"), paths);
        Assert.Contains(Path.Combine(_config, "settings.json"), paths);
        Assert.Contains(Path.Combine(_config, "prompts.json"), paths);
        Assert.Contains(Path.Combine(_config, "migration.json"), paths);
    }

    [Fact]
    public void PurgeAll_RemovesEverything_OnAnyDrive()
    {
        SeedEverything();

        JotDataPurge.PurgeAll(_data, _config, removeLaunchEntry: false);

        foreach (string p in JotDataPurge.ArtifactPaths(_data, _config))
            Assert.False(File.Exists(p) || Directory.Exists(p), $"survived wipe: {p}");
        // The now-empty folders are removed too.
        Assert.False(Directory.Exists(_data));
        Assert.False(Directory.Exists(_config));
    }

    [Fact]
    public void PurgeAll_LeavesUnrelatedFilesInASharedFolder()
    {
        // A user pointed the data dir at a shared folder that also holds their own files.
        SeedEverything();
        Write(Path.Combine(_data, "my-taxes.xlsx"), "PRIVATE");

        JotDataPurge.PurgeAll(_data, _config, removeLaunchEntry: false);

        Assert.True(File.Exists(Path.Combine(_data, "my-taxes.xlsx"))); // untouched
        Assert.True(Directory.Exists(_data));                          // not removed — still has the user's file
        Assert.False(Directory.Exists(Path.Combine(_data, "models"))); // but Jot's own children are gone
        Assert.False(File.Exists(Path.Combine(_data, "aikey.dat")));
    }

    [Fact]
    public void DeferredWipe_MarkerRoundtrip_PurgesRecordedDataDir()
    {
        SeedEverything();

        // Erase writes the marker (capturing the custom data dir) instead of deleting from the live process.
        JotDataPurge.RequestWipe(_data, _config);
        Assert.True(File.Exists(Path.Combine(_config, JotDataPurge.WipeMarkerFile)));

        // Next launch consumes it and wipes the recorded data dir + config.
        bool ran = JotDataPurge.ConsumePendingWipe(_config, removeLaunchEntry: false);

        Assert.True(ran);
        Assert.False(Directory.Exists(Path.Combine(_data, "models")));
        Assert.False(File.Exists(Path.Combine(_data, "aikey.dat")));
        Assert.False(File.Exists(Path.Combine(_config, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_config, JotDataPurge.WipeMarkerFile))); // marker cleaned up
    }

    [Fact]
    public void ConsumePendingWipe_IsNoOp_WithoutAMarker()
    {
        SeedEverything();
        Assert.False(JotDataPurge.ConsumePendingWipe(_config, removeLaunchEntry: false));
        Assert.True(File.Exists(Path.Combine(_config, "settings.json"))); // nothing deleted
    }
}
