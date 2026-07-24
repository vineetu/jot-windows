namespace Jot.Transcription.Nemotron;

/// <summary>
/// Incremental mel for one streaming session: caches every frame that can no longer change (its STFT
/// window lies fully left of the moving end reflect-pad — <see cref="MelFrontend.StableFrameLimit"/>)
/// and recomputes only the volatile tail plus new frames on each append. Before this, every 300 ms
/// caption poll AND the final stop recomputed the mel of the ENTIRE utterance — O(n²) across a
/// dictation, over a second per poll near the end of a long one (measured: 1101 ms avg on the last
/// tenth of an 80 s clip). Results are byte-identical to a full <see cref="MelFrontend.Compute"/>:
/// cached frames were produced by the same math at a length where they were already final.
/// </summary>
internal sealed class StreamingMel(MelFrontend mel)
{
    private readonly List<float[]> _frames = new(); // [0.._stable) are final; the rest get replaced
    private int _stable;

    /// <summary>Full time-major mel for <paramref name="samples"/>, which must be the previous call's
    /// samples plus newly appended audio (a session only ever grows).</summary>
    public float[][] Update(float[] samples)
    {
        if (samples.Length == 0) return [];
        _frames.RemoveRange(_stable, _frames.Count - _stable);   // drop the end-pad-perturbed tail
        _frames.AddRange(mel.ComputeFrom(samples, _stable));
        _stable = Math.Min(MelFrontend.StableFrameLimit(samples.Length), _frames.Count);
        return _frames.ToArray();
    }

    /// <summary>Flattens time-major rows to the fp16 encoder's feature-major layout
    /// (<c>[mel * frames + frame]</c>) — the transform ComputeFeatureMajor used to do.</summary>
    public static float[] ToFeatureMajor(float[][] timeMajor)
    {
        int frames = timeMajor.Length;
        var fm = new float[MelFrontend.NMels * frames];
        for (int f = 0; f < frames; f++)
        {
            float[] row = timeMajor[f];
            for (int m = 0; m < MelFrontend.NMels; m++) fm[m * frames + f] = row[m];
        }
        return fm;
    }
}
