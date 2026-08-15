using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

public class GgmlEngineOptionsTests
{
    [Fact]
    public void DefaultLookahead_IsThree()
    {
        Assert.Equal(3, new JotSettings().GgmlAttContextRight);
        Assert.Equal(3, GgmlEngineOptions.DefaultLookahead);
    }

    [Fact]
    public void OrtEnv_IsDetectedButDoesNotChangeBackend()
    {
        Assert.True(GgmlEngineOptions.IsOrtRequested(k => k == "JOT_ENGINE" ? "ort" : null));
        Assert.False(GgmlEngineOptions.IsOrtRequested(_ => null));
        // Detection is for the factory log line only — backend still follows the device.
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(
                k => k == "JOT_ENGINE" ? "ort" : null, TranscriptionDevices.Auto));
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
    public void Backend_FollowsDevice()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Cpu,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.Cpu));
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.Auto));
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.GpuVulkan));
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.Gpu));
    }

    [Theory]
    [InlineData("cpu", "Cpu")]
    [InlineData("vulkan", "Vulkan")]
    [InlineData("auto", "Auto")]
    public void Backend_EnvOverridesDevice(string env, string expected)
    {
        Assert.Equal(expected, GgmlEngineOptions.ResolveBackend(
            k => k == "JOT_GGML_BACKEND" ? env : null, TranscriptionDevices.Cpu).ToString());
    }
}
