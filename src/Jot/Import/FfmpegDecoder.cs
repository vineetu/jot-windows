using System.Diagnostics;
using System.IO;

namespace Jot.Import;

/// <summary>
/// The one FFmpeg decode: any audio/video file to 16 kHz mono Float32 PCM. The exe path is a PARAMETER,
/// not <see cref="FfmpegInstaller.ExePath"/>: that static hangs off <c>JotPaths.AppDataRoot</c>, which for
/// a process without MSIX package identity (the `jot` CLI) can never resolve a Store install's tools
/// folder. Callers that have identity pass <see cref="FfmpegInstaller.ExePath"/>.
/// </summary>
public static class FfmpegDecoder
{
    private const int TargetSampleRate = 16_000;

    public static (float[] samples, double durationSeconds) DecodeToMono16k(string ffmpegExe, string path)
    {
        if (!File.Exists(ffmpegExe))
            throw new FileNotFoundException("FFmpeg download did not complete.", ffmpegExe);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
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
}
