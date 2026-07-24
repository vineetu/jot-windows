using Jot.Transcription;
using Xunit;

namespace Jot.Tests;

/// <summary>Full decision matrix for engine selection — this rule decides what every user runs.</summary>
public class EngineSelectorTests
{
    private const string Gpu = "GPU (DirectML)";

    // Explicit GPU: honored exactly as before Auto existed.
    [Theory]
    [InlineData(true, EngineChoice.Fp16Dml)]
    [InlineData(false, EngineChoice.Int4DmlEncoder)] // legacy parity: GPU pick without fp16 → int4 DML encoder
    public void ExplicitGpu_IsHonored(bool fp16Installed, EngineChoice expected)
    {
        Assert.Equal(expected, EngineSelector.Select(Gpu, fp16Installed, null, false));
        // Even a hostile verdict doesn't override an explicit pick.
        Assert.Equal(expected, EngineSelector.Select(Gpu, fp16Installed, "CPU", true));
    }

    // Auto: GPU tier only when EVERYTHING is proven.
    [Fact]
    public void Auto_AllProven_TakesGpuTier()
    {
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("Auto", true, "GPU", true));
    }

    [Theory]
    [InlineData(false, "GPU", true)]   // model missing
    [InlineData(true, null, true)]     // no verdict yet
    [InlineData(true, "CPU", true)]    // probe said no
    [InlineData(true, "GPU", false)]   // verdict from a different GPU/driver
    public void Auto_AnyMissingProof_FallsToInt4Cpu(bool installed, string? verdict, bool keyMatch)
    {
        Assert.Equal(EngineChoice.Int4Cpu, EngineSelector.Select("Auto", installed, verdict, keyMatch));
    }

    [Fact]
    public void ExplicitCpu_AlwaysCpu_EvenWhenGpuIsProven()
    {
        Assert.Equal(EngineChoice.Int4Cpu, EngineSelector.Select("CPU", true, "GPU", true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage-from-hand-edited-json")]
    public void UnknownDeviceValues_FallToInt4Cpu(string? device)
    {
        Assert.Equal(EngineChoice.Int4Cpu, EngineSelector.Select(device, true, "GPU", true));
    }

    [Fact]
    public void Auto_IsCaseInsensitive()
    {
        Assert.Equal(EngineChoice.Fp16Dml, EngineSelector.Select("auto", true, "GPU", true));
    }
}
