using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Downloads the Nemotron 3.5 ASR streaming multilingual (int4 ONNX) assets, so the app can ship without
/// the ~0.75 GB model and fetch it once. Each file streams to a <c>.part</c> temporary and is moved into
/// place only on full success, so an interrupted download never leaves a half-written model that
/// <see cref="NemotronModel.IsInstalled"/> would wrongly report as ready.
/// </summary>
public sealed class NemotronModelInstaller
{
    // Self-hosted on our own GitHub release (public CDN), mirroring FfmpegInstaller/jot-deps-v1 and
    // decoupled from app version tags. Moved OFF huggingface.co after a Store cert lab failed the
    // first-run download from HF: a lab network that blocks/throttles a non-Microsoft host, plus our old
    // single-shot fetch, surfaced as "Download failed" and rejected the app. GitHub's release CDN is
    // rarely blocked, and the fetch below now retries + resumes so a transient blip can't kill 0.75 GB.
    private const string BaseUrl =
        "https://github.com/vineetu/jot-windows/releases/download/jot-model-nemotron-int4-v1/";

    private const int MaxAttempts = 4; // per file — a blip mid-download shouldn't fail the whole install

    // A cert lab is more likely to THROTTLE than cleanly block: the connection stays open but bytes stop.
    // HttpClient.Timeout is disabled for the streamed body, so without this a stalled transfer would hang
    // the "Downloading…" UI forever. We cap the gap BETWEEN reads (not the whole download) and treat a
    // breach as retryable — 60s with zero bytes = stalled, not merely slow (a live link returns data far
    // more often than that even when heavily throttled).
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    // File name + approximate size (bytes), used to weight overall progress. The two big .data files
    // carry the external weights; the .onnx graphs are tiny by comparison.
    private static readonly (string Name, long ApproxBytes)[] Assets =
    [
        (NemotronModel.EncoderFile, 2_700_000),
        (NemotronModel.EncoderFile + ".data", 690_090_000),
        (NemotronModel.DecoderFile, 5_000),
        (NemotronModel.DecoderFile + ".data", 59_785_000),
        (NemotronModel.JointFile, 3_000),
        (NemotronModel.JointFile + ".data", 37_831_000),
        (NemotronModel.VocabFile, 64_000),
    ];

    /// <summary>Total download size in bytes (sum of all assets) — lets the UI show "X MB of Y MB".</summary>
    public static long TotalBytes { get; } = Assets.Sum(a => a.ApproxBytes);

    /// <summary>
    /// A non-technical status line for a [0,1] progress fraction, e.g. "Downloading… 340 MB of 754 MB (45%)".
    /// The MB counter keeps moving even when the rounded percent looks stuck, so a slow download reads as alive.
    /// </summary>
    public static string DescribeProgress(double fraction)
    {
        double totalMb = TotalBytes / (1024.0 * 1024.0);
        return $"Downloading… {fraction * totalMb:0} MB of {totalMb:0} MB ({fraction * 100:0}%)";
    }

    private readonly NemotronModel _model;

    public NemotronModelInstaller(NemotronModel model) => _model = model;

    public bool IsInstalled => _model.IsInstalled;

    /// <summary>
    /// Ensures every asset is present, downloading any that are missing. Reports overall progress in
    /// [0,1]. Safe to call when already installed (returns immediately).
    /// </summary>
    public async Task EnsureInstalledAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (_model.IsInstalled)
        {
            progress?.Report(1.0);
            return;
        }

        Directory.CreateDirectory(_model.Directory);

        long totalBytes = Assets.Sum(a => a.ApproxBytes);
        EnsureEnoughFreeSpace(_model.Directory, totalBytes); // fail fast with a clear message, not mid-write

        long completedBytes = 0;

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        foreach ((string name, long approxBytes) in Assets)
        {
            string finalPath = Path.Combine(_model.Directory, name);
            if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                completedBytes += approxBytes;
                progress?.Report(Math.Min(1.0, completedBytes / (double)totalBytes));
                continue;
            }

            long fileStart = completedBytes;
            await DownloadWithRetryAsync(http, BaseUrl + name, name, finalPath, approxBytes,
                fileProgress => progress?.Report(
                    Math.Min(1.0, (fileStart + fileProgress) / (double)totalBytes)),
                ct).ConfigureAwait(false);
            completedBytes += approxBytes;
        }

        progress?.Report(1.0);
    }

    /// <summary>
    /// Downloads one file, retrying transient network/IO failures with a short backoff. The partial
    /// <c>.part</c> is kept between attempts so the next try resumes via an HTTP Range request rather
    /// than refetching from zero.
    /// </summary>
    private static async Task DownloadWithRetryAsync(
        HttpClient http, string url, string name, string finalPath, long approxBytes,
        Action<long> onBytes, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileAsync(http, url, name, finalPath, approxBytes, onBytes, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    // Network drops, disk-write hiccups and stalls (surfaced as IOException) are worth another try; a
    // permanent HTTP error (a 4xx like 404) is not — it's thrown as PermanentDownloadException so we fail fast.
    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException;

    private sealed class PermanentDownloadException(string message) : Exception(message);

    private static async Task DownloadFileAsync(
        HttpClient http, string url, string name, string finalPath, long approxBytes,
        Action<long> onBytes, CancellationToken ct)
    {
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
            // 416: the server says our .part already holds the whole file — accept it as complete.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                File.Move(tempPath, finalPath, overwrite: true);
                onBytes(approxBytes);
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
            long fileTotal = (bodyLength ?? approxBytes) + existing;

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
                        onBytes(Math.Min(approxBytes, (long)(written / (double)Math.Max(1, fileTotal) * approxBytes)));
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

        File.Move(tempPath, finalPath, overwrite: true);
        onBytes(approxBytes);
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
