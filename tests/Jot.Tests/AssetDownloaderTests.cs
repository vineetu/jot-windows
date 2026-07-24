using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jot.Services.Download;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// Exercises the REAL download path (HTTP, .part promotion, checksum gate, self-healing skip) against a
/// throwaway localhost server — no network, no mocks of the code under test.
/// </summary>
public sealed class AssetDownloaderTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly ConcurrentDictionary<string, int> _requests = new();
    private readonly CancellationTokenSource _serverCts = new();

    public AssetDownloaderTests()
    {
        // Find a free port by letting the OS assign one, then bind HttpListener to it (localhost
        // prefixes need no URL ACL). The tiny race between release and re-bind is fine for a test.
        int port;
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _baseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_baseUrl);
        _listener.Start();
        _ = Task.Run(ServeAsync);

        _dir = Path.Combine(Path.GetTempPath(), "jot-dl-test-" + Guid.NewGuid().ToString("N"));
    }

    private async Task ServeAsync()
    {
        while (!_serverCts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener closed

            string name = ctx.Request.Url!.AbsolutePath.TrimStart('/');
            _requests.AddOrUpdate(name, 1, (_, n) => n + 1);
            if (_files.TryGetValue(name, out byte[]? bytes))
            {
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _serverCts.Cancel();
        try { _listener.Stop(); } catch { }
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private Task Ensure(AssetManifest manifest) =>
        AssetDownloader.EnsureAsync(manifest, _dir, retryDelay: TimeSpan.Zero);

    [Fact]
    public async Task Download_WithCorrectChecksum_InstallsFile()
    {
        byte[] content = Encoding.UTF8.GetBytes("hello model weights");
        _files["a.bin"] = content;
        var manifest = new AssetManifest(_baseUrl, [new DownloadAsset("a.bin", content.Length, Sha256Of(content))]);

        await Ensure(manifest);

        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_dir, "a.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "a.bin.part")), "no .part left behind");
    }

    [Fact]
    public async Task Download_WithWrongChecksum_FailsAndLeavesNothing()
    {
        byte[] content = Encoding.UTF8.GetBytes("corrupted-in-transit");
        _files["b.bin"] = content;
        var manifest = new AssetManifest(_baseUrl,
            [new DownloadAsset("b.bin", content.Length, new string('0', 64))]);

        var ex = await Assert.ThrowsAsync<IOException>(() => Ensure(manifest));

        Assert.Contains("checksum mismatch", ex.Message);
        // A corrupted file must be promoted to NEITHER the final name NOR a resumable .part (a resume
        // would re-verify the same bad bytes forever).
        Assert.False(File.Exists(Path.Combine(_dir, "b.bin")));
        Assert.False(File.Exists(Path.Combine(_dir, "b.bin.part")));
        // And it must have burned all retry attempts refetching from zero, not given up on the first.
        Assert.Equal(4, _requests["b.bin"]);
    }

    [Fact]
    public async Task Download_WithoutChecksum_IsAcceptedBySizeGuardOnly()
    {
        byte[] content = Encoding.UTF8.GetBytes("legacy asset, no hash");
        _files["c.bin"] = content;
        var manifest = new AssetManifest(_baseUrl, [new DownloadAsset("c.bin", content.Length)]);

        await Ensure(manifest);

        Assert.True(File.Exists(Path.Combine(_dir, "c.bin")));
    }

    [Fact]
    public async Task Ensure_SkipsPresentFiles_AndFetchesOnlyMissingOnes()
    {
        // Self-healing: an "installed" model missing one newly-added manifest file fetches ONLY that file.
        byte[] present = Encoding.UTF8.GetBytes("already here");
        byte[] missing = Encoding.UTF8.GetBytes("added in a later release");
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "old.bin"), present);
        _files["old.bin"] = present;
        _files["new.bin"] = missing;
        var manifest = new AssetManifest(_baseUrl,
        [
            new DownloadAsset("old.bin", present.Length, Sha256Of(present)),
            new DownloadAsset("new.bin", missing.Length, Sha256Of(missing)),
        ]);

        await Ensure(manifest);

        Assert.False(_requests.ContainsKey("old.bin"), "present file must not be re-downloaded");
        Assert.Equal(missing, await File.ReadAllBytesAsync(Path.Combine(_dir, "new.bin")));
    }

    [Fact]
    public async Task Missing404Asset_FailsFast_WithoutRetryStorm()
    {
        var manifest = new AssetManifest(_baseUrl, [new DownloadAsset("gone.bin", 10, null)]);

        await Assert.ThrowsAnyAsync<Exception>(() => Ensure(manifest));

        Assert.Equal(1, _requests["gone.bin"]); // 4xx is permanent — one attempt, no retries
    }
}
