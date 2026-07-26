using System;

namespace Jot.Transcription.Ctc;

/// <summary>
/// 80-band log-mel front-end for the Parakeet CTC 110M export.
///
/// This reproduces NeMo's <c>AudioToMelSpectrogramPreprocessor</c> with the values the checkpoint
/// itself carries (parakeet-tdt_ctc-110m model_config.yaml): sample_rate 16000, window hann,
/// window_size 0.025, window_stride 0.01, n_fft 512, features 80, normalize per_feature, plus the
/// NeMo defaults preemph 0.97, lowfreq 0, highfreq 8000, mel_norm slaney, log_zero_guard add 2^-24.
///
/// IT IS DELIBERATELY *NOT* WHAT sherpa-onnx FEEDS THIS MODEL. sherpa computes a Kaldi fbank
/// (HTK mel scale, Povey window, per-frame DC removal, low/high 20/7600, FLT_EPSILON floor) —
/// a different front-end from the one the model was trained on, which it gets away with because
/// per_feature normalization plus CTC is forgiving. We match the checkpoint, not the third-party
/// runtime, so the transcript comparison against sherpa is a smoke test and the tensor comparison
/// against the NeMo reference is the real contract.
///
/// Structurally this is <see cref="Nemotron.MelFrontend"/> with three constants moved (128->80 mels,
/// 1e-10 -> 2^-24 guard, periodic -> symmetric Hann) and a normalization pass added. It stays a
/// separate class anyway: MelFrontend.NMels is a public const read from the shipping RNNT engines,
/// and their byte-exactness is not worth risking to save a duplicated FFT (design doc D2).
/// </summary>
internal sealed class CtcMelFrontend
{
    public const int SampleRate = 16_000;
    public const int NMels = 80;

    private const int NFft = 512;
    private const int Hop = 160;
    private const int WinLength = 400;
    private const int NBins = NFft / 2 + 1;   // 257
    private const double Preemph = 0.97;
    private const double LogGuard = 1.0 / 16_777_216.0;   // 2^-24, NeMo log_zero_guard_value
    private const double NormEps = 1e-5;

    private readonly double[] _window;        // length 512, hann(400) zero-padded and centered
    private readonly double[][] _melWeights;  // [80][257]

    public CtcMelFrontend()
    {
        _window = BuildWindow();
        _melWeights = BuildMelFilterbank();
    }

    /// <summary>Frames for a sample count. center=True STFT: 1 + n/hop.</summary>
    public static int FrameCount(int sampleCount) => sampleCount <= 0 ? 0 : 1 + sampleCount / Hop;

    /// <summary>log-mel rows <c>result[frame][mel]</c>, per_feature-normalized.</summary>
    public float[][] Compute(float[] samples)
    {
        int n = samples.Length;
        int frames = FrameCount(n);
        if (frames == 0) return [];

        const int Pad = NFft / 2;
        // Virtual padded signal: reflect | preemphasized | reflect. Preemphasis is applied to the
        // WHOLE signal before the STFT (NeMo), not inside each frame (Kaldi) — the difference is
        // visible at every frame boundary.
        double YAt(int j) => j <= 0 ? samples[0] : samples[j] - Preemph * samples[j - 1];
        double Padded(int i)
        {
            if (i < Pad) return YAt(Math.Min(Pad - i, n - 1));
            if (i < Pad + n) return YAt(i - Pad);
            return YAt(Math.Max(n - 2 - (i - Pad - n), 0));
        }

        var mel = new float[frames][];
        var re = new double[NFft];
        var im = new double[NFft];
        var power = new double[NBins];

        for (int f = 0; f < frames; f++)
        {
            int off = f * Hop;
            for (int i = 0; i < NFft; i++) { re[i] = Padded(off + i) * _window[i]; im[i] = 0.0; }
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

        NormalizePerFeature(mel);
        return mel;
    }

    /// <summary>
    /// log-mel features laid out FEATURE-MAJOR as the ONNX <c>audio_signal</c> input wants them:
    /// flat <c>[80, frames]</c> indexed <c>[mel * frames + frame]</c>.
    /// </summary>
    public float[] ComputeFeatureMajor(float[] samples, out int frames)
    {
        float[][] timeMajor = Compute(samples);
        frames = timeMajor.Length;
        var fm = new float[NMels * frames];
        for (int f = 0; f < frames; f++)
        {
            float[] row = timeMajor[f];
            for (int m = 0; m < NMels; m++) fm[m * frames + f] = row[m];
        }
        return fm;
    }

    /// <summary>
    /// NeMo <c>normalize_batch(..., "per_feature")</c>: per-mel-bin mean and UNBIASED (n-1) standard
    /// deviation over the whole utterance, with the epsilon added to the std, not to the variance.
    /// Utterance-global by construction — this is exactly why the spotter cannot be chunked (D1),
    /// and why a dictation that opens with 20 s of silence is normalized against that silence.
    /// </summary>
    private static void NormalizePerFeature(float[][] rows)
    {
        int frames = rows.Length;
        for (int m = 0; m < NMels; m++)
        {
            double sum = 0.0;
            for (int f = 0; f < frames; f++) sum += rows[f][m];
            double mean = sum / frames;

            double sq = 0.0;
            for (int f = 0; f < frames; f++) { double d = rows[f][m] - mean; sq += d * d; }
            double std = frames > 1 ? Math.Sqrt(sq / (frames - 1)) : 0.0;

            double inv = 1.0 / (std + NormEps);
            for (int f = 0; f < frames; f++) rows[f][m] = (float)((rows[f][m] - mean) * inv);
        }
    }

    // torch.hann_window(400, periodic=False) — SYMMETRIC, i.e. an (N-1) denominator. The Nemotron
    // front-end uses the periodic form; they are not interchangeable.
    private static double[] BuildWindow()
    {
        var w = new double[NFft];
        int start = (NFft - WinLength) / 2; // 56 — torch.stft centers a short window inside n_fft
        for (int n = 0; n < WinLength; n++)
            w[start + n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / (WinLength - 1));
        return w;
    }

    // Slaney mel scale (librosa htk=False), which is what librosa.filters.mel gives NeMo.
    private static double HzToMel(double hz)
    {
        const double fMin = 0.0, fSp = 200.0 / 3.0;
        const double minLogHz = 1000.0;
        double minLogMel = (minLogHz - fMin) / fSp;
        double logstep = Math.Log(6.4) / 27.0;
        return hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) / logstep : (hz - fMin) / fSp;
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

        double melMin = HzToMel(0.0), melMax = HzToMel(SampleRate / 2.0);
        var melPoints = new double[NMels + 2];
        for (int i = 0; i < NMels + 2; i++)
            melPoints[i] = MelToHz(melMin + (melMax - melMin) * i / (NMels + 1));

        var weights = new double[NMels][];
        for (int m = 0; m < NMels; m++)
        {
            weights[m] = new double[NBins];
            double fLower = melPoints[m], fCenter = melPoints[m + 1], fUpper = melPoints[m + 2];
            double enorm = 2.0 / (fUpper - fLower);   // librosa norm="slaney"
            for (int k = 0; k < NBins; k++)
            {
                double lower = (fftFreqs[k] - fLower) / (fCenter - fLower);
                double upper = (fUpper - fftFreqs[k]) / (fUpper - fCenter);
                weights[m][k] = Math.Max(0.0, Math.Min(lower, upper)) * enorm;
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
