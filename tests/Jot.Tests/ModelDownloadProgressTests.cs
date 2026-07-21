using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

public class ModelDownloadProgressTests
{
    [Fact]
    public void TotalBytes_IsAboutThreeQuartersOfAGig()
    {
        double mb = NemotronModelInstaller.TotalBytes / (1024.0 * 1024.0);
        Assert.InRange(mb, 700, 800); // the int4 model is ~754 MB — guards against an Assets typo
    }

    [Theory]
    [InlineData(0.0, "Downloading… 0 MB of 754 MB (0%)")]
    [InlineData(0.5, "Downloading… 377 MB of 754 MB (50%)")]
    [InlineData(1.0, "Downloading… 754 MB of 754 MB (100%)")]
    public void DescribeProgress_ShowsMbOfMbAndPercent(double fraction, string expected)
    {
        Assert.Equal(expected, NemotronModelInstaller.DescribeProgress(fraction));
    }

    [Fact]
    public void DescribeProgress_DownloadedNeverExceedsTotal()
    {
        // The "X" must never read higher than "Y" at any point in [0,1].
        double total = NemotronModelInstaller.TotalBytes / (1024.0 * 1024.0);
        for (double p = 0; p <= 1.0; p += 0.05)
            Assert.True(p * total <= total + 0.001, $"downloaded exceeded total at p={p}");
    }
}
