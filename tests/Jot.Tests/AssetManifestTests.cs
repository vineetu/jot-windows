using System;
using System.Linq;
using Jot.Services;
using Jot.Services.Download;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

/// <summary>Guards both model manifests against typos: names must match what the model locators expect,
/// sizes must be sane, and every entry must carry a well-formed SHA-256 (integrity is only as good as
/// the baked hashes).</summary>
public class AssetManifestTests
{
    private static AssetManifest Int4() =>
        new NemotronModelInstaller(new NemotronModel(directory: @"C:\x")).Manifest;

    private static AssetManifest Fp16() =>
        new NemotronFp16ModelInstaller(new NemotronFp16Model(directory: @"C:\x")).Manifest;

    [Fact]
    public void Fp16Manifest_CoversEveryFileTheLocatorRequires()
    {
        var names = Fp16().Assets.Select(a => a.Name).ToHashSet();
        // Everything IsInstalled checks for must be in the manifest, or a "complete" download
        // would still report not-installed.
        Assert.Contains(NemotronFp16Model.EncoderFile, names);
        Assert.Contains(NemotronFp16Model.EncoderDataFile, names); // the nosplit-named external weights
        Assert.Contains(NemotronFp16Model.DecoderFile, names);
        Assert.Contains(NemotronFp16Model.DecoderFile + ".data", names);
        Assert.Contains(NemotronFp16Model.JointFile, names);
        Assert.Contains(NemotronFp16Model.JointFile + ".data", names);
        Assert.Contains(NemotronFp16Model.VocabFile, names);
        Assert.Contains(NemotronFp16Model.LanguagesFile, names);
        Assert.Equal(8, names.Count);
    }

    [Fact]
    public void Int4Manifest_CoversEveryFileTheLocatorRequires()
    {
        var names = Int4().Assets.Select(a => a.Name).ToHashSet();
        Assert.Contains(NemotronModel.EncoderFile, names);
        Assert.Contains(NemotronModel.EncoderFile + ".data", names);
        Assert.Contains(NemotronModel.DecoderFile, names);
        Assert.Contains(NemotronModel.DecoderFile + ".data", names);
        Assert.Contains(NemotronModel.JointFile, names);
        Assert.Contains(NemotronModel.JointFile + ".data", names);
        Assert.Contains(NemotronModel.VocabFile, names);
        Assert.Equal(7, names.Count);
    }

    [Theory]
    [InlineData("int4")]
    [InlineData("fp16")]
    public void EveryAsset_HasWellFormedSha256AndPositiveSize(string which)
    {
        var manifest = which == "int4" ? Int4() : Fp16();
        Assert.All(manifest.Assets, a =>
        {
            Assert.True(a.Bytes > 0, $"{a.Name}: size must be exact, not zero");
            Assert.NotNull(a.Sha256);
            Assert.Equal(64, a.Sha256!.Length);
            Assert.True(a.Sha256.All(Uri.IsHexDigit), $"{a.Name}: hash must be hex");
        });
    }

    [Fact]
    public void Fp16TotalBytes_IsAboutOnePointTwoGig()
    {
        double gb = Fp16().TotalBytes / (1024.0 * 1024.0 * 1024.0);
        Assert.InRange(gb, 1.1, 1.4);
    }

    [Fact]
    public void GpuStatusText_MentionsTheRealSize()
    {
        // The "~1.3 GB" consent-relevant label must stay honest if the manifest ever changes.
        Assert.Contains("1.3 GB", GpuModelDownload.GpuNotInstalledText);
    }

    [Fact]
    public void BaseUrls_AreDistinctReleases()
    {
        Assert.NotEqual(Int4().BaseUrl, Fp16().BaseUrl);
        Assert.StartsWith("https://github.com/vineetu/jot-windows/releases/download/", Int4().BaseUrl);
        Assert.StartsWith("https://github.com/vineetu/jot-windows/releases/download/", Fp16().BaseUrl);
        Assert.EndsWith("/", Fp16().BaseUrl); // BaseUrl + Name concatenation depends on the trailing slash
        Assert.EndsWith("/", Int4().BaseUrl);
    }
}
