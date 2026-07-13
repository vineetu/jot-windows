using System;

namespace Jot.Transcription.Nemotron;

/// <summary>
/// 128-band log-mel front-end for Nemotron 3.5, byte-compatible with NeMo's FilterbankFeatures /
/// the validated Python reference: preemphasis 0.97 → reflect-pad n_fft/2 → periodic-Hann(400)
/// centered in a 512-pt STFT (hop 160) → power spectrum → Slaney mel filterbank (128 bins, 0–8000 Hz,
/// Slaney-normalized) → log(mel + 1e-10). Dither is OFF (deterministic). Output is TIME-MAJOR
/// <c>[frames][128]</c> — the encoder's <c>audio_signal</c> wants [batch, frames, mels].
///
/// Everything runs in double to track numpy's rfft precision (argmax ties otherwise drift).
/// </summary>
internal sealed class MelFrontend
{
    public const int SampleRate = 16_000;
    private const int NFft = 512;
    private const int Hop = 160;
    private const int WinLength = 400;
    public const int NMels = 128;
    private const int NBins = NFft / 2 + 1; // 257
    private const double Preemph = 0.97;
    private const double LogGuard = 1e-10;

    private readonly double[] _window;        // length 512
    private readonly double[][] _melWeights;  // [128][257]

    public MelFrontend()
    {
        _window = BuildWindow();
        _melWeights = BuildMelFilterbank();
    }

    /// <summary>Returns log-mel features as time-major rows: <c>result[frame][mel]</c>.</summary>
    public float[][] Compute(float[] samples)
    {
        // 1. Preemphasis on the raw signal.
        var y = new double[samples.Length];
        if (samples.Length > 0) y[0] = samples[0];
        for (int i = 1; i < samples.Length; i++) y[i] = samples[i] - Preemph * samples[i - 1];

        // 2. Reflect-pad by n_fft/2 both sides (torch.stft center=True).
        double[] padded = ReflectPad(y, NFft / 2);

        int frames = padded.Length >= NFft ? 1 + (padded.Length - NFft) / Hop : 0;
        var mel = new float[frames][];
        var re = new double[NFft];
        var im = new double[NFft];
        var power = new double[NBins];

        for (int f = 0; f < frames; f++)
        {
            int off = f * Hop;
            for (int n = 0; n < NFft; n++) { re[n] = padded[off + n] * _window[n]; im[n] = 0.0; }
            Fft(re, im);
            for (int k = 0; k < NBins; k++) power[k] = re[k] * re[k] + im[k] * im[k];

            var row = new float[NMels];
            for (int m = 0; m < NMels; m++)
            {
                double[] w = _melWeights[m];
                double acc = 0.0;
                for (int k = 0; k < NBins; k++) acc += w[k] * power[k];
                row[m] = (float)Math.Log(acc + LogGuard);
            }
            mel[f] = row;
        }
        return mel;
    }

    private static double[] ReflectPad(double[] y, int pad)
    {
        int n = y.Length;
        var outp = new double[n + 2 * pad];
        Array.Copy(y, 0, outp, pad, n);
        for (int i = 0; i < pad; i++)
        {
            outp[pad - 1 - i] = y[Math.Min(i + 1, n - 1)];          // left reflect (no edge repeat)
            outp[pad + n + i] = y[Math.Max(n - 2 - i, 0)];          // right reflect
        }
        return outp;
    }

    private static double[] BuildWindow()
    {
        var w = new double[NFft];
        int start = (NFft - WinLength) / 2; // 56
        for (int n = 0; n < WinLength; n++)
            w[start + n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / WinLength); // periodic Hann
        return w;
    }

    // Slaney mel scale (librosa htk=False).
    private static double HzToMel(double hz)
    {
        const double fMin = 0.0, fSp = 200.0 / 3.0;
        double mel = (hz - fMin) / fSp;
        const double minLogHz = 1000.0;
        double minLogMel = (minLogHz - fMin) / fSp;
        double logstep = Math.Log(6.4) / 27.0;
        return hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) / logstep : mel;
    }

    private static double MelToHz(double mel)
    {
        const double fMin = 0.0, fSp = 200.0 / 3.0;
        const double minLogHz = 1000.0;
        double minLogMel = (minLogHz - fMin) / fSp;
        double logstep = Math.Log(6.4) / 27.0;
        return mel >= minLogMel ? minLogHz * Math.Exp(logstep * (mel - minLogMel)) : fMin + fSp * mel;
    }

    private static double[][] BuildMelFilterbank()
    {
        var fftFreqs = new double[NBins];
        for (int k = 0; k < NBins; k++) fftFreqs[k] = k * (double)SampleRate / NFft;

        double melMin = HzToMel(0.0), melMax = HzToMel(8000.0);
        var melPoints = new double[NMels + 2];
        for (int i = 0; i < NMels + 2; i++)
            melPoints[i] = MelToHz(melMin + (melMax - melMin) * i / (NMels + 1));

        var weights = new double[NMels][];
        for (int m = 0; m < NMels; m++)
        {
            weights[m] = new double[NBins];
            double fLower = melPoints[m], fCenter = melPoints[m + 1], fUpper = melPoints[m + 2];
            double enorm = 2.0 / (fUpper - fLower); // Slaney normalization
            for (int k = 0; k < NBins; k++)
            {
                double lower = (fftFreqs[k] - fLower) / (fCenter - fLower);
                double upper = (fUpper - fftFreqs[k]) / (fUpper - fCenter);
                double v = Math.Max(0.0, Math.Min(lower, upper));
                weights[m][k] = v * enorm;
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
