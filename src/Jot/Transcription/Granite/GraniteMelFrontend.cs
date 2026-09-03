using System;

namespace Jot.Transcription.Granite;

/// <summary>
/// Feature front-end for Granite Speech 5.0 TurboCTC — a port of
/// <c>GraniteSpeech5FeatureExtractor._extract_features</c> (transformers 5.16).
///
/// Output is <c>[T/2, 320]</c>: each row is TWO consecutive 160-dim frames concatenated, and each
/// 160-dim frame is 80 log-mels followed by their 80 time-deltas. 100 Hz mels in, 50 Hz rows out.
///
/// EVERY constant here is torchaudio's default, not NeMo's, and the two disagree in ways that are
/// silent rather than loud — a wrong mel scale still produces fluent-looking text with a worse WER.
/// The differences from <see cref="Ctc.CtcMelFrontend"/> (the spotter's 80-mel front-end, which
/// tracks NeMo) are therefore deliberate and load-bearing:
///   * HTK mel scale, NOT Slaney.
///   * NO filter-area normalization (torchaudio <c>norm=None</c>); Slaney's 2/(hi-lo) is absent.
///   * PERIODIC Hann (2*pi*n/N), not symmetric (N-1).
///   * NO preemphasis at all.
///   * log10 with a 1e-10 clamp, then a per-utterance floor at (max - 8.0), then /4 + 1 —
///     not ln with an additive guard, and not per-feature mean/variance normalization.
/// Sharing code with the spotter's front-end would put its verified byte-exactness on the line to
/// save a duplicated FFT; the repo already made that call once (design doc D2) and this follows it.
///
/// Verified against a dumped torchaudio tensor for Assets/probe.wav — see GraniteMelFrontendTests.
/// </summary>
internal sealed class GraniteMelFrontend
{
    public const int SampleRate = 16_000;
    public const int NMels = 80;
    /// <summary>Mels + deltas per mel frame.</summary>
    public const int FrameDim = NMels * 2;          // 160
    /// <summary>Consecutive mel frames stacked into one encoder row.</summary>
    public const int FrameStacking = 2;
    /// <summary>Width of the encoder's <c>input_features</c> input.</summary>
    public const int InputDim = FrameDim * FrameStacking;   // 320

    private const int NFft = 512;
    private const int Hop = 160;
    private const int WinLength = 400;
    private const int NBins = NFft / 2 + 1;         // 257
    private const double MelClamp = 1e-10;          // clamp_min_ before log10
    private const double FloorDb = 8.0;             // logmel_floor_db, applied below the GLOBAL max

    private readonly double[] _window;              // length 512: hann(400) centered, zero-padded
    private readonly double[][] _melWeights;        // [80][257]

    public GraniteMelFrontend()
    {
        _window = BuildWindow();
        _melWeights = BuildMelFilterbank();
    }

    /// <summary>
    /// Mel frames the extractor keeps for a sample count: <c>n / hop</c> rounded UP to an even
    /// number, because the trailing frame-stacking pair is padded rather than dropped. Note this is
    /// <c>n / hop</c>, NOT the STFT's <c>1 + n / hop</c> — the extractor computes the full STFT and
    /// then slices, so an off-by-one here silently shifts every row.
    /// </summary>
    public static int MelFrameCount(int sampleCount)
    {
        if (sampleCount <= 0) return 0;
        int frames = sampleCount / Hop;
        return frames % 2 == 0 ? frames : frames + 1;
    }

    /// <summary>Encoder rows for a sample count.</summary>
    public static int RowCount(int sampleCount) => MelFrameCount(sampleCount) / FrameStacking;

    /// <summary>
    /// Encoder input, flat and row-major: <c>[rows, 320]</c> indexed <c>[row * 320 + i]</c>.
    /// </summary>
    public float[] Compute(float[] samples, out int rows)
    {
        int melFrames = MelFrameCount(samples.Length);
        rows = melFrames / FrameStacking;
        if (rows == 0) return [];

        // The extractor right-pads the waveform with zeros so the last stacking pair is complete.
        // Padding the SIGNAL (not the features) matters: the STFT window straddles the boundary,
        // so zero-padding here and zero-padding a feature row are different tensors.
        int needed = (melFrames - 1) * Hop + 1;
        float[] audio = samples;
        if (audio.Length < needed)
        {
            audio = new float[needed];
            Array.Copy(samples, audio, samples.Length);
        }

        double[][] logmel = LogMel(audio, melFrames);   // [melFrames][80]
        ApplyFloorAndScale(logmel);
        double[][] deltas = ComputeDeltas(logmel);      // [melFrames][80]

        // [T,160] -> [T/2, 320]: row r is frame 2r followed by frame 2r+1.
        var outFeat = new float[rows * InputDim];
        for (int r = 0; r < rows; r++)
        {
            for (int half = 0; half < FrameStacking; half++)
            {
                int t = r * FrameStacking + half;
                int at = r * InputDim + half * FrameDim;
                for (int m = 0; m < NMels; m++)
                {
                    outFeat[at + m] = (float)logmel[t][m];
                    outFeat[at + NMels + m] = (float)deltas[t][m];
                }
            }
        }
        return outFeat;
    }

    /// <summary>log10 mel power spectrogram, clamped at 1e-10. <c>result[frame][mel]</c>.</summary>
    private double[][] LogMel(float[] audio, int frames)
    {
        int n = audio.Length;
        const int Pad = NFft / 2;   // center=True

        // torch.stft(center=True, pad_mode="reflect"): reflect WITHOUT repeating the edge sample,
        // i.e. index -1 maps to sample 1, not sample 0.
        double Padded(int i)
        {
            int j = i - Pad;
            if (j < 0) j = -j;
            if (j >= n) j = 2 * (n - 1) - j;
            return j >= 0 && j < n ? audio[j] : 0.0;
        }

        var mel = new double[frames][];
        var re = new double[NFft];
        var im = new double[NFft];
        var power = new double[NBins];

        for (int f = 0; f < frames; f++)
        {
            int off = f * Hop;
            for (int i = 0; i < NFft; i++) { re[i] = Padded(off + i) * _window[i]; im[i] = 0.0; }
            Fft(re, im);
            for (int k = 0; k < NBins; k++) power[k] = re[k] * re[k] + im[k] * im[k];  // power=2.0

            var row = new double[NMels];
            for (int m = 0; m < NMels; m++)
            {
                double[] w = _melWeights[m];
                double acc = 0.0;
                for (int k = 0; k < NBins; k++) acc += w[k] * power[k];
                row[m] = Math.Log10(Math.Max(acc, MelClamp));
            }
            mel[f] = row;
        }
        return mel;
    }

    /// <summary>
    /// <c>max(logmel, globalMax - 8.0) / 4 + 1</c>. The max is over the WHOLE utterance (both mel
    /// and time axes), which is what makes this front-end non-chunkable: the same audio scored in
    /// two different windows normalizes differently. The streaming session therefore recomputes
    /// features over the whole buffer rather than appending them.
    /// </summary>
    private static void ApplyFloorAndScale(double[][] logmel)
    {
        double max = double.NegativeInfinity;
        foreach (double[] row in logmel)
            foreach (double v in row)
                if (v > max) max = v;

        double floor = max - FloorDb;
        foreach (double[] row in logmel)
            for (int m = 0; m < NMels; m++)
                row[m] = (Math.Max(row[m], floor) / 4.0) + 1.0;
    }

    /// <summary>
    /// torchaudio <c>compute_deltas(win_length=3)</c>: kernel [-1,0,1] over time divided by
    /// denom = n(n+1)(2n+1)/3 = 2 for n=1, with REPLICATE edge padding. So d[t] = (x[t+1]-x[t-1])/2,
    /// and at the edges the missing neighbour is the edge frame itself.
    /// </summary>
    private static double[][] ComputeDeltas(double[][] x)
    {
        int t = x.Length;
        const double Denom = 2.0;
        var d = new double[t][];
        for (int i = 0; i < t; i++)
        {
            var row = new double[NMels];
            double[] prev = x[i == 0 ? 0 : i - 1];
            double[] next = x[i == t - 1 ? t - 1 : i + 1];
            for (int m = 0; m < NMels; m++) row[m] = (next[m] - prev[m]) / Denom;
            d[i] = row;
        }
        return d;
    }

    /// <summary>
    /// torch.hann_window(400) — PERIODIC (2*pi*n/N), which is torchaudio's default and differs from
    /// the spotter front-end's symmetric window. torch.stft centers a short window inside n_fft.
    /// </summary>
    private static double[] BuildWindow()
    {
        var w = new double[NFft];
        int start = (NFft - WinLength) / 2;   // 56
        for (int n = 0; n < WinLength; n++)
            w[start + n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / WinLength);
        return w;
    }

    // HTK mel scale (torchaudio mel_scale="htk"), NOT Slaney.
    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);

    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

    /// <summary>
    /// torchaudio <c>melscale_fbanks(norm=None)</c>: triangular filters over linearly spaced FFT
    /// bins, with NO area normalization. Leaving Slaney's 2/(hi-lo) in would scale every band.
    /// </summary>
    private static double[][] BuildMelFilterbank()
    {
        var fftFreqs = new double[NBins];
        for (int k = 0; k < NBins; k++) fftFreqs[k] = k * (SampleRate / 2.0) / (NBins - 1);

        double melMin = HzToMel(0.0), melMax = HzToMel(SampleRate / 2.0);
        var fPts = new double[NMels + 2];
        for (int i = 0; i < NMels + 2; i++)
            fPts[i] = MelToHz(melMin + (melMax - melMin) * i / (NMels + 1));

        var weights = new double[NMels][];
        for (int m = 0; m < NMels; m++)
        {
            weights[m] = new double[NBins];
            double lo = fPts[m], mid = fPts[m + 1], hi = fPts[m + 2];
            for (int k = 0; k < NBins; k++)
            {
                double down = (fftFreqs[k] - lo) / (mid - lo);
                double up = (hi - fftFreqs[k]) / (hi - mid);
                weights[m][k] = Math.Max(0.0, Math.Min(down, up));
            }
        }
        return weights;
    }

    // In-place iterative radix-2 Cooley-Tukey FFT (n must be a power of two).
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1.0, curIm = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tRe = re[b] * curRe - im[b] * curIm;
                    double tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                    re[a] += tRe; im[a] += tIm;
                    double nRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nRe;
                }
            }
        }
    }
}
