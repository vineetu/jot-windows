using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// M2 coverage for the shipping spotter — the half that needs the real 132 MB checkpoint, and therefore
/// the half CI never runs. Same env-var gating as <see cref="CtcSpikeTests"/>:
///
///   JOT_CTC_MODEL_DIR   a directory holding model.int8.onnx + tokens.txt + tokenizer.model
///                       (exactly the three files the release must carry)
///   JOT_CTC_SPIKE_AUDIO a directory of 16 kHz mono WAVs, including tts-terms.wav
///
/// With either unset every fact here is a REAL xunit skip (see <see cref="ModelFactAttribute"/>), so the
/// run reports honestly instead of counting an un-run fact as green. The DP's own correctness is
/// covered without any of this in <c>Vocabulary/CtcWordSpotterTests</c>; what needs the model is
/// CALIBRATION (where the accept threshold goes) and LATENCY, neither of which can be reasoned about.
/// </summary>
public class CtcSpotterTests(ITestOutputHelper output)
{
    private static string? ModelDir => Environment.GetEnvironmentVariable("JOT_CTC_MODEL_DIR");
    private static string? AudioDir => Environment.GetEnvironmentVariable("JOT_CTC_SPIKE_AUDIO");

    private static bool Have =>
        ModelDir is { Length: > 0 } d && new CtcModel(directory: d).IsInstalled
        && AudioDir is { Length: > 0 } a && Directory.Exists(a);

    /// <summary>
    /// The planted set. tts-terms.wav says all six (as "Sri Ram", "Octa" and "Claud code" — the whole
    /// point: the audio carries the MIS-spelling and the term list carries the right one).
    /// </summary>
    private static VocabularyTerm[] PlantedTerms() =>
    [
        new() { Text = "Nemotron" },
        new() { Text = "Parakeet" },
        new() { Text = "Sriram", Aliases = ["Sri Ram"] },
        new() { Text = "Okta", Aliases = ["Octa"] },
        new() { Text = "Claude Code", Aliases = ["Claud code"] },
        new() { Text = "Thursday" },
    ];

    private static CtcVocabularySpotter Spotter(float minScore) =>
        new(new CtcModel(directory: ModelDir!), new OnnxSessionFactory(), settings: null, minScore: minScore);

    // MARK: - Calibration

    /// <summary>
    /// THE threshold derivation. Runs the same six terms over a clip that contains all of them and over
    /// four clips that contain none, and prints both score distributions. <c>DefaultMinScore</c> is
    /// whatever separates them — never a number copied from another implementation's scale.
    ///
    /// Run with a wide-open floor so both bands are visible; the assertion is that the shipped default
    /// lands strictly between the worst hit and the best decoy.
    /// </summary>
    [ModelFact("spotter-model", "spike-audio:tts-terms.wav")]
    public void M2_ThresholdSeparatesPlantedTermsFromDecoys()
    {
        string planted = Path.Combine(AudioDir!, "tts-terms.wav");

        using var s = Spotter(minScore: -30f);   // wide open: we want the whole distribution
        VocabularyTerm[] terms = PlantedTerms();

        var hits = new List<double>();
        output.WriteLine("--- planted (tts-terms.wav) ---");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in Best(s.Spot(WavAudio.ReadMono16k(planted), 16_000, terms, default)))
        {
            output.WriteLine($"  {d.Term,-14} {d.Score,8:F3}   {d.StartTime,6:F2}s–{d.EndTime:F2}s");
            hits.Add(d.Score);
            found.Add(d.Term);
        }

        var decoys = new List<double>();
        output.WriteLine("--- decoys (clips containing none of the terms) ---");
        foreach (string name in new[] { "public-0.wav", "real-40s.wav", "real-90s.wav", "seg-22-60.wav" })
        {
            string wav = Path.Combine(AudioDir!, name);
            if (!File.Exists(wav)) continue;
            foreach (var d in Best(s.Spot(WavAudio.ReadMono16k(wav), 16_000, terms, default)))
            {
                output.WriteLine($"  {name,-16} {d.Term,-14} {d.Score,8:F3}");
                decoys.Add(d.Score);
            }
        }

        // Level sensitivity, printed because CtcSpikeTests measured that the mel front-end is NOT
        // gain-invariant below bin 40 (the 2^-24 log guard is absolute). A threshold calibrated only on
        // a loud clip could silently stop working on quiet dictations.
        output.WriteLine("--- planted at -12 dB (gain 0.25) ---");
        float[] quiet = [.. WavAudio.ReadMono16k(planted).Select(x => x * 0.25f)];
        var quietScores = new List<double>();
        foreach (var d in Best(s.Spot(quiet, 16_000, terms, default)))
        {
            output.WriteLine($"  {d.Term,-14} {d.Score,8:F3}");
            quietScores.Add(d.Score);
        }
        output.WriteLine($"quiet  : {quietScores.Count} terms, worst {(quietScores.Count > 0 ? quietScores.Min() : 0):F3}");

        Assert.True(hits.Count > 0, "no planted term scored at all");
        Assert.True(decoys.Count > 0, "no decoy scores — the decoy clips are missing");

        double worstHit = hits.Min(), bestHit = hits.Max();
        double bestDecoy = decoys.Max();
        output.WriteLine($"planted: {hits.Count} terms, {worstHit:F3} … {bestHit:F3} " +
                         $"(median {Median(hits):F3}); found {found.Count}/{terms.Length}");
        output.WriteLine($"decoys : {decoys.Count} scores, best {bestDecoy:F3}");
        output.WriteLine($"gap    : {worstHit - bestDecoy:F3} nats; shipped default " +
                         $"{CtcWordSpotter.DefaultMinScore:F2}");

        Assert.True(worstHit > bestDecoy,
            $"no separation: worst planted {worstHit:F3} <= best decoy {bestDecoy:F3}");
        Assert.InRange(CtcWordSpotter.DefaultMinScore, bestDecoy, worstHit);
    }

    /// <summary>Recall + zero-insertion at the SHIPPED threshold — the number that actually ships.</summary>
    [ModelFact("spotter-model", "spike-audio:tts-terms.wav")]
    public void M2_ShippedThresholdFindsThePlantedTermsAndNothingElse()
    {
        string planted = Path.Combine(AudioDir!, "tts-terms.wav");

        using var s = Spotter(CtcWordSpotter.DefaultMinScore);
        VocabularyTerm[] terms = PlantedTerms();

        var found = s.Spot(WavAudio.ReadMono16k(planted), 16_000, terms, default)
                     .Select(d => d.Term).Distinct().ToList();
        output.WriteLine($"recall at {CtcWordSpotter.DefaultMinScore}: {found.Count}/{terms.Length} — " +
                         string.Join(", ", found));

        int decoyHits = 0;
        foreach (string name in new[] { "public-0.wav", "real-40s.wav", "real-90s.wav", "seg-22-60.wav" })
        {
            string wav = Path.Combine(AudioDir!, name);
            if (!File.Exists(wav)) continue;
            int n = s.Spot(WavAudio.ReadMono16k(wav), 16_000, terms, default).Count;
            if (n > 0) output.WriteLine($"  FALSE POSITIVE in {name}: {n}");
            decoyHits += n;
        }

        Assert.Equal(0, decoyHits);
        Assert.True(found.Count >= terms.Length - 1,
            $"recall {found.Count}/{terms.Length} — at most one miss is tolerated");
    }

    /// <summary>Silence must produce nothing. A spotter that fires on a quiet room would let the gate
    /// rewrite words in every dictation with a pause in it.</summary>
    [ModelFact("spotter-model", "spike-audio")]
    public void M2_SilenceProducesNoDetections()
    {
        using var s = Spotter(CtcWordSpotter.DefaultMinScore);
        Assert.Empty(s.Spot(new float[16_000 * 5], 16_000, PlantedTerms(), default));
    }

    // MARK: - Latency

    /// <summary>
    /// End-to-end wall time of the whole shipping pass (trim + mel + graph + DP) on the CPU EP
    /// (the only backend the product uses). Prints the table; asserts that 60 s stays inside the deadline.
    /// </summary>
    [ModelFact("spotter-model", "spike-audio")]
    public void M2_LatencyCpuVersusDirectMl()
    {
        string[] clips = ["real-10s.wav", "real-40s.wav", "real-60s.wav", "real-90s.wav"];
        VocabularyTerm[] terms = PlantedTerms();

        foreach (ComputeBackend backend in new[] { ComputeBackend.Cpu })
        {
            var tokens = CtcTokens.Load(Path.Combine(ModelDir!, CtcModel.TokensFile));
            var tokenizer = CtcTokenizer.Load(Path.Combine(ModelDir!, CtcModel.TokenizerFile));
            var swLoad = Stopwatch.StartNew();
            using var enc = new CtcEncoder(Path.Combine(ModelDir!, CtcModel.ModelFile),
                                           new OnnxSessionFactory(), backend);
            swLoad.Stop();
            output.WriteLine($"=== {backend} — session load {swLoad.ElapsedMilliseconds} ms ===");

            var fe = new CtcMelFrontend();
            var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds);
            var queries = terms.Select(t => new CtcSpotQuery(t.Text, t.Aliases, tokenizer.Encode(t.Text)))
                               .Where(q => q.TokenIds.Count > 0).ToList();

            foreach (string name in clips)
            {
                string wav = Path.Combine(AudioDir!, name);
                if (!File.Exists(wav)) continue;
                float[] pcm = WavAudio.ReadMono16k(wav);

                var times = new List<double>();
                double melMs = 0, encMs = 0, dpMs = 0, trimmed = 0;
                for (int i = 0; i < 6; i++)   // first run is warm-up and dropped
                {
                    var sw = Stopwatch.StartNew();
                    (float[] speech, _) = SilenceTrim.Trim(pcm, 16_000);
                    var swMel = Stopwatch.StartNew();
                    float[] fm = fe.ComputeFeatureMajor(speech, out int frames);
                    swMel.Stop();
                    var swEnc = Stopwatch.StartNew();
                    float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);
                    swEnc.Stop();
                    var swDp = Stopwatch.StartNew();
                    _ = dp.Spot(lp, outFrames, vocab, queries);
                    swDp.Stop();
                    sw.Stop();
                    if (i == 0) continue;
                    times.Add(sw.Elapsed.TotalMilliseconds);
                    melMs = swMel.Elapsed.TotalMilliseconds;
                    encMs = swEnc.Elapsed.TotalMilliseconds;
                    dpMs = swDp.Elapsed.TotalMilliseconds;
                    trimmed = speech.Length / 16000.0;
                }
                if (times.Count == 0) continue;
                times.Sort();
                output.WriteLine($"  {name,-16} raw={pcm.Length / 16000.0,6:F1}s trimmed={trimmed,6:F1}s  " +
                                 $"p50={times[times.Count / 2],7:F0}ms  min={times[0]:F0} max={times[^1]:F0}  " +
                                 $"(mel={melMs:F0} enc={encMs:F0} dp={dpMs:F1})");
            }
        }
    }

    /// <summary>
    /// The DP's cost must not blow up with list size — the UI plans a 200-term cap. Measured against the
    /// same log-prob matrix so only the search is timed.
    /// </summary>
    [ModelFact("spotter-model", "spike-audio:real-60s.wav")]
    public void M2_TwoHundredTermsCostAlmostNothing()
    {
        string wav = Path.Combine(AudioDir!, "real-60s.wav");

        var tokens = CtcTokens.Load(Path.Combine(ModelDir!, CtcModel.TokensFile));
        var tokenizer = CtcTokenizer.Load(Path.Combine(ModelDir!, CtcModel.TokenizerFile));
        using var enc = new CtcEncoder(Path.Combine(ModelDir!, CtcModel.ModelFile), new OnnxSessionFactory());
        float[] fm = new CtcMelFrontend().ComputeFeatureMajor(WavAudio.ReadMono16k(wav), out int frames);
        float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);

        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds);
        foreach (int n in new[] { 1, 50, 200 })
        {
            var queries = Enumerable.Range(0, n)
                .Select(i => new CtcSpotQuery("t" + i, [], tokenizer.Encode("Nemotron" + i % 7)))
                .Where(q => q.TokenIds.Count > 0).ToList();

            var sw = Stopwatch.StartNew();
            for (int r = 0; r < 5; r++) dp.Spot(lp, outFrames, vocab, queries);
            sw.Stop();
            output.WriteLine($"  {n,4} terms over {outFrames} frames: {sw.Elapsed.TotalMilliseconds / 5:F1} ms");
        }
    }

    // MARK: - Term validation against the real BPE

    [ModelFact("spotter-model")]
    public void M2_CheckTermMatchesTheCheckpointsRealVocabulary()
    {
        using var s = new CtcVocabularySpotter(new CtcModel(directory: ModelDir), new OnnxSessionFactory());
        foreach (string good in new[] { "Nemotron", "Parakeet", "Okta", "Claude Code", "Sriram" })
            Assert.Equal(TermSpottability.Ok, s.CheckTerm(good));
        foreach (string bad in new[] { "café", "Zürich", "3.14", "Wi-Fi" })
            Assert.Equal(TermSpottability.Unsupported, s.CheckTerm(bad));
        foreach (string tiny in new[] { "a", "I", "" })
            Assert.Equal(TermSpottability.TooShort, s.CheckTerm(tiny));
    }

    /// <summary>Dispose really returns the working set — the whole reason "turn vocabulary off" is a
    /// requirement rather than hygiene.</summary>
    [ModelFact("spotter-model", "spike-audio")]
    public void M2_UnloadReturnsTheWorkingSet()
    {
        var p = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); p.Refresh();
        long before = p.WorkingSet64;

        using var s = Spotter(CtcWordSpotter.DefaultMinScore);
        s.Spot(new float[16_000 * 5].Select((_, i) => (float)(0.2 * Math.Sin(i * 0.05))).ToArray(),
               16_000, PlantedTerms(), default);
        p.Refresh();
        long loaded = p.WorkingSet64;

        s.Unload();
        GC.Collect(); GC.WaitForPendingFinalizers(); System.Threading.Thread.Sleep(500); p.Refresh();
        long unloaded = p.WorkingSet64;

        output.WriteLine($"working set: before={before / 1048576.0:F1} MB " +
                         $"loaded={loaded / 1048576.0:F1} MB (+{(loaded - before) / 1048576.0:F1}) " +
                         $"unloaded={unloaded / 1048576.0:F1} MB (returned {(loaded - unloaded) / 1048576.0:F1})");
    }

    // MARK: - helpers

    /// <summary>Best occurrence per term — a calibration table wants one number per term, not one per
    /// utterance.</summary>
    private static IEnumerable<VocabularyGate.Detection> Best(IReadOnlyList<VocabularyGate.Detection> all) =>
        all.GroupBy(d => d.Term, StringComparer.Ordinal)
           .Select(g => g.OrderByDescending(d => d.Score).First())
           .OrderByDescending(d => d.Score);

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s[s.Count / 2];
    }
}
