using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// M0 spike coverage for the CTC word-spotter front-end (docs/plans/vocabulary-ctc-port.md §5 M0).
///
/// Split in two on purpose:
///   * pure-logic facts run everywhere and are the CI-visible contract;
///   * the model-dependent facts are a REAL xunit skip (see <see cref="ModelFactAttribute"/>) unless
///     JOT_CTC_SPIKE_DIR / JOT_CTC_SPIKE_AUDIO point at a local sherpa-onnx Parakeet-CTC export + WAVs.
///     They must never make CI depend on a 130 MB download or on the user's own recordings — and they
///     must never report GREEN for a fact that did not run, which is what the old early-return did.
/// </summary>
public class CtcSpikeTests(ITestOutputHelper output)
{
    private static string? ModelDir => Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_DIR");
    private static string? AudioDir => Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_AUDIO");

    private static bool HaveModel =>
        ModelDir is { Length: > 0 } d && File.Exists(Path.Combine(d, "tokens.txt"));

    private static string ModelOnnx()
    {
        string d = ModelDir!;
        string p = Path.Combine(d, "model.int8.onnx");
        return File.Exists(p) ? p : Path.Combine(d, "model.onnx");
    }

    // ---------------------------------------------------------------- pure logic

    [Theory]
    // NeMo get_seq_len with a center=True STFT: floor(n / hop) + 1. NOT kaldi's (n + hop/2) / hop,
    // which sherpa-onnx uses and which is off by one on most lengths.
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(160, 2)]
    [InlineData(16_000, 101)]
    [InlineData(118_960, 744)]     // sherpa's own test_wavs/0.wav; the Python reference agrees
    [InlineData(640_000, 4001)]    // 40 s
    public void FrameCount_MatchesNeMoGetSeqLen(int samples, int expected) =>
        Assert.Equal(expected, CtcMelFrontend.FrameCount(samples));

    [Fact]
    public void PerFeatureNormalization_ZeroMeanUnitVarianceAcrossFrames()
    {
        // Not a tautology: it fails if the normalization is done per-FRAME (the other plausible
        // reading of "per_feature") or is applied before the log. The measured sd lands just under
        // 1 rather than on it because NeMo divides by the UNBIASED (n-1) std.
        var mel = new CtcMelFrontend().Compute(Chirp(16_000 * 2));
        Assert.True(mel.Length > 100);

        for (int m = 0; m < CtcMelFrontend.NMels; m++)
        {
            double sum = 0, sumSq = 0;
            foreach (float[] row in mel) { sum += row[m]; sumSq += (double)row[m] * row[m]; }
            double mean = sum / mel.Length;
            double sd = Math.Sqrt(sumSq / mel.Length - mean * mean);
            Assert.InRange(mean, -1e-3, 1e-3);
            Assert.InRange(sd, 0.99, 1.01);
        }
    }

    [Fact]
    public void FeatureMajorLayout_IsTransposeOfTimeMajor()
    {
        var fe = new CtcMelFrontend();
        float[] pcm = Chirp(16_000);
        float[][] tm = fe.Compute(pcm);
        float[] fm = fe.ComputeFeatureMajor(pcm, out int frames);

        Assert.Equal(tm.Length, frames);
        Assert.Equal(CtcMelFrontend.NMels * frames, fm.Length);
        for (int f = 0; f < frames; f += 7)
            for (int m = 0; m < CtcMelFrontend.NMels; m += 11)
                Assert.Equal(tm[f][m], fm[m * frames + f]);
    }

    [Fact]
    public void MelFrontend_IsScaleInvariantAwayFromTheLogGuard()
    {
        // per_feature normalization cancels a constant gain, which is why we can hand the recorder's
        // float[] straight to the model without matching any external level convention.
        //
        // MEASURED CAVEAT, do not "fix": the log guard (2^-24) is an absolute floor, so bins whose
        // energy approaches it stop scaling linearly. Preemphasis has a deep notch below ~150 Hz, so
        // the lowest bins get there first — this test therefore only claims invariance where the
        // signal is well above the guard. Real consequence: to this model a quiet recording is not
        // simply a scaled loud one.
        var fe = new CtcMelFrontend();
        float[] a = Chirp(16_000);
        float[] b = a.Select(x => x * 0.25f).ToArray();

        float[][] ma = fe.Compute(a), mb = fe.Compute(b);
        double sumSq = 0; int n = 0; double max = 0;
        for (int f = 0; f < ma.Length; f++)
            for (int m = 0; m < CtcMelFrontend.NMels; m++)
            {
                double d = ma[f][m] - mb[f][m];
                sumSq += d * d; n++; max = Math.Max(max, Math.Abs(d));
            }
        double rms = Math.Sqrt(sumSq / n);
        output.WriteLine($"gain-invariance: rms={rms:E3} max={max:E3}");
        for (int lo = 0; lo < 80; lo += 10)
        {
            double s2 = 0; int c = 0;
            for (int f = 0; f < ma.Length; f++)
                for (int m = lo; m < lo + 10; m++) { double d = ma[f][m] - mb[f][m]; s2 += d * d; c++; }
            output.WriteLine($"  bins {lo}-{lo + 9}: rms={Math.Sqrt(s2 / c):E3}");
        }

        // The claim is about the UPPER half of the filterbank, and the per-band numbers above show
        // why: sensitivity to gain falls monotonically with frequency (measured 1.8e-1 in bins 0-9
        // down to 1.5e-4 in bins 70-79) because the 2^-24 log guard is an ABSOLUTE floor and
        // preemphasis has already gutted the low bins. Bins 40+ carry the speech energy and are
        // invariant; below that, a 12 dB level change genuinely changes the features.
        //
        // That is a real M2 risk, not a test artifact: spotter score thresholds calibrated on
        // loud dictations may not transfer to quiet ones.
        double hiSumSq = 0; int hiN = 0;
        for (int f = 0; f < ma.Length; f++)
            for (int m = 40; m < CtcMelFrontend.NMels; m++)
            {
                double d = ma[f][m] - mb[f][m];
                hiSumSq += d * d; hiN++;
            }
        Assert.True(Math.Sqrt(hiSumSq / hiN) < 5e-3, "upper filterbank is not gain-invariant");
    }

    [Fact]
    public void MelFrontend_ShortSignalIsHandledByReflection()
    {
        // Empty -> no frames; 1 sample -> 1 frame whose 512-point window is entirely reflection.
        // Must not throw, divide by zero in the (n-1) std, or produce NaN.
        var fe = new CtcMelFrontend();
        Assert.Empty(fe.Compute([]));

        float[][] one = fe.Compute(new float[1]);
        Assert.Single(one);
        Assert.All(one[0], v => Assert.False(float.IsNaN(v) || float.IsInfinity(v)));

        float[][] mel = fe.Compute(Chirp(100));
        Assert.Single(mel);
        Assert.All(mel[0], v => Assert.False(float.IsNaN(v) || float.IsInfinity(v)));
    }

    [Fact]
    public void GreedyDecode_CollapsesRepeatsAndDropsBlank()
    {
        const int V = 4, Blank = 3;
        // a a _ a b b _   ->  a a b   (the blank between the two 'a's breaks the collapse)
        int[] argmax = [0, 0, Blank, 0, 1, 1, Blank];
        var tokens = CtcGreedyDecoder.Decode(OneHot(argmax, V), argmax.Length, V, Blank);

        Assert.Equal([0, 0, 1], tokens.Select(t => t.Id));
        Assert.Equal([0, 3, 4], tokens.Select(t => t.Frame));
    }

    [Fact]
    public void GreedyDecode_AllBlankYieldsNothing()
    {
        const int V = 4, Blank = 3;
        int[] argmax = [Blank, Blank, Blank];
        Assert.Empty(CtcGreedyDecoder.Decode(OneHot(argmax, V), argmax.Length, V, Blank));
    }

    [Fact]
    public void GreedyDecode_TieTakesLowestIndex()
    {
        // std::max_element keeps the first maximum; a >= comparison here would silently change
        // transcripts versus the oracle.
        const int V = 3, Blank = 2;
        var probs = new float[] { -1f, -1f, -5f };
        var tokens = CtcGreedyDecoder.Decode(probs, 1, V, Blank);
        Assert.Equal(0, Assert.Single(tokens).Id);
    }

    [Fact]
    public void Tokens_DecodeMatchesSherpaConvention()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ctc-tokens-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "<unk> 0\n▁Hello 1\n, 2\n▁world 3\n<blk> 4\n", new UTF8Encoding(false));
        try
        {
            var t = CtcTokens.Load(path);
            Assert.Equal(4, t.BlankId);
            Assert.Equal(5, t.Count);
            // Leading SentencePiece space is stripped once, interior ones become real spaces.
            Assert.Equal("Hello, world", t.Decode([1, 2, 3]));
            Assert.Equal("", t.Decode([]));
        }
        finally { File.Delete(path); }
    }

    // ---------------------------------------------------------------- model-gated (spike)

    /// <summary>
    /// M0 exit criterion C1', and the one that actually has teeth. Compares our feature matrix
    /// element-wise against a NeMo AudioToMelSpectrogramPreprocessor reference (numpy + librosa,
    /// generated by the throwaway nemo_ref.py) for the checkpoint's own config.
    ///
    /// This exists because the transcript-level oracle was MEASURED to be blind: swapping the
    /// analysis window for the wrong one left every reference transcript byte-identical. A tensor
    /// comparison catches that in the first frame.
    /// </summary>
    [ModelFact("feats", "spike-audio")]
    public void C1Prime_FeaturesMatchNeMoReferenceTensor()
    {
        string featDir = Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_FEATS")!;

        var fe = new CtcMelFrontend();
        int checkedFiles = 0;
        double worst = 0;

        foreach (string binPath in Directory.GetFiles(featDir, "*.sym.bin").OrderBy(x => x))
        {
            string stem = Path.GetFileName(binPath).Replace(".sym.bin", "");
            string wav = Path.Combine(AudioDir!, stem + ".wav");
            if (!File.Exists(wav)) continue;

            (int mels, int refFrames, float[] refFlat) = ReadFeatureDump(binPath);
            float[] ours = fe.ComputeFeatureMajor(WavAudio.ReadMono16k(wav), out int frames);

            Assert.Equal(CtcMelFrontend.NMels, mels);
            Assert.Equal(refFrames, frames);

            double max = 0;
            for (int i = 0; i < ours.Length; i++) max = Math.Max(max, Math.Abs(ours[i] - refFlat[i]));
            worst = Math.Max(worst, max);
            checkedFiles++;
            output.WriteLine($"{stem}: {mels}x{frames}  max|delta| = {max:E3}");
        }

        Assert.True(checkedFiles > 0, "no reference feature dumps found");
        output.WriteLine($"C1': worst max|delta| across {checkedFiles} clips = {worst:E3}");
        Assert.True(worst <= 1e-3, $"feature mismatch {worst:E3} exceeds 1e-3");
    }

    private static (int Mels, int Frames, float[] Data) ReadFeatureDump(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        int mels = br.ReadInt32(), frames = br.ReadInt32();
        var data = new float[mels * frames];
        for (int i = 0; i < data.Length; i++) data[i] = br.ReadSingle();
        return (mels, frames, data);
    }

    /// <summary>
    /// M0 exit criterion C1. NOTE the assertion is NOT "string-equal on every clip", which is what
    /// the design doc originally asked for. Measured on this machine: perturbing the input WAV by
    /// +/-1 LSB (-90 dBFS, inaudible) moves SHERPA'S OWN transcript by 3.6-14.7% WER on 4 of 5
    /// clips, and by 0% on the fifth. The model's argmax path is chaotic on quiet/noisy audio, so
    /// byte-equality is not a property any independent implementation can have there — the oracle
    /// does not have it against itself. What we can and do require:
    ///   * byte-exact on at least one clip where the model IS stable, and
    ///   * WER within the model's own perturbation band everywhere else.
    /// A real front-end defect (wrong window, wrong mel scale, per-frame instead of per-utterance
    /// normalization) lands far outside this band, so the check still discriminates.
    /// </summary>
    [ModelFact("spike-model", "spike-audio")]
    public void C1_ReproducesSherpaOracleTranscript()
    {
        var tokens = CtcTokens.Load(Path.Combine(ModelDir!, "tokens.txt"));
        using var enc = new CtcEncoder(ModelOnnx(), new OnnxSessionFactory());
        var fe = new CtcMelFrontend();

        string oracleDir = Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_ORACLE")
                           ?? Path.Combine(Path.GetDirectoryName(AudioDir!.TrimEnd('\\'))!, "oracle");
        string? oursDir = Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_OURS");
        if (oursDir is { Length: > 0 }) Directory.CreateDirectory(oursDir);
        int compared = 0, exact = 0;
        double worstWer = 0;

        foreach (string wav in Directory.GetFiles(AudioDir!, "*.wav").OrderBy(x => x))
        {
            string refPath = Path.Combine(oracleDir, Path.GetFileNameWithoutExtension(wav) + ".txt");
            if (!File.Exists(refPath)) continue;

            string expected = File.ReadAllText(refPath);
            string actual = Transcribe(fe, enc, tokens, WavAudio.ReadMono16k(wav));

            if (expected.Length == 0)
            {
                // Not a comparison, a finding: sherpa's Kaldi front-end drives this checkpoint to
                // all-blank on some real 50-60 s dictations (reproduced with BOTH the int8 and fp32
                // exports), while the NeMo features the model was trained on decode the same audio
                // fine. An empty reference is not something to match.
                output.WriteLine($"ORACLE-EMPTY {Path.GetFileName(wav)} (ours: {actual.Length} chars) — skipped");
                continue;
            }
            compared++;
            if (oursDir is { Length: > 0 })
                File.WriteAllText(Path.Combine(oursDir, Path.GetFileNameWithoutExtension(wav) + ".txt"),
                                  actual, new UTF8Encoding(false));

            double wer = WordErrorRate(expected, actual);
            worstWer = Math.Max(worstWer, wer);
            if (expected == actual) { exact++; output.WriteLine($"EXACT  {Path.GetFileName(wav)}"); }
            else
            {
                output.WriteLine($"DIFF   {Path.GetFileName(wav)}  WER={wer * 100:F1}%");
                output.WriteLine($"  oracle: {expected}");
                output.WriteLine($"  ours  : {actual}");
            }
        }

        output.WriteLine($"C1: {exact}/{compared} byte-exact, worst WER {worstWer * 100:F1}%");
        Assert.True(compared > 0, "no oracle references found");
        Assert.True(exact >= 1, "no clip reproduced the oracle byte-for-byte");
        Assert.True(worstWer <= 0.15, $"worst WER {worstWer:P1} exceeds the model's own perturbation band");
    }

    /// <summary>Word-level Levenshtein / reference length.</summary>
    private static double WordErrorRate(string reference, string hypothesis)
    {
        string[] a = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] b = hypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (a.Length == 0) return b.Length == 0 ? 0 : 1;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                                  prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return (double)prev[b.Length] / a.Length;
    }

    /// <summary>
    /// M0 exit criterion C2. Id-for-id equality against Python `sentencepiece` running on the same
    /// tokenizer.model, over a 107-term list that deliberately includes multi-word terms, near-miss
    /// pairs (Jamie/Jamy, Ramaa Nathan/Ramanathan), accents, digits and CJK.
    ///
    /// Encode->decode round-tripping is NOT the criterion and would have passed a wrong encoder:
    /// a merge sequence the model never emits round-trips perfectly and yields silent zero recall.
    /// </summary>
    [ModelFact("spm", "spm-ids")]
    public void C2_TokenizerIdsMatchSentencePieceOracle()
    {
        string spModel = Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_SPM")!;
        string oracleJson = Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_SPM_IDS")!;

        var tk = CtcTokenizer.Load(spModel);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(oracleJson));

        var tokens = HaveModel ? CtcTokens.Load(Path.Combine(ModelDir!, "tokens.txt")) : null;
        int inScope = 0, agreed = 0, outOfScope = 0;
        var disagreements = new List<string>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string term = prop.Name;
            int[] expected = prop.Value.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            int[] actual = tk.Encode(term).ToArray();

            // Scope: what a vocabulary term can actually be. Single characters and terms containing
            // characters this 1024-piece English BPE has no piece for (digits, hyphen, accents, CJK)
            // are excluded and pinned separately below — they are unspottable regardless.
            bool spottable = expected.Length > 0 && !expected.Contains(0) && tokens?.Piece(0) == "<unk>";
            if (term.Trim().Length < 2 || !spottable) { outOfScope++; continue; }

            inScope++;
            if (expected.SequenceEqual(actual)) agreed++;
            else disagreements.Add($"'{term}': oracle [{string.Join(",", expected)}] ours [{string.Join(",", actual)}]");
        }

        foreach (string d in disagreements) output.WriteLine(d);
        output.WriteLine($"C2: {agreed}/{inScope} in-scope terms id-for-id identical ({outOfScope} out of scope)");
        Assert.True(inScope > 50, "oracle fixture too small to be meaningful");
        Assert.Equal(inScope, agreed);
    }

    /// <summary>
    /// The two places Microsoft.ML.Tokenizers 2.0.0 does NOT agree with SentencePiece, pinned so a
    /// package bump that changes either shows up red instead of silently altering spotter recall.
    /// Neither is reachable by a sane vocabulary term, but both fail SILENTLY (empty id list = the
    /// DP matches nothing), so VocabularyStore.SanitizeTerm must reject length-1 terms rather than
    /// trust the encoder.
    /// </summary>
    [ModelFact("spm")]
    public void C2c_KnownTokenizerDefectsArePinned()
    {
        var tk = CtcTokenizer.Load(Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_SPM")!);

        // 1. Any single-character input encodes to nothing (SentencePiece gives ["▁a"] = [3]).
        foreach (string one in new[] { "a", "I", "J", "x", " a", "a " })
            Assert.Empty(tk.Encode(one));

        // 2. A run of out-of-vocabulary characters emits one <unk> per character; SentencePiece
        //    merges the run into a single <unk>.
        Assert.Equal([966, 0, 0, 0], tk.Encode("123"));   // SentencePiece: [966, 0]

        // 3. Everything two characters or longer that IS in vocabulary agrees exactly.
        Assert.Equal([142], tk.Encode("ab"));
        Assert.Equal([298, 969], tk.Encode("Jo"));
    }

    [ModelFact("spm", "spike-model")]
    public void C2b_UnspottableTermsAreDetected()
    {
        // Consequence of C2 worth surfacing in the UI later: this 1024-piece English BPE has no
        // digits, no hyphen and no accented Latin, so those terms encode to <unk> and the DP can
        // never match them. Better to tell the user than to look broken.
        var tk = CtcTokenizer.Load(Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_SPM")!);
        var tokens = CtcTokens.Load(Path.Combine(ModelDir!, "tokens.txt"));

        foreach (string good in new[] { "Nemotron", "Parakeet", "Okta", "Claude Code", "Sriram" })
            Assert.True(tk.IsSpottable(good, tokens), good);
        foreach (string bad in new[] { "café", "Zürich", "3.14", "Wi-Fi", "" })
            Assert.False(tk.IsSpottable(bad, tokens), bad);
    }

    [ModelFact("spike-model", "spike-audio")]
    public void C3_LatencyOnCpu()
    {
        var tokens = CtcTokens.Load(Path.Combine(ModelDir!, "tokens.txt"));
        var factory = new OnnxSessionFactory();
        var swLoad = Stopwatch.StartNew();
        using var enc = new CtcEncoder(ModelOnnx(), factory);
        swLoad.Stop();
        output.WriteLine($"session load: {swLoad.ElapsedMilliseconds} ms");

        var fe = new CtcMelFrontend();
        foreach (string name in new[] { "real-10s.wav", "real-40s.wav", "real-60s.wav", "real-180s.wav" })
        {
            string wav = Path.Combine(AudioDir!, name);
            if (!File.Exists(wav)) continue;
            float[] pcm = WavAudio.ReadMono16k(wav);

            var times = new List<double>();
            double melMs = 0, encMs = 0;
            for (int i = 0; i < 11; i++)   // first run is warm-up and dropped
            {
                var sw = Stopwatch.StartNew();
                var swMel = Stopwatch.StartNew();
                float[] fm = fe.ComputeFeatureMajor(pcm, out int frames);
                swMel.Stop();
                var swEnc = Stopwatch.StartNew();
                float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);
                swEnc.Stop();
                _ = tokens.Decode(CtcGreedyDecoder.Decode(lp, outFrames, vocab, tokens.BlankId).Select(t => t.Id).ToList());
                sw.Stop();
                if (i == 0) continue;
                times.Add(sw.Elapsed.TotalMilliseconds);
                melMs = swMel.Elapsed.TotalMilliseconds;
                encMs = swEnc.Elapsed.TotalMilliseconds;
            }
            times.Sort();
            double p50 = times[times.Count / 2];
            double p95 = times[(int)Math.Ceiling(0.95 * times.Count) - 1];
            output.WriteLine($"{name}: audio={pcm.Length / 16000.0:F1}s  n={times.Count}  " +
                             $"p50={p50:F0}ms  p95={p95:F0}ms  min={times[0]:F0}ms  max={times[^1]:F0}ms  " +
                             $"(last run: mel={melMs:F0}ms enc={encMs:F0}ms)  " +
                             $"budget 800/2000 => {(p50 < 800 && p95 < 2000 ? "MET" : "MISSED")}");
        }
    }

    [ModelFact("spike-model")]
    public void C4_WorkingSetDeltaForResidentSession()
    {
        var p = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); p.Refresh();
        long before = p.WorkingSet64;

        var enc = new CtcEncoder(ModelOnnx(), new OnnxSessionFactory());
        // Touch it once: ORT is lazy, so weights are not fully resident until the first Run.
        var fe = new CtcMelFrontend();
        float[] fm = fe.ComputeFeatureMajor(Chirp(16_000 * 5), out int frames);
        enc.Run(fm, frames, out _, out _);

        p.Refresh();
        long afterLoad = p.WorkingSet64;

        enc.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(500); p.Refresh();
        long afterDispose = p.WorkingSet64;

        output.WriteLine($"working set: before={before / 1048576.0:F1} MB  " +
                         $"loaded={afterLoad / 1048576.0:F1} MB (+{(afterLoad - before) / 1048576.0:F1})  " +
                         $"disposed={afterDispose / 1048576.0:F1} MB (returned {(afterLoad - afterDispose) / 1048576.0:F1})");
    }

    [ModelFact("spike-model", "spike-audio:real-40s.wav")]
    public void FrameDuration_DerivedFromAudioMatchesEightyMilliseconds()
    {
        // Detection placement multiplies frame index by this; hardcoding it wrong puts every
        // correction on the wrong word (design doc §4 item 3).
        string wav = Path.Combine(AudioDir!, "real-40s.wav");

        using var enc = new CtcEncoder(ModelOnnx(), new OnnxSessionFactory());
        float[] pcm = WavAudio.ReadMono16k(wav);
        float[] fm = new CtcMelFrontend().ComputeFeatureMajor(pcm, out int frames);
        enc.Run(fm, frames, out int outFrames, out _);

        double seconds = pcm.Length / 16000.0;
        double perFrame = seconds / outFrames;
        output.WriteLine($"audio={seconds:F2}s outFrames={outFrames} => {perFrame * 1000:F2} ms/frame");
        Assert.InRange(perFrame, 0.0795, 0.0805);
        Assert.Equal(0.08, CtcEncoder.FrameSeconds, 6);
    }

    // ---------------------------------------------------------------- helpers

    private static string Transcribe(CtcMelFrontend fe, CtcEncoder enc, CtcTokens tokens, float[] pcm)
    {
        float[] fm = fe.ComputeFeatureMajor(pcm, out int frames);
        float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);
        return tokens.Decode(CtcGreedyDecoder.Decode(lp, outFrames, vocab, tokens.BlankId)
                                             .Select(t => t.Id).ToList());
    }

    /// <summary>
    /// Deterministic broadband sweep. The noise floor is not decoration: without it the lowest mel
    /// bins carry no energy, hit the FLT_EPSILON log floor, and stop behaving like a linear system.
    /// </summary>
    private static float[] Chirp(int n)
    {
        var s = new float[n];
        var rng = new Random(1234);
        for (int i = 0; i < n; i++)
        {
            double t = i / 16000.0;
            s[i] = (float)(0.4 * Math.Sin(2 * Math.PI * (200 + 3000 * t) * t)
                         + 0.1 * Math.Sin(2 * Math.PI * 5500 * t)
                         + 0.02 * (rng.NextDouble() * 2 - 1));
        }
        return s;
    }

    private static float[] OneHot(int[] argmax, int vocab)
    {
        var p = new float[argmax.Length * vocab];
        for (int t = 0; t < argmax.Length; t++)
            for (int v = 0; v < vocab; v++)
                p[t * vocab + v] = v == argmax[t] ? -0.1f : -9f;
        return p;
    }
}
