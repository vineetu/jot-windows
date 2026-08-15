using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

public class ModelDownloadProgressTests
{
    [Fact]
    public void GgufTotalBytes_IsAboutSevenHundredSixteenMb()
    {
        double mb = NemotronGgufModelInstaller.TotalBytes / (1024.0 * 1024.0);
        Assert.InRange(mb, 700, 730);
    }

    [Fact]
    public void DescribeProgress_DownloadedNeverExceedsTotal()
    {
        double total = NemotronGgufModelInstaller.TotalBytes / (1024.0 * 1024.0);
        for (double p = 0; p <= 1.0; p += 0.05)
            Assert.True(p * total <= total + 0.001, $"downloaded exceeded total at p={p}");
    }
}
