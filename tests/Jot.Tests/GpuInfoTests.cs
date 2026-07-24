using Jot.Platform;
using Xunit;

namespace Jot.Tests;

public class GpuInfoTests
{
    private static GpuInfo.Identity Id(
        string desc = "AMD Radeon RX 5700 XT", uint vendor = 0x1002, uint device = 0x731F,
        ulong vram = 8UL << 30, long driver = 0x001F_0000_3105_0002, bool software = false)
        => new(desc, vendor, device, vram, driver, software);

    [Fact]
    public void CacheKey_ChangesWhenDriverChanges()
    {
        Assert.NotEqual(Id(driver: 1).CacheKey, Id(driver: 2).CacheKey);
    }

    [Fact]
    public void CacheKey_ChangesWhenGpuChanges()
    {
        Assert.NotEqual(Id(device: 0x731F).CacheKey, Id(device: 0x744C).CacheKey);
    }

    [Fact]
    public void CacheKey_StableForSameHardwareAndDriver()
    {
        // Same GPU + driver across reboots must produce the same key (LUID would break this).
        Assert.Equal(Id().CacheKey, Id().CacheKey);
    }

    [Theory]
    [InlineData(2UL << 30, false, true)]   // exactly 2 GB, real adapter → capable
    [InlineData((2UL << 30) - 1, false, false)] // just under 2 GB → not capable
    [InlineData(8UL << 30, true, false)]   // WARP/software adapter → never capable
    public void LooksCapable_GatesOnVramAndSoftwareFlag(ulong vram, bool software, bool expected)
    {
        Assert.Equal(expected, Id(vram: vram, software: software).LooksCapable);
    }

    [Fact]
    public void FormatDriverVersion_SplitsTheFourWords()
    {
        long v = (31L << 48) | (0L << 32) | (12561L << 16) | 2005L;
        Assert.Equal("31.0.12561.2005", GpuInfo.FormatDriverVersion(v));
    }
}
