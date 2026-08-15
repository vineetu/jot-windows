using System.Diagnostics;
using System.IO;

namespace Jot.Transcription.Ggml;

/// <summary>
/// "Is Q8_0-on-Vulkan actually good on THIS machine" — the ggml twin of <see cref="GpuProbe"/>.
/// Same clip, same KnownWords, same 150 ms/chunk bar. Cold pass is untimed (shader compile).
/// Never run on the boot path.
/// </summary>
public static class GgmlProbe
{
    public const double MaxAvgChunkMs = 150;

    private static readonly string[] KnownWords = ["quick", "brown", "fox"];

    public static string DefaultClipPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "probe.wav");

    public static ProbeResult Run(NemotronGgufModel model, string? clipPath = null)
    {
        clipPath ??= DefaultClipPath;
        if (!model.IsInstalled)
            return new(false, 0, 0, "", "GGUF not installed");
        if (!GgmlNativeLocator.IsPresent())
            return new(false, 0, 0, "", "transcribe.cpp natives not present");
        if (!File.Exists(clipPath))
            return new(false, 0, 0, "", $"probe clip missing: {clipPath}");

        try
        {
            float[] samples = WavAudio.ReadMono16k(clipPath);
            var opts = new GgmlEngineOptions
            {
                AttContextRight = GgmlEngineOptions.DefaultLookahead,
                Backend = NativeMethods.BackendRequest.Vulkan,
            };
            using var t = new GgmlNemotronTranscriber(model, opts);

            // Cold pass: model load + Vulkan shader compile. Untimed by design.
            string coldText = t.TranscribeAsync(samples, 16_000).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(coldText) && samples.Length > 0)
                return new(false, 0, 0, coldText, "Vulkan probe produced empty text on the cold pass");

            const int slice = 5120;
            var session = t.OpenStream();
            var acceptSw = new Stopwatch();
            double maxAccept = 0;
            var total = Stopwatch.StartNew();
            for (int off = 0; off < samples.Length; off += slice)
            {
                int n = Math.Min(slice, samples.Length - off);
                var buf = new float[n];
                Array.Copy(samples, off, buf, 0, n);
                acceptSw.Restart();
                session.Accept(buf);
                acceptSw.Stop();
                maxAccept = Math.Max(maxAccept, acceptSw.Elapsed.TotalMilliseconds);
            }
            string transcript = session.Finish();
            total.Stop();

            int chunks = Math.Max(1, samples.Length / slice);
            double avg = total.Elapsed.TotalMilliseconds / chunks;

            bool sane = KnownWords.Any(w => transcript.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (!sane)
                return new(false, avg, maxAccept, transcript,
                    $"transcript failed the sanity check (got \"{transcript}\") — Vulkan produced garbage");

            bool viable = avg < MaxAvgChunkMs;
            return new(viable, avg, maxAccept, transcript,
                $"avg {avg:0} ms per 320 ms chunk (limit {MaxAvgChunkMs:0}), max Accept {maxAccept:0} ms");
        }
        catch (Exception ex)
        {
            return new(false, 0, 0, "", "ggml Vulkan probe failed: " + ex.Message);
        }
    }
}
