using System;
using System.IO;
using Jot.Services.Abstractions;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

/// <summary>Mirrors NemotronModelLocationTests: the fp16 model must resolve its folder EXACTLY like the
/// int4 model, or a moved data folder would carry one model and strand the other.</summary>
public class Fp16ModelLocationTests
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
        var model = new NemotronFp16Model(settings: store);
        Assert.Equal(Path.Combine(@"D:\MyJot", "models", NemotronFp16Model.ModelFolder), model.Directory);
    }

    [Fact]
    public void Directory_FallsBackToLocalAppData_WhenNoDataDirectory()
    {
        var model = new NemotronFp16Model(settings: new FakeSettingsStore()); // DataDirectory null
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jot", "models", NemotronFp16Model.ModelFolder);
        Assert.Equal(expected, model.Directory);
    }

    [Fact]
    public void Directory_ExplicitOverrideWins_OverSettings()
    {
        var store = new FakeSettingsStore();
        store.Current.DataDirectory = @"D:\MyJot";
        var model = new NemotronFp16Model(directory: @"C:\override\model", settings: store);
        Assert.Equal(@"C:\override\model", model.Directory);
    }
}
