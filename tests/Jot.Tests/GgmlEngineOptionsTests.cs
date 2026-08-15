using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

public class GgmlEngineOptionsTests
{
    [Fact]
    public void DefaultSettings_AreOff_WithoutAssets()
    {
        Assert.False(GgmlEngineOptions.IsEnabled(new JotSettings(), _ => null));
        Assert.False(new JotSettings().UseGgmlEngine);
        Assert.Equal(3, new JotSettings().GgmlAttContextRight);
    }

    [Fact]
    public void AssetsPresent_EnablesByDefault()
    {
        Assert.True(GgmlEngineOptions.IsEnabled(new JotSettings(), _ => null, assetsPresent: true));
    }

    [Fact]
    public void OrtEnv_WinsOverAssets()
    {
        Assert.False(GgmlEngineOptions.IsEnabled(
            new JotSettings(),
            k => k == "JOT_ENGINE" ? "ort" : null,
            assetsPresent: true));
    }

    [Fact]
    public void HiddenSetting_Enables()
    {
        Assert.True(GgmlEngineOptions.IsEnabled(new JotSettings { UseGgmlEngine = true }, _ => null));
    }

    [Fact]
    public void EnvGgml_EnablesEvenWhenSettingIsOff()
    {
        Assert.True(GgmlEngineOptions.IsEnabled(new JotSettings(), k => k == "JOT_ENGINE" ? "ggml" : null));
    }

    [Fact]
    public void EnvOrt_ForcesOffEvenWhenSettingIsOn()
    {
        Assert.False(GgmlEngineOptions.IsEnabled(
            new JotSettings { UseGgmlEngine = true },
            k => k == "JOT_ENGINE" ? "ort" : null));
    }

    [Theory]
    [InlineData(null, 3, 3)]
    [InlineData("6", 3, 6)]
    [InlineData("13", 3, 13)]
    [InlineData("0", 3, 0)]
    [InlineData("99", 6, 6)]   // illegal env → fall through to setting
    [InlineData("nope", 6, 6)]
    [InlineData(null, 99, 3)]  // illegal setting → live default
    public void Lookahead_EnvBeatsSetting_IllegalFallsBack(string? envR, int setting, int expected)
    {
        int got = GgmlEngineOptions.ResolveLookahead(setting, k => k == "JOT_GGML_R" ? envR : null);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Backend_FollowsDevice_NotJustOnnxChoice()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(EngineChoice.Fp16Dml, _ => null));
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(EngineChoice.Int4DmlEncoder, _ => null));
        Assert.Equal(
            NativeMethods.BackendRequest.Cpu,
            GgmlEngineOptions.ResolveBackend(EngineChoice.Int4Cpu, _ => null, TranscriptionDevices.Cpu));
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(EngineChoice.Int4Cpu, _ => null, TranscriptionDevices.Auto));
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(EngineChoice.Int4Cpu, _ => null, TranscriptionDevices.GpuVulkan));
    }

    [Theory]
    [InlineData("cpu", "Cpu")]
    [InlineData("vulkan", "Vulkan")]
    [InlineData("auto", "Auto")]
    public void Backend_EnvOverridesChoice(string env, string expected)
    {
        Assert.Equal(expected, GgmlEngineOptions.ResolveBackend(
            EngineChoice.Fp16Dml, k => k == "JOT_GGML_BACKEND" ? env : null).ToString());
    }
}
