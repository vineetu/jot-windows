using System.Diagnostics;
using System.IO;
using Jot.Models;
using Jot.Services.Abstractions;
using Jot.Transcription;

namespace Jot.Import;

/// <summary>
/// Imports an audio OR video file of any format into the library. Decodes it to 16 kHz mono via
/// FFmpeg (which handles mp3/mp4/m4a/mov/webm/mkv/ogg/opus/flac/aac/wav/… — everything, downloaded on
/// first use rather than bundled — see <see cref="FfmpegInstaller"/>), then runs it through the
/// on-device transcriber and adds the result to the store. A "Needs transcription" row appears
/// immediately and fills in when done.
/// </summary>
public sealed class MediaImporter
{
    private const int TargetSampleRate = 16_000;

    private readonly ITranscriber _transcriber;
    private readonly IRecordingStore _store;
    private readonly FfmpegInstaller _ffmpeg;

    public MediaImporter(ITranscriber transcriber, IRecordingStore store, FfmpegInstaller ffmpeg)
    {
        _transcriber = transcriber;
        _store = store;
        _ffmpeg = ffmpeg;
    }

    private static string FfmpegPath => FfmpegInstaller.ExePath;

    /// <summary>Call on the UI thread. Adds a pending row, then decodes + transcribes off-thread and
    /// updates the row in place.</summary>
    public async Task ImportAsync(string path)
    {
        var item = new RecordingItem
        {
            Kind = RecordingKind.Dictation,
            CreatedAt = DateTime.Now,
            ModelLabel = "Parakeet",
            Title = Path.GetFileNameWithoutExtension(path),
            Status = RecordingStatus.NeedsTranscription,
        };
        _store.Add(item);

        try
        {
            await _ffmpeg.EnsureInstalledAsync();

            (string text, double duration) = await Task.Run(async () =>
            {
                (float[] samples, double dur) = Decode(path);
                if (samples.Length == 0) throw new InvalidOperationException("No audio found in this file.");
                string t = (await _transcriber.TranscribeAsync(samples, TargetSampleRate)).Trim();
                return (t, dur);
            });

            // Back on the UI thread (awaited from a UI-context caller).
            item.DurationSeconds = duration;
            item.Transcript = text.Length > 0 ? text : "(no speech detected)";
            if (text.Length > 0) item.Title = TitleFrom(text);
            item.Status = RecordingStatus.Complete;
        }
        catch (Exception ex)
        {
            item.Transcript = "Couldn't import this file: " + ex.Message;
            item.Status = RecordingStatus.Complete;
        }
    }

    /// <summary>Decodes any media file to 16 kHz mono Float32 PCM via the bundled FFmpeg.</summary>
    private static (float[] samples, double durationSeconds) Decode(string path)
    {
        if (!File.Exists(FfmpegPath))
            throw new FileNotFoundException("FFmpeg download did not complete.", FfmpegPath);

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-i", path,
            "-ac", "1", "-ar", TargetSampleRate.ToString(),
            "-f", "f32le", "-",   // raw 32-bit float little-endian PCM to stdout
        }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(ms);
        proc.WaitForExit();
        string err = errTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? "FFmpeg could not decode this file." : err.Trim());

        byte[] bytes = ms.ToArray();
        var samples = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
        return (samples, samples.Length / (double)TargetSampleRate);
    }

    private static string TitleFrom(string transcript)
    {
        string[] words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Imported recording";
        string title = string.Join(' ', words.Take(6));
        return words.Length > 6 ? title + "…" : title;
    }
}
