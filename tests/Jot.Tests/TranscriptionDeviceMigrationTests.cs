using System;
using Jot.Services;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

public class TranscriptionDeviceMigrationTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public int Saves;
        public void Save() => Saves++;
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    [Fact]
    public void LegacyCpuDefault_MigratesToAuto_Once()
    {
        var store = new FakeSettingsStore();
        store.Current.TranscriptionDevice = "CPU"; // every pre-GPU-tier user carries this
        StartupMigration.MigrateTranscriptionDevice(store);
        Assert.Equal("Auto", store.Current.TranscriptionDevice);
        Assert.True(store.Current.TranscriptionDeviceMigrated);
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public void ExplicitGpuChoice_IsPreserved()
    {
        var store = new FakeSettingsStore();
        store.Current.TranscriptionDevice = "GPU (DirectML)";
        StartupMigration.MigrateTranscriptionDevice(store);
        Assert.Equal("GPU (DirectML)", store.Current.TranscriptionDevice);
        Assert.True(store.Current.TranscriptionDeviceMigrated);
    }

    [Fact]
    public void ExplicitCpuAfterMigration_IsSticky()
    {
        var store = new FakeSettingsStore();
        store.Current.TranscriptionDevice = "CPU";
        StartupMigration.MigrateTranscriptionDevice(store);   // CPU -> Auto
        store.Current.TranscriptionDevice = "CPU";            // user deliberately picks CPU in Settings
        StartupMigration.MigrateTranscriptionDevice(store);   // must NOT flip it back to Auto
        Assert.Equal("CPU", store.Current.TranscriptionDevice);
    }

    [Fact]
    public void FreshInstall_DefaultsToAuto_AndMarkerIsSet()
    {
        var store = new FakeSettingsStore();
        Assert.Equal("Auto", store.Current.TranscriptionDevice); // the new JotSettings default
        StartupMigration.MigrateTranscriptionDevice(store);
        Assert.Equal("Auto", store.Current.TranscriptionDevice);
        Assert.True(store.Current.TranscriptionDeviceMigrated);
    }
}
