using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jot.Text;
using Jot.Transcription.Granite;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// Fetches the English engine (Granite graph + punctuation model) from the live GitHub releases,
/// exactly as a user's first opt-in does.
///
/// OPT-IN (<c>JOT_ENGLISH_LIVE_DOWNLOAD=1</c>) because it moves ~712 MB and depends on a host being
/// up. It exists for the same reason as the vocabulary model's live test: this is the one class of
/// bug every offline test in this repo is blind to. The manifests here are internally consistent and
/// would stay green forever while pointing at a tag nobody cut, an asset nobody uploaded, or bytes
/// that were truncated on the way up — and the failure only ever shows up on a real user's first run.
///
/// Downloads to a temp directory, never the user's install.
/// </summary>
public class EnglishModelDownloadLiveTests(ITestOutputHelper output)
{
    private const string EnableVar = "JOT_ENGLISH_LIVE_DOWNLOAD";

    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnableVar) != "1")
                Skip = $"needs {EnableVar}=1 (downloads ~712 MB from the live releases)";
        }
    }

    /// <summary>
    /// Records every progress fraction, synchronously on whatever thread reported it.
    ///
    /// Deliberately NOT <see cref="Progress{T}"/>: that one POSTS each callback to the captured
    /// context, so the callbacks run on threadpool threads and some are still in flight after the
    /// download's await returns. Appending to a plain List from there raced the assertions below and
    /// threw "Collection was modified" mid-enumeration — the download was fine, the bookkeeping was
    /// not. Reporting inline also makes the last value the real last one, which
    /// <c>Assert.Equal(1.0, seen[^1])</c> depends on. The lock costs nothing at 45k reports and keeps
    /// this correct if the downloader ever fetches assets concurrently.
    /// </summary>
    private sealed class ProgressRecorder : IProgress<double>
    {
        private readonly List<double> _values = [];

        public void Report(double value)
        {
            lock (_values) _values.Add(value);
        }

        public List<double> Snapshot()
        {
            lock (_values) return [.. _values];
        }
    }

    [LiveFact]
    public async Task LiveReleases_ServeEveryAsset_AtTheExactSizeAndHash()
    {
        string root = Path.Combine(Path.GetTempPath(),
                                   "jot-english-live-" + Guid.NewGuid().ToString("N")[..8]);
        var granite = new GraniteModel(directory: Path.Combine(root, "granite"));
        var punct = new PunctCapSegModel(directory: Path.Combine(root, "punct"));
        var installer = new EnglishEngineInstaller(new GraniteModelInstaller(granite),
                                                   new PunctCapSegModelInstaller(punct));
        Assert.False(installer.IsInstalled);

        try
        {
            var recorder = new ProgressRecorder();
            await installer.EnsureInstalledAsync(recorder);
            var seen = recorder.Snapshot();

            // AssetDownloader verifies size and SHA-256 per asset and refuses to promote a file that
            // fails either, so reaching here at all means the published bytes match what the
            // installers pin. IsInstalled then confirms all four landed under the right names.
            Assert.True(installer.IsInstalled);
            Assert.True(granite.IsInstalled);
            Assert.True(punct.IsInstalled);

            output.WriteLine($"downloaded {EnglishEngineInstaller.TotalBytes / (1024.0 * 1024.0):0} MB " +
                             $"to {root}, {seen.Count} progress reports");

            // One continuous fraction across BOTH releases: the thing that must never happen is the
            // bar resetting toward zero when the second release starts, which is what a naive
            // "run one installer then the other" would do and what this composite exists to prevent.
            //
            // NOT asserted as strictly monotonic. The shared AssetDownloader re-counts bytes when a
            // chunk is retried, so it emits occasional backwards steps of ~1e-5 mid-file — measured,
            // pre-existing, and invisible in a progress bar. Tightening this would be testing the
            // downloader's retry accounting rather than this class, and would fail on a flaky
            // network for a reason no user would ever see.
            Assert.All(seen, f => Assert.InRange(f, 0.0, 1.0));
            for (int i = 1; i < seen.Count; i++)
                Assert.True(seen[i] >= seen[i - 1] - 0.01,
                            $"progress rewound visibly at report {i}: {seen[i - 1]} -> {seen[i]}");
            Assert.Equal(1.0, seen[^1], 3);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { /* temp dir */ }
        }
    }
}
