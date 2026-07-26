using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jot.Services;
using Jot.Services.Download;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The vocabulary model's download recipe and the state machine the Settings row binds to.
///
/// This is the path that had NO caller at all until 1.2.2 — a user could switch vocabulary on and reach
/// a permanent dead end — so the manifest (which decides whether the URLs resolve and the hashes match)
/// and the consent rule (which decides whether 132 MB is ever fetched behind someone's back) are both
/// pinned here.
/// </summary>
public class CtcModelDownloadTests
{
    private static AssetManifest Manifest() =>
        new CtcModelInstaller(new CtcModel(directory: @"C:\x")).Manifest;

    [Fact]
    public void Manifest_CoversEveryFileTheLocatorRequires()
    {
        var names = Manifest().Assets.Select(a => a.Name).ToHashSet();
        // CtcModel.IsInstalled demands all three; a manifest missing one would download "successfully"
        // and still read as not-installed forever.
        Assert.Contains(CtcModel.ModelFile, names);
        Assert.Contains(CtcModel.TokensFile, names);
        Assert.Contains(CtcModel.TokenizerFile, names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void EveryAsset_HasWellFormedSha256AndPositiveSize()
    {
        Assert.All(Manifest().Assets, a =>
        {
            Assert.True(a.Bytes > 0, $"{a.Name}: size must be exact, not zero");
            Assert.NotNull(a.Sha256);
            Assert.Equal(64, a.Sha256!.Length);
            Assert.True(a.Sha256.All(Uri.IsHexDigit), $"{a.Name}: hash must be hex");
        });
    }

    [Fact]
    public void BaseUrl_PointsAtTheReleaseTagAndEndsInASlash()
    {
        string url = Manifest().BaseUrl;
        Assert.StartsWith("https://github.com/vineetu/jot-windows/releases/download/", url);
        Assert.Contains(CtcModelInstaller.ReleaseTag, url);
        Assert.EndsWith("/", url);   // BaseUrl + Name concatenation depends on it
    }

    [Fact]
    public void SizeQuotedToTheUser_MatchesTheManifest()
    {
        // The consent prompt, the idle row label and DescribeProgress must all quote ONE number.
        int fromManifest = (int)Math.Round(Manifest().TotalBytes / (1024.0 * 1024.0));
        Assert.Equal(fromManifest, CtcModelDownload.SizeMb);
        Assert.Contains($"{CtcModelDownload.SizeMb} MB", CtcModelDownload.VocabularyNotInstalledText);
        Assert.Contains($"{CtcModelDownload.SizeMb} MB", CtcModelDownload.ConsentMessage);
        Assert.Contains($"{CtcModelDownload.SizeMb} MB", Manifest().DescribeProgress(1.0));
    }

    [Fact]
    public void ConsentMessage_SaysDecliningKeepsTheTerms()
    {
        // Declining leaves the toggle on; the copy has to say the terms survive, or "No" reads as
        // "throw away what I just set up".
        Assert.Contains("saved", CtcModelDownload.ConsentMessage);
    }

    [Theory]
    // enabled, installed, downloading, languageOk -> offer?
    [InlineData(true, false, false, true, true)]    // the one case that asks
    [InlineData(false, false, false, true, false)]  // switching OFF must never start a download
    [InlineData(true, true, false, true, false)]    // already on disk
    [InlineData(true, false, true, true, false)]    // already running — never two prompts
    [InlineData(true, false, false, false, false)]  // non-English: don't ask for 132 MB we'd then ignore
    public void ShouldOffer_AsksOnlyOnTheOptIn(
        bool enabled, bool installed, bool downloading, bool languageOk, bool expected)
        => Assert.Equal(expected, CtcModelDownload.ShouldOffer(enabled, installed, downloading, languageOk));
}

/// <summary>
/// <see cref="ModelDownload"/>'s observable states, exercised through a fake installer so no bytes move.
/// These are what the Settings rows render: not-installed → downloading(progress) → ready, or → failed
/// with a Retry that re-runs the same idempotent Ensure.
/// </summary>
public class ModelDownloadStateTests
{
    /// <summary>A ModelDownload over a scriptable installer. ModelDownload's ctor is protected, which is
    /// the point (only real models get one) — a test subclass is the sanctioned way in.</summary>
    private sealed class TestDownload : ModelDownload
    {
        public TestDownload(FakeInstaller installer) : base(installer, "Installed", "Not installed (~9 MB)") { }
    }

    private sealed class FakeInstaller : IModelInstaller
    {
        public bool ThrowWith { get; set; }
        public bool Installed { get; set; }
        public bool IsInstalled => Installed;
        public AssetManifest Manifest { get; } =
            new("https://example.invalid/", [new("a.bin", 9_437_184, null)]);

        public Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
        {
            // Throws BEFORE reporting, like the real thing: AssetDownloader reports bytes it actually
            // wrote, so a 404 on the first asset produces no progress at all.
            if (ThrowWith) throw new InvalidOperationException("a.bin: 404 Not Found from example.invalid");
            progress?.Report(0.5);
            Installed = true;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FreshInstall_GoesNotInstalled_ToReady()
    {
        var download = new TestDownload(new FakeInstaller());
        Assert.False(download.IsInstalled);
        Assert.True(download.ShowButton);
        Assert.Equal("Download", download.ButtonText);

        Assert.True(await download.EnsureAsync());
        Assert.True(download.IsInstalled);
        Assert.False(download.ShowButton);      // ready: no button at all
        Assert.False(download.Failed);
        Assert.Equal("Installed", download.StatusText);
    }

    [Fact]
    public async Task Failure_KeepsTheReason_AndOffersRetry()
    {
        var installer = new FakeInstaller { ThrowWith = true };
        var download = new TestDownload(installer);

        Assert.False(await download.EnsureAsync());
        Assert.True(download.Failed);
        Assert.False(download.IsDownloading);
        Assert.StartsWith("Download failed — ", download.StatusText);
        Assert.Contains("404", download.StatusText);   // the REAL cause reaches the user
        Assert.True(download.ShowButton);
        Assert.Equal("Retry", download.ButtonText);
    }

    [Fact]
    public async Task Refresh_DoesNotSilentlyEraseAFailure()
    {
        var installer = new FakeInstaller { ThrowWith = true };
        var download = new TestDownload(installer);
        await download.EnsureAsync();

        download.Refresh();  // e.g. re-opening Settings
        Assert.True(download.Failed);
        Assert.StartsWith("Download failed — ", download.StatusText);
    }

    [Fact]
    public async Task Retry_AfterAFailure_Succeeds_AndClearsTheFailedState()
    {
        var installer = new FakeInstaller { ThrowWith = true };
        var download = new TestDownload(installer);
        await download.EnsureAsync();

        installer.ThrowWith = false;
        Assert.True(await download.EnsureAsync());
        Assert.False(download.Failed);
        Assert.Equal("Download", download.ButtonText);
        Assert.Equal("Installed", download.StatusText);
    }

    [Fact]
    public void Refresh_PicksUpAModelThatAppearedOrVanishedElsewhere()
    {
        var installer = new FakeInstaller();
        var download = new TestDownload(installer);
        Assert.False(download.IsInstalled);

        installer.Installed = true;              // e.g. a data-folder move brought it into view
        download.Refresh();
        Assert.True(download.IsInstalled);

        installer.Installed = false;             // ...or moved it away
        download.Refresh();
        Assert.False(download.IsInstalled);
        Assert.Equal("Not installed (~9 MB)", download.StatusText);
    }
}
