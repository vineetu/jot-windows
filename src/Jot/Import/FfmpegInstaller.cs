using System.IO;
using System.Net.Http;

namespace Jot.Import;

/// <summary>
/// Downloads the bundled-no-more FFmpeg decoder on first use of file import, so the app ships without
/// its ~138 MB weight. Mirrors <see cref="Jot.Transcription.ParakeetModelInstaller"/>'s atomic
/// download-to-.part-then-move pattern. Hosted as a standalone GitHub release (decoupled from app
/// version tags) since it never needs to change in lockstep with the app.
/// </summary>
public sealed class FfmpegInstaller
{
    private const string DownloadUrl =
        "https://github.com/vineetu/jot-windows/releases/download/jot-deps-v1/ffmpeg.exe";

    public static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot", "tools");

    public static string ExePath => Path.Combine(InstallDir, "ffmpeg.exe");

    public static bool IsInstalled => File.Exists(ExePath) && new FileInfo(ExePath).Length > 0;

    /// <summary>Downloads FFmpeg if not already present. Safe to call every time before use.</summary>
    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (IsInstalled) return;

        Directory.CreateDirectory(InstallDir);
        string tempPath = ExePath + ".part";

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            var dest = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            try { await source.CopyToAsync(dest, ct).ConfigureAwait(false); }
            finally { await dest.DisposeAsync().ConfigureAwait(false); }
        }
        finally { await source.DisposeAsync().ConfigureAwait(false); }

        File.Move(tempPath, ExePath, overwrite: true);
    }
}
