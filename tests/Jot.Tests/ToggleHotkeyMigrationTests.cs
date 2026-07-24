using System;
using Jot.Services;
using Jot.Services.Abstractions;
using Xunit;

namespace Jot.Tests;

public class ToggleHotkeyMigrationTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    [Fact]
    public void LegacyAltSpaceDefault_IsRescuedToCtrlShiftSpace()
    {
        var store = new FakeSettingsStore();
        store.Current.ToggleRecordingHotkey = "Alt+Space"; // the pre-2026-07-23 shipped default
        StartupMigration.MigrateToggleHotkey(store);
        Assert.Equal("Ctrl+Shift+Space", store.Current.ToggleRecordingHotkey);
        Assert.True(store.Current.ToggleHotkeyMigrated);
    }

    [Fact]
    public void DeliberateAltSpaceRepick_AfterMigration_IsSticky()
    {
        var store = new FakeSettingsStore();
        store.Current.ToggleRecordingHotkey = "Alt+Space";
        StartupMigration.MigrateToggleHotkey(store);          // rescued
        store.Current.ToggleRecordingHotkey = "Alt+Space";    // user really wants it back
        StartupMigration.MigrateToggleHotkey(store);          // must NOT touch it again
        Assert.Equal("Alt+Space", store.Current.ToggleRecordingHotkey);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Space")] // current default
    [InlineData("F13")]              // custom choices are real choices
    [InlineData("Ctrl+Alt+D")]
    public void AnyOtherChord_IsUntouched(string chord)
    {
        var store = new FakeSettingsStore();
        store.Current.ToggleRecordingHotkey = chord;
        StartupMigration.MigrateToggleHotkey(store);
        Assert.Equal(chord, store.Current.ToggleRecordingHotkey);
        Assert.True(store.Current.ToggleHotkeyMigrated);
    }
}
