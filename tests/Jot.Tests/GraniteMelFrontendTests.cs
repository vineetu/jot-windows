using System;
using System.IO;
using System.Text.Json;
using Jot.Transcription;
using Jot.Transcription.Granite;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// The Granite front-end's real contract: does it produce the SAME tensor torchaudio produces?
///
/// This is the failure mode worth spending a fixture on. A front-end that is subtly wrong — HTK vs
/// Slaney mel, periodic vs symmetric Hann, a missing filter normalization, an off-by-one frame
/// count — does not throw and does not produce gibberish. It produces fluent, plausible text with a
/// quietly worse WER, which no smoke test catches and no user reports as a bug. So the fact is
/// pinned to a golden tensor dumped from the Python extractor rather than to "it ran".
///
/// The fixture is 404 KB and lives in the repo precisely so this runs in CI, with no checkpoint and
/// no 536 MB download.
/// </summary>
public class GraniteMelFrontendTests(ITestOutputHelper output)
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "granite");

    private static string ProbeWav =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "probe.wav");

    private static (int Rows, int Dim, int Samples) GoldenShape()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDir, "probe_features.json")));
        JsonElement shape = doc.RootElement.GetProperty("shape");
        return (shape[1].GetInt32(), shape[2].GetInt32(),
                doc.RootElement.GetProperty("samples").GetInt32());
    }

    private static float[] GoldenFeatures()
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(FixtureDir, "probe_features.f32"));
        var f = new float[raw.Length / sizeof(float)];
        Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
        return f;
    }

    // ------------------------------------------------------------------ pure logic

    [Theory]
    // n/hop rounded UP to even. NOT the STFT's 1 + n/hop: the extractor computes the full STFT and
    // slices to this count, so using the STFT length shifts the whole stacking grid by one frame.
    [InlineData(101256, 632)]   // probe.wav: 632 frames exactly, no rounding
    [InlineData(16000, 100)]    // 1 s -> 100 frames
    [InlineData(16080, 100)]    // 100.5 frames floors to 100, already even
    [InlineData(16160, 102)]    // 101 frames -> rounded up to 102
    [InlineData(0, 0)]
    [InlineData(80, 0)]         // less than one hop
    public void MelFrameCount_rounds_up_to_an_even_number_of_frames(int samples, int expected)
        => Assert.Equal(expected, GraniteMelFrontend.MelFrameCount(samples));

    [Fact]
    public void RowCount_is_half_the_mel_frames()
    {
        Assert.Equal(316, GraniteMelFrontend.RowCount(101256));
        Assert.Equal(50, GraniteMelFrontend.RowCount(16000));
        Assert.Equal(0, GraniteMelFrontend.RowCount(80));
    }

    [Fact]
    public void Compute_returns_no_rows_for_audio_shorter_than_one_hop()
    {
        var fe = new GraniteMelFrontend();
        float[] f = fe.Compute(new float[80], out int rows);
        Assert.Equal(0, rows);
        Assert.Empty(f);
    }

    // ------------------------------------------------------------------ the golden tensor

    [Fact]
    public void Features_match_the_torchaudio_reference_for_probe_wav()
    {
        Assert.True(File.Exists(ProbeWav), $"probe.wav missing at {ProbeWav}");

        (int goldRows, int goldDim, int goldSamples) = GoldenShape();
        float[] gold = GoldenFeatures();
        Assert.Equal(goldRows * goldDim, gold.Length);
        Assert.Equal(GraniteMelFrontend.InputDim, goldDim);

        float[] samples = WavAudio.ReadMono16k(ProbeWav);
        Assert.Equal(goldSamples, samples.Length);

        var fe = new GraniteMelFrontend();
        float[] got = fe.Compute(samples, out int rows);

        Assert.Equal(goldRows, rows);
        Assert.Equal(gold.Length, got.Length);

        double maxAbs = 0.0, sumSq = 0.0;
        int worst = -1;
        for (int i = 0; i < gold.Length; i++)
        {
            double d = Math.Abs(gold[i] - got[i]);
            if (d > maxAbs) { maxAbs = d; worst = i; }
            sumSq += d * d;
        }
        double rms = Math.Sqrt(sumSq / gold.Length);
        output.WriteLine($"rows={rows} dim={goldDim} maxAbs={maxAbs:E3} rms={rms:E3} " +
                         $"worst=row {worst / goldDim} col {worst % goldDim}");

        // Tolerance, not equality: torchaudio computes the STFT and filterbank in float32 while this
        // port accumulates in double, so the two differ by float32 rounding. The bar is set well
        // below anything that could move a CTC argmax — a genuine front-end mistake (wrong mel scale,
        // wrong window, missing normalization) lands orders of magnitude above this, not near it.
        Assert.True(maxAbs < 2e-3, $"max abs diff {maxAbs:E3} exceeds 2e-3");
        Assert.True(rms < 1e-4, $"rms diff {rms:E3} exceeds 1e-4");
    }

    [Fact]
    public void Deltas_follow_the_mels_in_every_stacked_half()
    {
        // Layout guard: each 320-wide row is [mels(80) deltas(80)] twice. If the halves were ever
        // swapped or the delta block moved, the golden-tensor fact above would catch it — but only
        // for one clip. This states the layout directly.
        float[] samples = WavAudio.ReadMono16k(ProbeWav);
        var fe = new GraniteMelFrontend();
        float[] got = fe.Compute(samples, out int rows);

        // A delta is (next - prev) / 2 of the mel block, so recompute one and compare.
        const int Dim = GraniteMelFrontend.InputDim;
        const int NMels = GraniteMelFrontend.NMels;
        float MelAt(int frame, int m)
        {
            int r = frame / 2, half = frame % 2;
            return got[r * Dim + half * GraniteMelFrontend.FrameDim + m];
        }
        float DeltaAt(int frame, int m)
        {
            int r = frame / 2, half = frame % 2;
            return got[r * Dim + half * GraniteMelFrontend.FrameDim + NMels + m];
        }

        int frames = rows * 2;
        for (int frame = 3; frame < frames - 3; frame += 97)
        {
            for (int m = 0; m < NMels; m += 17)
            {
                double expected = (MelAt(frame + 1, m) - MelAt(frame - 1, m)) / 2.0;
                Assert.True(Math.Abs(expected - DeltaAt(frame, m)) < 1e-5,
                            $"delta mismatch at frame {frame} mel {m}");
            }
        }
    }
}
