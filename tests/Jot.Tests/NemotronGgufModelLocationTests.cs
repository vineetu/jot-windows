using System;
using System.IO;
using Jot.Services.Abstractions;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

public class NemotronGgufModelLocationTests
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
        var model = new NemotronGgufModel(settings: store, env: _ => null);
        Assert.Equal(Path.Combine(@"D:\MyJot", "models", NemotronGgufModel.ModelFolder), model.Directory);
        Assert.Equal(
            Path.Combine(@"D:\MyJot", "models", NemotronGgufModel.ModelFolder, NemotronGgufModel.FileName),
            model.ModelPath);
    }

    [Fact]
    public void Directory_FallsBackToLocalAppData_WhenNoDataDirectory()
    {
        var model = new NemotronGgufModel(settings: new FakeSettingsStore(), env: _ => null);
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jot", "models", NemotronGgufModel.ModelFolder);
        Assert.Equal(expected, model.Directory);
    }

    [Fact]
    public void Directory_ExplicitOverrideWins_OverSettings()
    {
        var store = new FakeSettingsStore();
        store.Current.DataDirectory = @"D:\MyJot";
        var model = new NemotronGgufModel(directory: @"C:\override\model", settings: store, env: _ => null);
        Assert.Equal(@"C:\override\model", model.Directory);
    }

    [Fact]
    public void EnvFileOverride_WinsOverEverything()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "jot-gguf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        string file = Path.Combine(tmp, NemotronGgufModel.FileName);
        File.WriteAllBytes(file, [0]);
        try
        {
            var store = new FakeSettingsStore();
            store.Current.DataDirectory = @"D:\MyJot";
            var model = new NemotronGgufModel(
                directory: @"C:\override\model",
                settings: store,
                env: name => name == NemotronGgufModel.PathEnvVar ? file : null);
            Assert.Equal(file, model.ModelPath);
            Assert.Equal(tmp, model.Directory);
            Assert.True(model.IsInstalled);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void MissingFile_IsNotInstalled_DoesNotThrow()
    {
        var model = new NemotronGgufModel(directory: @"C:\nope\gguf-missing", env: _ => null);
        Assert.False(model.IsInstalled);
    }
}
