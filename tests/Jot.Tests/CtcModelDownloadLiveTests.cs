using System;
using System.IO;
using System.Threading.Tasks;
using Jot.Transcription.Ctc;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// The ONE test that actually talks to the network: fetch the vocabulary model from the live GitHub
/// release, exactly as a user's first opt-in does.
///
/// OPT-IN (<c>JOT_CTC_LIVE_DOWNLOAD=1</c>) because it moves ~132 MB and depends on a host being up —
/// neither belongs in a default run. It exists anyway because this is precisely the class of bug that
/// got the app rejected from the Store once already: a download that 404s or hash-fails is invisible to
/// every offline test in this repo, since the manifest can be internally consistent and still point at
/// a tag nobody cut, an asset nobody uploaded, or bytes that were re-compressed on the way up.
///
/// Downloads to a temp directory, never the user's install — the real model must not be at risk from
/// running a test.
/// </summary>
public class CtcModelDownloadLiveTests
{
    private const string EnableVar = "JOT_CTC_LIVE_DOWNLOAD";

    private readonly ITestOutputHelper _out;
    public CtcModelDownloadLiveTests(ITestOutputHelper output) => _out = output;

    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnableVar) != "1")
                Skip = $"needs {EnableVar}=1 (downloads ~132 MB from the live release)";
        }
    }

    [LiveFact]
    public async Task LiveRelease_ServesEveryAsset_AtTheExactSizeAndHash()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jot-ctc-live-" + Guid.NewGuid().ToString("N")[..8]);
        var model = new CtcModel(directory: dir);
        var installer = new CtcModelInstaller(model);
        Assert.False(installer.IsInstalled);

        try
        {
            double last = -1;
            var progress = new Progress<double>(f =>
            {
                if (f - last < 0.25 && f < 1.0) return;
                last = f;
                _out.WriteLine(installer.Manifest.DescribeProgress(f));
            });

            // AssetDownloader verifies size AND SHA-256 per file before promoting the .part, so a
            // successful return IS the integrity assertion — a 404 or a hash mismatch throws here.
            await installer.EnsureInstalledAsync(progress);

            Assert.True(installer.IsInstalled);
            Assert.True(model.IsInstalled);   // all three files, the spotter's own readiness test

            foreach (var asset in installer.Manifest.Assets)
            {
                var file = new FileInfo(Path.Combine(dir, asset.Name));
                Assert.True(file.Exists, $"{asset.Name} missing after a successful download");
                Assert.Equal(asset.Bytes, file.Length);
            }
            Assert.Empty(Directory.GetFiles(dir, "*.part"));   // nothing left half-written
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir; best effort */ }
        }
    }
}
