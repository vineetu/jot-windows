using System.IO;
using System.Net.Http;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// Downloads the Nemotron 3.5 ASR streaming multilingual (int4 ONNX) assets from Hugging Face, so the
/// app can ship without the ~0.67 GB model and fetch it once. Each file streams to a <c>.part</c>
/// temporary and is moved into place only on full success, so an interrupted download never leaves a
/// half-written model that <see cref="NemotronModel.IsInstalled"/> would wrongly report as ready.
/// </summary>
public sealed class NemotronModelInstaller
{
    // onnx-community's int4 export of nvidia/nemotron-... (weights per the model card's licence).
    private const string BaseUrl =
        "https://huggingface.co/onnx-community/nemotron-3.5-asr-streaming-0.6b-onnx-int4/resolve/main/";

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

        // ConfigureAwait(false) throughout: keep the large download off (and independent of) the
        // caller's SynchronizationContext.
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
