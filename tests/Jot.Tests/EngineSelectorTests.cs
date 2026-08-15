using Jot.Transcription;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Device string → ggml backend. Auto and any GPU pick try Vulkan; explicit CPU stays on CPU.
/// Leftover "GPU (DirectML)" still contains "GPU" so it routes the same as Vulkan.
/// </summary>
public class EngineSelectorTests
{
    [Fact]
    public void Auto_TriesVulkan()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.Auto));
    }

    [Fact]
    public void ExplicitCpu_StaysCpu()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Cpu,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.Cpu));
    }

    [Fact]
    public void GpuVulkan_TriesVulkan()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.GpuVulkan));
    }

    [Fact]
    public void LeftoverDirectMlLabel_StillTriesVulkan()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, TranscriptionDevices.Gpu));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage-from-hand-edited-json")]
    public void UnknownDevice_FallsToCpu(string? device)
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Cpu,
            GgmlEngineOptions.ResolveBackend(_ => null, device));
    }

    [Fact]
    public void Auto_IsCaseInsensitive()
    {
        Assert.Equal(
            NativeMethods.BackendRequest.Vulkan,
            GgmlEngineOptions.ResolveBackend(_ => null, "auto"));
    }
}
