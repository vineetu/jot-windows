using System;
using System.Linq;
using Jot.Transcription.Nemotron;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The incremental mel MUST be bit-identical to the full recompute it replaced — the engines were
/// validated byte-exact against the Python reference, so even a one-ULP drift here is a regression.
/// </summary>
public class StreamingMelTests
{
    private static float[] Speechish(int n, int seed)
    {
        // Deterministic pseudo-speech: mixed tones + noise, full float range exercised.
        var rng = new Random(seed);
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 180 * i / 16000.0)
                         + 0.2 * Math.Sin(2 * Math.PI * 731 * i / 16000.0)
                         + 0.1 * (rng.NextDouble() * 2 - 1));
        return s;
    }

    [Fact]
    public void IncrementalUpdates_AreBitIdentical_ToFullCompute()
    {
        var mel = new MelFrontend();
        float[] all = Speechish(160_000, seed: 7); // 10 s

        // Feed in awkward, uneven increments (never aligned to hop/chunk boundaries).
        int[] cuts = [1_234, 5_000, 5_001, 48_000, 90_909, 129_500, 160_000];
        var sm = new StreamingMel(mel);
        float[][] incremental = [];
        foreach (int cut in cuts)
            incremental = sm.Update(all[..cut]);

        float[][] full = mel.Compute(all);

        Assert.Equal(full.Length, incremental.Length);
        for (int f = 0; f < full.Length; f++)
            Assert.True(full[f].SequenceEqual(incremental[f]), $"frame {f} differs");
    }

    [Fact]
    public void ComputeFrom_MatchesTheTailOfFullCompute()
    {
        var mel = new MelFrontend();
        float[] samples = Speechish(50_000, seed: 42);
        float[][] full = mel.Compute(samples);
        float[][] tail = mel.ComputeFrom(samples, 100);
        Assert.Equal(full.Length - 100, tail.Length);
        for (int f = 0; f < tail.Length; f++)
            Assert.True(full[100 + f].SequenceEqual(tail[f]), $"frame {100 + f} differs");
    }

    [Fact]
    public void StableFrameLimit_NeverExceedsFrameCount_AndFramesBelowItNeverChange()
    {
        var mel = new MelFrontend();
        float[] all = Speechish(40_000, seed: 3);

        // Claim under test: frames below StableFrameLimit(n) are identical whether computed at
        // length n or at any longer length — that's what makes caching them safe.
        int nShort = 24_000;
        float[][] atShort = mel.Compute(all[..nShort]);
        float[][] atFull = mel.Compute(all);
        int stable = MelFrontend.StableFrameLimit(nShort);
        Assert.True(stable <= atShort.Length);
        Assert.True(stable > 0);
        for (int f = 0; f < stable; f++)
            Assert.True(atShort[f].SequenceEqual(atFull[f]), $"'stable' frame {f} changed with more audio");
    }

    [Fact]
    public void ToFeatureMajor_MatchesTheOldLayout()
    {
        var mel = new MelFrontend();
        float[] samples = Speechish(20_000, seed: 9);
        float[] oldLayout = mel.ComputeFeatureMajor(samples, out int frames);
        float[] newLayout = StreamingMel.ToFeatureMajor(mel.Compute(samples));
        Assert.Equal(frames * MelFrontend.NMels, newLayout.Length);
        Assert.True(oldLayout.SequenceEqual(newLayout));
    }

    [Fact]
    public void EmptyAndTinyInputs_AreSafe()
    {
        var mel = new MelFrontend();
        Assert.Empty(new StreamingMel(mel).Update([]));
        var sm = new StreamingMel(mel);
        float[][] tiny = sm.Update(Speechish(10, 1)); // shorter than one window
        Assert.Equal(MelFrontend.FrameCount(10), tiny.Length);
    }
}
