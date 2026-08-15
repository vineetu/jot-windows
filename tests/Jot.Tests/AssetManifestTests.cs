using System;
using System.Linq;
using Jot.Transcription.Ggml;
using Xunit;

namespace Jot.Tests;

/// <summary>Guards the GGUF manifest against typos: name must match the locator, size must be sane,
/// and the entry must carry a well-formed SHA-256.</summary>
public class AssetManifestTests
{
    private static Jot.Services.Download.AssetManifest Gguf() =>
        new NemotronGgufModelInstaller(new NemotronGgufModel(directory: @"C:\x", env: _ => null)).Manifest;

    [Fact]
    public void GgufManifest_IsTheOneQ8_0File()
    {
        var m = Gguf();
        Assert.Single(m.Assets);
        Assert.Equal(NemotronGgufModel.FileName, m.Assets[0].Name);
        Assert.Equal(751_094_240, m.Assets[0].Bytes);
        Assert.Equal(64, m.Assets[0].Sha256!.Length);
        double mb = m.TotalBytes / (1024.0 * 1024.0);
        Assert.InRange(mb, 700, 730);
        Assert.StartsWith("https://github.com/vineetu/jot-windows/releases/download/", m.BaseUrl);
        Assert.Contains(NemotronGgufModelInstaller.ReleaseTag, m.BaseUrl);
    }

    [Fact]
    public void EveryAsset_HasWellFormedSha256AndPositiveSize()
    {
        Assert.All(Gguf().Assets, a =>
        {
            Assert.True(a.Bytes > 0, $"{a.Name}: size must be exact, not zero");
            Assert.NotNull(a.Sha256);
            Assert.Equal(64, a.Sha256!.Length);
            Assert.True(a.Sha256.All(Uri.IsHexDigit), $"{a.Name}: hash must be hex");
        });
    }
}
