using System;
using System.IO;
using Jot.Services;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The container-default upgrade glue: config is MOVED into the container (so existing users keep settings
/// and skip the wizard), while big data is only ADOPTED in place (no risky move). Both must be no-ops on a
/// fresh install and idempotent on re-run. Tested via the pure cores against temp dirs.
/// </summary>
public class StartupMigrationTests : IDisposable
{
    private readonly string _root;
    private readonly string _legacy;
    private readonly string _container;

    public StartupMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jot-migtest2-" + Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_root, "legacy");
        _container = Path.Combine(_root, "container");
        Directory.CreateDirectory(_legacy);
        Directory.CreateDirectory(_container);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void MigrateConfig_MovesConfigFiles_IntoTheContainer()
    {
        Write(Path.Combine(_legacy, "settings.json"), "{\"FirstRunComplete\":true}");
        Write(Path.Combine(_legacy, "prompts.json"), "[]");

        StartupMigration.MigrateConfig(_legacy, _container);

        Assert.Equal("{\"FirstRunComplete\":true}", File.ReadAllText(Path.Combine(_container, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_container, "prompts.json")));
        // Moved, not copied — the legacy originals are gone, so a later erase can't resurrect them.
        Assert.False(File.Exists(Path.Combine(_legacy, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "prompts.json")));
    }

    [Fact]
    public void MigrateConfig_IsNoOp_WhenContainerAlreadyHasSettings()
    {
        Write(Path.Combine(_legacy, "settings.json"), "LEGACY");
        Write(Path.Combine(_container, "settings.json"), "CONTAINER");

        StartupMigration.MigrateConfig(_legacy, _container);

        Assert.Equal("CONTAINER", File.ReadAllText(Path.Combine(_container, "settings.json"))); // not overwritten
        Assert.True(File.Exists(Path.Combine(_legacy, "settings.json")));                       // legacy untouched
    }

    [Fact]
    public void MigrateConfig_IsNoOp_OnFreshInstall()
    {
        StartupMigration.MigrateConfig(_legacy, _container); // neither side has settings
        Assert.False(File.Exists(Path.Combine(_container, "settings.json")));
    }

    [Theory]
    // no folder chosen + data in legacy + none in container -> adopt
    [InlineData(true)]
    // ... but not when the container already holds the data
    [InlineData(false)]
    public void ShouldAdoptLegacy_OnlyWhenLegacyHoldsTheOnlyData(bool legacyIsOnlyData)
    {
        Write(Path.Combine(_legacy, "library.json"), "[]");
        if (!legacyIsOnlyData) Write(Path.Combine(_container, "library.json"), "[]");

        bool adopt = StartupMigration.ShouldAdoptLegacy(dataDirectorySetting: "", _legacy, _container);

        Assert.Equal(legacyIsOnlyData, adopt);
    }

    [Fact]
    public void ShouldAdoptLegacy_False_WhenUserAlreadyChoseAFolder()
    {
        Write(Path.Combine(_legacy, "library.json"), "[]");
        Assert.False(StartupMigration.ShouldAdoptLegacy("D:\\MyJotData", _legacy, _container));
    }

    [Fact]
    public void ShouldAdoptLegacy_False_WhenLegacyEqualsRoot()
    {
        // Unpackaged: root IS the legacy folder, so there's nothing to adopt.
        Write(Path.Combine(_legacy, "library.json"), "[]");
        Assert.False(StartupMigration.ShouldAdoptLegacy("", _legacy, _legacy));
    }

    [Fact]
    public void RelocateData_MovesAllData_IntoTheContainer_SoNothingSurvivesUninstall()
    {
        Write(Path.Combine(_legacy, "models", "enc.onnx"), "M");
        Write(Path.Combine(_legacy, "recordings", "r.wav"), "A");
        Write(Path.Combine(_legacy, "library.json"), "[]");
        Write(Path.Combine(_legacy, "aikey.dat"), "K");   // the credential store moves too

        StartupMigration.RelocateData(_legacy, _container);

        Assert.Equal("M", File.ReadAllText(Path.Combine(_container, "models", "enc.onnx")));
        Assert.True(File.Exists(Path.Combine(_container, "recordings", "r.wav")));
        Assert.Equal("[]", File.ReadAllText(Path.Combine(_container, "library.json")));
        Assert.Equal("K", File.ReadAllText(Path.Combine(_container, "aikey.dat")));
        // Moved, not copied — the old location is emptied, so uninstall (which wipes the container) leaves nothing.
        Assert.False(Directory.Exists(Path.Combine(_legacy, "models")));
        Assert.False(File.Exists(Path.Combine(_legacy, "library.json")));
        Assert.False(File.Exists(Path.Combine(_legacy, "aikey.dat")));
        Assert.False(File.Exists(Path.Combine(_container, "relocating.marker"))); // marker cleared
    }

    [Fact]
    public void RelocateData_IsIdempotent_SkippingItemsAlreadyMoved()
    {
        Write(Path.Combine(_legacy, "library.json"), "NEW");
        Write(Path.Combine(_container, "library.json"), "ALREADY"); // a prior (crash-interrupted) relocate

        StartupMigration.RelocateData(_legacy, _container);

        Assert.Equal("ALREADY", File.ReadAllText(Path.Combine(_container, "library.json"))); // not clobbered
    }

    [Fact]
    public void SameVolume_TrueForPathsOnTheSameDrive()
    {
        Assert.True(StartupMigration.SameVolume(_legacy, _container));
    }
}
