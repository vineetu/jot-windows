using System.IO;
using System.Text.Json;
using Jot.Cli;
using Jot.Services.Abstractions;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Root resolution is the CLI's one un-shareable piece of knowledge — a console exe has no MSIX package
/// identity, so it cannot ask JotPaths where the Store build keeps its data and has to probe. Picking the
/// wrong root means "no model found" on a machine that has one.
/// </summary>
public class CliPathsTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "jot-clipaths-" + Guid.NewGuid().ToString("N"));

    public CliPathsTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Root(string name)
    {
        string path = Path.Combine(_tmp, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void InstallGguf(string modelsParent)
    {
        string dir = Path.Combine(modelsParent, NemotronGgufModel.ModelFolder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, NemotronGgufModel.FileName), "x");
    }

    private static void WriteSettings(string root, JotSettings s) =>
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(s));

    [Fact]
    public void FirstRootWithAModelWins()
    {
        string a = Root("a"), b = Root("b");
        InstallGguf(Path.Combine(a, "models"));
        InstallGguf(Path.Combine(b, "models"));

        ResolvedPaths r = CliPaths.Resolve(null, null, [a, b], a);
        Assert.Equal(a, r.Root);
        Assert.Equal(Path.Combine(a, "models"), r.ModelsParent);
    }

    [Fact]
    public void RootWithoutAModelIsSkipped()
    {
        string a = Root("a"), b = Root("b");
        InstallGguf(Path.Combine(b, "models"));

        Assert.Equal(b, CliPaths.Resolve(null, null, [a, b], a).Root);
    }

    [Fact]
    public void UnreadableRootDoesNotStopTheProbe()
    {
        string missingDrive = @"Q:\nope\Jot";
        string b = Root("b");
        InstallGguf(Path.Combine(b, "models"));

        Assert.Equal(b, CliPaths.Resolve(null, null, [missingDrive, b], b).Root);
    }

    [Fact]
    public void DataDirectoryRedirectIsHonoured()
    {
        string a = Root("a"), moved = Root("moved");
        InstallGguf(Path.Combine(moved, "models"));
        WriteSettings(a, new JotSettings { DataDirectory = moved });

        ResolvedPaths r = CliPaths.Resolve(null, null, [a], a);
        Assert.Equal(a, r.Root);                                        // tools\ still hangs off here
        Assert.Equal(moved, r.DataRoot);
        Assert.Equal(Path.Combine(moved, "models"), r.ModelsParent);
        Assert.Equal(Path.Combine(moved, "Vocabulary"), r.VocabularyDir);
        Assert.Equal(Path.Combine(a, "tools"), r.ToolsDir);
    }

    [Fact]
    public void DataDirOverrideSkipsTheProbeEntirely()
    {
        string a = Root("a"), explicitRoot = Root("explicit");
        InstallGguf(Path.Combine(a, "models"));
        WriteSettings(explicitRoot, new JotSettings { Language = "de-DE" });

        ResolvedPaths r = CliPaths.Resolve(null, explicitRoot, [a], a);
        Assert.Equal(explicitRoot, r.DataRoot);
        Assert.Equal("de-DE", r.Settings.Language);
    }

    [Fact]
    public void ModelDirOverrideIsTheModelsParentNotTheLeaf()
    {
        string a = Root("a"), models = Root("models-elsewhere");
        InstallGguf(models);

        ResolvedPaths r = CliPaths.Resolve(models, null, [a], a);
        Assert.Equal(models, r.ModelsParent);
        Assert.Equal(Path.Combine(models, NemotronGgufModel.ModelFolder), r.GgufDir);
    }

    [Fact]
    public void NothingInstalledFallsBackWithoutThrowing()
    {
        string a = Root("a"), fallback = Root("fallback");

        ResolvedPaths r = CliPaths.Resolve(null, null, [a], fallback);
        Assert.Equal(fallback, r.Root);
        Assert.Equal(Path.Combine(fallback, "models"), r.ModelsParent);
    }

    [Fact]
    public void CorruptSettingsFallBackToDefaults()
    {
        string a = Root("a");
        InstallGguf(Path.Combine(a, "models"));
        File.WriteAllText(Path.Combine(a, "settings.json"), "{ not json");

        ResolvedPaths r = CliPaths.Resolve(null, null, [a], a);
        Assert.Equal(a, r.DataRoot);
        Assert.Equal(new JotSettings().Language, r.Settings.Language);
    }

    [Fact]
    public void DefaultRootsProbeTheUnpackagedRootFirst()
    {
        string localAppData = Root("LocalAppData");
        IReadOnlyList<string> roots = CliPaths.DefaultRoots(localAppData);
        Assert.Equal(Path.Combine(localAppData, "Jot"), roots[0]);
    }

    [Fact]
    public void DefaultRootsFindContainersByTheirModelFolderNotByPackageName()
    {
        string localAppData = Root("LocalAppData");
        string cache = Path.Combine(localAppData, "Packages", "SomeGeneratedName_abc123", "LocalCache");
        Directory.CreateDirectory(Path.Combine(cache, "models", "nemotron-3.5-whatever"));
        Directory.CreateDirectory(
            Path.Combine(localAppData, "Packages", "Unrelated.App_xyz", "LocalCache"));

        Assert.Contains(cache, CliPaths.DefaultRoots(localAppData));
        Assert.Single(CliPaths.DefaultRoots(localAppData), r => r.Contains("Packages"));
    }
}
