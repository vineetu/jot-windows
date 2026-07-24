using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Jot.Services.Download;

/// <summary>
/// The one download engine for large on-device assets, driven by an <see cref="AssetManifest"/>. Each
/// file streams to a <c>.part</c> temporary and is moved into place only on full success (size AND
/// checksum), so an interrupted or corrupted download never leaves a half-written model that a
/// File.Exists-based IsInstalled would wrongly report as ready. Always iterates the whole manifest with
/// a per-file skip — self-healing when a later release adds a file to an already-installed model.
/// </summary>
public static class AssetDownloader
{
    private const int MaxAttempts = 4; // per file — a blip mid-download shouldn't fail the whole install

    // A cert lab is more likely to THROTTLE than cleanly block: the connection stays open but bytes stop.
    // HttpClient.Timeout is disabled for the streamed body, so without this a stalled transfer would hang
    // the "Downloading…" UI forever. We cap the gap BETWEEN reads (not the whole download) and treat a
    // breach as retryable — 60s with zero bytes = stalled, not merely slow (a live link returns data far
    // more often than that even when heavily throttled).
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Ensures every manifest asset is present in <paramref name="targetDir"/>, downloading any that are
    /// missing. Reports overall progress in [0,1]. Safe to call when already installed (per-file skips).
    /// </summary>
    public static async Task EnsureAsync(AssetManifest manifest, string targetDir,
        IProgress<double>? progress = null, CancellationToken ct = default, TimeSpan? retryDelay = null)
    {
        Directory.CreateDirectory(targetDir);

        long totalBytes = manifest.TotalBytes;

        // Fail fast with a clear message, not mid-write — but only demand space for what's actually missing.
        long missingBytes = manifest.Assets
            .Where(a => !IsAssetPresent(Path.Combine(targetDir, a.Name)))
            .Sum(a => a.Bytes);
        if (missingBytes > 0) EnsureEnoughFreeSpace(targetDir, missingBytes);

        long completedBytes = 0;

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        foreach (var asset in manifest.Assets)
        {
            string finalPath = Path.Combine(targetDir, asset.Name);
            if (IsAssetPresent(finalPath))
            {
                completedBytes += asset.Bytes;
                progress?.Report(Math.Min(1.0, completedBytes / (double)totalBytes));
                continue;
            }

            long fileStart = completedBytes;
            await DownloadWithRetryAsync(http, manifest.BaseUrl + asset.Name, asset, finalPath,
                fileProgress => progress?.Report(
                    Math.Min(1.0, (fileStart + fileProgress) / (double)totalBytes)),
                ct, retryDelay).ConfigureAwait(false);
            completedBytes += asset.Bytes;
        }

        progress?.Report(1.0);
    }

    private static bool IsAssetPresent(string finalPath) =>
        File.Exists(finalPath) && new FileInfo(finalPath).Length > 0;

    /// <summary>
    /// Downloads one file, retrying transient network/IO failures with a short backoff. The partial
    /// <c>.part</c> is kept between attempts so the next try resumes via an HTTP Range request rather
    /// than refetching from zero (a checksum failure is the exception — it deletes the .part first).
    /// </summary>
    private static async Task DownloadWithRetryAsync(
        HttpClient http, string url, DownloadAsset asset, string finalPath,
        Action<long> onBytes, CancellationToken ct, TimeSpan? retryDelay)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileAsync(http, url, asset, finalPath, onBytes, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                // retryDelay is a test hook — production always backs off (2s, 4s, 6s).
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    // Network drops, disk-write hiccups and stalls (surfaced as IOException) are worth another try; a
    // permanent HTTP error (a 4xx like 404) is not — it's thrown as PermanentDownloadException so we fail fast.
    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException;

    private sealed class PermanentDownloadException(string message) : Exception(message);

    private static async Task DownloadFileAsync(
        HttpClient http, string url, DownloadAsset asset, string finalPath,
        Action<long> onBytes, CancellationToken ct)
    {
        string name = asset.Name;
        string tempPath = finalPath + ".part";

        // Resume: if a partial .part survived a previous attempt, ask only for the remaining bytes.
        long existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        // Bound the connect/headers wait too, so a dead connection that never responds can't hang forever.
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(StallTimeout);
        HttpResponseMessage response;
        try
        {
            // ConfigureAwait(false) throughout: keep the large download off the caller's SynchronizationContext.
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"{name}: no response from {new Uri(url).Host} within {StallTimeout.TotalSeconds:0}s");
        }

        using (response)
        {
            // 416: the server says our .part already holds the whole file — verify and accept it as complete.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                VerifyChecksum(tempPath, asset);
                File.Move(tempPath, finalPath, overwrite: true);
                onBytes(asset.Bytes);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Surface the REAL cause (host + status). Retry only server-side/transient statuses; a 4xx
                // (bad URL, gone) won't fix itself, so fail immediately instead of burning MaxAttempts.
                string msg = $"{name}: {(int)response.StatusCode} {response.ReasonPhrase} from {new Uri(url).Host}";
                bool retryable = (int)response.StatusCode >= 500
                    || response.StatusCode == HttpStatusCode.RequestTimeout   // 408
                    || response.StatusCode == HttpStatusCode.TooManyRequests; // 429
                throw retryable ? new HttpRequestException(msg) : new PermanentDownloadException(msg);
            }

            // We asked to resume but the server ignored the Range (200, not 206): restart the file cleanly.
            bool append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) existing = 0;

            long? bodyLength = response.Content.Headers.ContentLength;
            long fileTotal = (bodyLength ?? asset.Bytes) + existing;

            var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                var dest = new FileStream(tempPath, append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                try
                {
                    var buffer = new byte[1 << 20];
                    long written = existing;
                    while (true)
                    {
                        // Reset the idle clock each read: a stalled transfer (no bytes for StallTimeout) is
                        // cancelled and thrown as a retryable IOException so DownloadWithRetryAsync resumes it.
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(StallTimeout);
                        int n;
                        try { n = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException(
                                $"{name}: download stalled (>{StallTimeout.TotalSeconds:0}s with no data)");
                        }
                        if (n == 0) break;

                        await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        written += n;
                        onBytes(Math.Min(asset.Bytes, (long)(written / (double)Math.Max(1, fileTotal) * asset.Bytes)));
                    }
                }
                finally { await dest.DisposeAsync().ConfigureAwait(false); }
            }
            finally { await source.DisposeAsync().ConfigureAwait(false); }

            // Completeness guard: a silently-truncated response must NOT be promoted to the final path, or
            // IsInstalled (File.Exists only) would report a broken model as ready. When the server gave a
            // Content-Length, the finished .part must match it exactly; a short file throws (retry resumes it).
            if (bodyLength is not null && new FileInfo(tempPath).Length != fileTotal)
            {
                throw new IOException(
                    $"{name}: incomplete download ({new FileInfo(tempPath).Length} of {fileTotal} bytes)");
            }
        }

        VerifyChecksum(tempPath, asset);
        File.Move(tempPath, finalPath, overwrite: true);
        onBytes(asset.Bytes);
    }

    // Integrity gate before the atomic move. On mismatch the .part is DELETED before throwing — a resume
    // would re-verify the same bad bytes forever — so the retry refetches the file from zero instead.
    private static void VerifyChecksum(string tempPath, DownloadAsset asset)
    {
        if (asset.Sha256 is null) return;
        string actual;
        using (var stream = File.OpenRead(tempPath))
            actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempPath);
            throw new IOException(
                $"{asset.Name}: checksum mismatch (expected {asset.Sha256[..8]}…, got {actual[..8]}…)");
        }
    }

    // The .data weights dwarf everything, so a wrong drive choice is the likely disk-full culprit; check
    // the actual target drive up front and tell the user how much is short (plus headroom for the .part).
    private static void EnsureEnoughFreeSpace(string dir, long needBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (root is null) return;

            var drive = new DriveInfo(root);
            long required = needBytes + (1L << 30); // model + ~1 GB headroom (a .part briefly doubles a file)
            if (drive.IsReady && drive.AvailableFreeSpace < required)
            {
                const double gb = 1L << 30;
                throw new IOException(
                    $"Not enough free space on {root.TrimEnd('\\')} — need ~{required / gb:0.#} GB, " +
                    $"{drive.AvailableFreeSpace / gb:0.#} GB free. Free up space or change the data folder in Settings.");
            }
        }
        catch (IOException) { throw; }        // our own "not enough space" message — let it through
        catch { /* couldn't probe the drive — don't block the download over a probe failure */ }
    }
}
