using System;
using System.IO;
using Jot.Services.Abstractions;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

public class NemotronModelLocationTests
{
    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    [Fact]
    public void Directory_FollowsUserChosenDataDirectory()
    {
        var store = new FakeSettingsStore();
        store.Current.DataDirectory = @"D:\MyJot";
        var model = new NemotronModel(settings: store);
        Assert.Equal(Path.Combine(@"D:\MyJot", "models", NemotronModel.ModelFolder), model.Directory);
    }

    [Fact]
    public void Directory_FallsBackToLocalAppData_WhenNoDataDirectory()
    {
        var model = new NemotronModel(settings: new FakeSettingsStore()); // DataDirectory null
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jot", "models", NemotronModel.ModelFolder);
        Assert.Equal(expected, model.Directory);
    }

    [Fact]
    public void Directory_ExplicitOverrideWins_OverSettings()
    {
        var store = new FakeSettingsStore();
        store.Current.DataDirectory = @"D:\MyJot";
        var model = new NemotronModel(directory: @"C:\override\model", settings: store);
        Assert.Equal(@"C:\override\model", model.Directory);
    }
}
