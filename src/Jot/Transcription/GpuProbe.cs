using System.Diagnostics;
using System.IO;
using Jot.Transcription.Nemotron;
using Jot.Transcription.Onnx;

namespace Jot.Transcription;

/// <summary>Outcome of one GPU speed/sanity check. <see cref="Reason"/> is diagnostics-grade text.</summary>
public sealed record ProbeResult(
    bool GpuViable, double AvgChunkMs, double MaxAcceptMs, string Transcript, string Reason);

/// <summary>
/// The authoritative "is fp16-on-DirectML actually good on THIS machine" benchmark. Runs real speech
/// through the real fp16 streaming pipeline: a cold pass (session + DML graph compile — untimed, but it
/// catches load failures and silent CPU fallback), then a warm streaming pass timed per 320 ms chunk.
/// Real recorded speech is essential, not synthetic tones: the known DML failure mode for this model
/// family is a SILENT wrong/empty transcript (the GLU Split-fusion class of bug), which only a
/// known-transcript clip can catch. Never run on the boot path — the cold pass takes seconds.
/// </summary>
public static class GpuProbe
{
    /// <summary>Viability bar: average wall time per 320 ms chunk. 150 ms ≈ 2x realtime headroom, enough
    /// to survive thermal throttling and background load without captions falling behind.</summary>
    public const double MaxAvgChunkMs = 150;

    /// <summary>The probe clip ships in Assets (a self-recorded phrase); any of these words in the
    /// transcript proves the pipeline produced real text, not garbage.</summary>
    private static readonly string[] KnownWords = ["quick", "brown", "fox"];

    /// <summary>Default probe clip path (next to the exe; packaged into the MSIX with the other assets).</summary>
    public static string DefaultClipPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "probe.wav");

    public static ProbeResult Run(NemotronFp16Model model, string? clipPath = null)
    {
        clipPath ??= DefaultClipPath;
        if (!model.IsInstalled)
            return new(false, 0, 0, "", "fp16 model not installed");
        if (!File.Exists(clipPath))
            return new(false, 0, 0, "", $"probe clip missing: {clipPath}");

        try
        {
            float[] samples = WavAudio.ReadMono16k(clipPath);

            bool fellBack = false;
            var factory = new OnnxSessionFactory();
            factory.BackendFallback += _ => fellBack = true;
            using var t = new NemotronFp16Transcriber(model, factory, ComputeBackend.DirectML);

            // Cold pass: session creation + DML graph compile + first inference. Untimed by design —
            // it's a once-per-launch cost, not the per-chunk latency the viability rule is about.
            string coldText = t.TranscribeAsync(samples, 16_000).GetAwaiter().GetResult();
            if (fellBack)
                return new(false, 0, 0, coldText, "DirectML unavailable — session fell back to CPU");

            // Warm pass: the REAL streaming path, fed in 320 ms slices (= one 32-frame mel chunk each).
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
                    $"transcript failed the sanity check (got \"{transcript}\") — DML produced garbage");

            bool viable = avg < MaxAvgChunkMs;
            return new(viable, avg, maxAccept, transcript,
                $"avg {avg:0} ms per 320 ms chunk (limit {MaxAvgChunkMs:0}), max Accept {maxAccept:0} ms");
        }
        catch (Exception ex)
        {
            return new(false, 0, 0, "", "fp16 DirectML probe failed: " + ex.Message);
        }
    }
}
