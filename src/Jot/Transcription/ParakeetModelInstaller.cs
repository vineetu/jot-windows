using System.IO;
using System.Net.Http;

namespace Jot.Transcription;

/// <summary>
/// Downloads the Parakeet-TDT (int8) model assets from Hugging Face on first run, so the app ships
/// small and fetches the ~630 MB model once. Files stream to <c>.part</c> temporaries and are moved
/// into place only on full success, so an interrupted download never leaves a half-written model that
/// <see cref="ParakeetModel.IsInstalled"/> would wrongly report as ready.
/// </summary>
public sealed class ParakeetModelInstaller
{
    // istupakov's ONNX export of nvidia/parakeet-tdt-0.6b-v2 (Apache-2.0 / CC-BY-4.0 weights).
    private const string BaseUrl =
        "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/";

    // File name + approximate size, used to weight overall progress.
    private static readonly (string Name, long ApproxBytes)[] Assets =
    [
        (ParakeetModel.PreprocessorFile, 132_000),
        (ParakeetModel.EncoderFile, 652_000_000),
        (ParakeetModel.DecoderJointFile, 9_000_000),
        (ParakeetModel.VocabFile, 12_000),
    ];

    private readonly ParakeetModel _model;

    public ParakeetModelInstaller(ParakeetModel model) => _model = model;

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
            await DownloadFileAsync(http, BaseUrl + name, finalPath, approxBytes,
                fileProgress => progress?.Report(
                    Math.Min(1.0, (fileStart + fileProgress) / (double)totalBytes)),
                ct).ConfigureAwait(false);
            completedBytes += approxBytes;
        }

        progress?.Report(1.0);
    }

    private static async Task DownloadFileAsync(
        HttpClient http, string url, string finalPath, long approxBytes,
        Action<long> onBytes, CancellationToken ct)
    {
        string tempPath = finalPath + ".part";

        // ConfigureAwait(false) throughout: this must not depend on (or hop back to) the caller's
        // SynchronizationContext — that both prevents a UI-thread deadlock if a caller ever blocks on
        // this task and keeps a large download off the UI thread.
        var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;

            var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            try
            {
                var dest = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                try
                {
                    var buffer = new byte[1 << 20];
                    long read = 0;
                    int n;
                    while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await dest.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        read += n;
                        // Report against the real content length when known, else the estimate.
                        long denom = contentLength ?? approxBytes;
                        onBytes(Math.Min(approxBytes, (long)(read / (double)Math.Max(1, denom) * approxBytes)));
                    }
                }
                finally { await dest.DisposeAsync().ConfigureAwait(false); }
            }
            finally { await source.DisposeAsync().ConfigureAwait(false); }
        }
        finally { response.Dispose(); }

        File.Move(tempPath, finalPath, overwrite: true);
    }
}
