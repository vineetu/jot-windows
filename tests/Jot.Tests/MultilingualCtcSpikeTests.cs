using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests;

/// <summary>
/// SPIKE — does the "Tier A is a drop-in" claim in
/// <c>docs/plans/vocabulary-multilingual-research.md</c> survive contact with a real non-English
/// checkpoint? Nothing here ships. It exists to produce the numbers in that document's "Measured"
/// section and to fail loudly if a future change breaks the contract it recorded.
///
/// Two checkpoints, both <c>nvidia/stt_XX_fastconformer_hybrid_large_pc</c> exported to int8 CTC ONNX
/// by sherpa-onnx and re-hosted on Hugging Face:
///   * <b>de</b> — CC-BY-4.0 upstream, 1024-piece BPE. The shippable one.
///   * <b>pt</b> — <b>CC-BY-NC-4.0 upstream</b>, 128-piece near-character vocabulary. Measured here
///     for the architecture question ONLY; it is licence-disqualified and must never be shipped.
///     See the Measured section, finding #1.
///
/// EVERY fact is a real xunit skip without the assets (<see cref="ModelFactAttribute"/>). CI never
/// downloads 132 MB, and green never means "did not run".
/// </summary>
public class MultilingualCtcSpikeTests(ITestOutputHelper output)
{
    private static string Root => ModelGate.MlSpikeRoot;

    private static string Dir(string lang) => Path.Combine(Root, lang);
    private static string Graph(string lang) => Path.Combine(Dir(lang), CtcModel.ModelFile);
    private static string TokensPath(string lang) => Path.Combine(Dir(lang), CtcModel.TokensFile);
    private static string SpmPath(string lang) => Path.Combine(Dir(lang), CtcModel.TokenizerFile);
    private static string AudioDir(string lang) => Path.Combine(Root, "audio-" + lang);

    // ------------------------------------------------------------------ E1: the graph contract

    /// <summary>
    /// E1. The claim: these exports match the graph contract <see cref="CtcEncoder"/> hard-codes.
    /// Asserted element by element rather than eyeballed, because "same recipe" is exactly the kind
    /// of claim that is true for seven properties and false for the eighth.
    /// </summary>
    [ModelTheory("ml:de")]
    [InlineData("de", 1024)]
    public void E1_GraphContractMatchesTheEnglishExport(string lang, int expectedVocab) =>
        AssertGraphContract(lang, expectedVocab);

    [ModelTheory("ml:pt")]
    [InlineData("pt", 128)]
    public void E1_GraphContractMatchesTheEnglishExport_Pt(string lang, int expectedVocab) =>
        AssertGraphContract(lang, expectedVocab);

    private void AssertGraphContract(string lang, int expectedVocab)
    {
        using var session = new OnnxSessionFactory().Create(Graph(lang), ComputeBackend.Cpu);

        var meta = session.ModelMetadata.CustomMetadataMap;
        foreach ((string k, string v) in meta.OrderBy(p => p.Key))
            output.WriteLine($"  {k} = {(v.Length > 90 ? v[..90] + "…" : v)}");

        // The three keys the port depends on. normalize_type is the one that silently ruins every
        // feature if it ever says anything else — our front-end has no other mode.
        Assert.Equal("per_feature", meta["normalize_type"]);
        Assert.Equal(CtcEncoder.SubsamplingFactor.ToString(CultureInfo.InvariantCulture),
                     meta["subsampling_factor"]);
        Assert.Equal(expectedVocab.ToString(CultureInfo.InvariantCulture), meta["vocab_size"]);
        Assert.Equal("EncDecHybridRNNTCTCBPEModel", meta["model_type"]);

        string[] inputs = [.. session.InputMetadata.Keys];
        string[] outputs = [.. session.OutputMetadata.Keys];
        Assert.Equal(["audio_signal", "length"], inputs);
        Assert.Equal(["logprobs"], outputs);
        Assert.Equal(typeof(float), session.InputMetadata["audio_signal"].ElementType);
        Assert.Equal(typeof(long), session.InputMetadata["length"].ElementType);

        // [batch, 80, frames] in; [batch, frames/8, vocab+1] out. The 80 is a FIXED axis in the
        // graph, so a checkpoint with a different mel count would fail here rather than at runtime.
        int[] inDims = session.InputMetadata["audio_signal"].Dimensions;
        int[] outDims = session.OutputMetadata["logprobs"].Dimensions;
        output.WriteLine($"  audio_signal dims [{string.Join(",", inDims)}]  " +
                         $"logprobs dims [{string.Join(",", outDims)}]");
        Assert.Equal(3, inDims.Length);
        Assert.Equal(CtcMelFrontend.NMels, inDims[1]);
        Assert.Equal(3, outDims.Length);
        Assert.Equal(expectedVocab + 1, outDims[2]);   // +1 for the CTC blank

        // tokens.txt must describe the SAME id space, and the blank must be read, not assumed.
        var tokens = CtcTokens.Load(TokensPath(lang));
        Assert.Equal(expectedVocab + 1, tokens.Count);
        Assert.Equal(expectedVocab, tokens.BlankId);
        output.WriteLine($"  tokens.txt: {tokens.Count} entries, blank {tokens.BlankId}");
    }

    // ------------------------------------------------------------------ E3: feature parity

    /// <summary>
    /// E3 / C1'. Our <see cref="CtcMelFrontend"/>, UNCHANGED, against a NeMo
    /// <c>AudioToMelSpectrogramPreprocessor</c> reference computed from the checkpoint's own
    /// <c>model_config.yaml</c>. The English bar recorded in <c>vocabulary-ctc-port.md</c> is
    /// max |Δ| ≤ 9.34e-4; the same metric is reported here so the two are directly comparable.
    ///
    /// This is the fact with teeth. A transcript-level comparison was MEASURED to be blind to a
    /// wrong analysis window; a tensor comparison catches it in the first frame.
    /// </summary>
    [ModelTheory("ml-feats:de")]
    [InlineData("de")]
    public void E3_FeaturesMatchNeMoReference_De(string lang) => AssertFeatureParity(lang);

    [ModelTheory("ml-feats:pt")]
    [InlineData("pt")]
    public void E3_FeaturesMatchNeMoReference_Pt(string lang) => AssertFeatureParity(lang);

    private void AssertFeatureParity(string lang)
    {
        string featDir = Path.Combine(Root, "feats-" + lang);
        var fe = new CtcMelFrontend();
        double worst = 0;
        int n = 0;
        string worstClip = "";

        foreach (string bin in Directory.GetFiles(featDir, "*.bin").OrderBy(x => x))
        {
            string wav = Path.Combine(AudioDir(lang), Path.GetFileNameWithoutExtension(bin) + ".wav");
            if (!File.Exists(wav)) continue;

            (int mels, int refFrames, float[] refFlat) = ReadFeatureDump(bin);
            float[] ours = fe.ComputeFeatureMajor(WavAudio.ReadMono16k(wav), out int frames);

            Assert.Equal(CtcMelFrontend.NMels, mels);
            Assert.Equal(refFrames, frames);

            double max = 0;
            for (int i = 0; i < ours.Length; i++) max = Math.Max(max, Math.Abs(ours[i] - refFlat[i]));
            if (max > worst) { worst = max; worstClip = Path.GetFileName(wav); }
            n++;
        }

        Assert.True(n > 0, "no reference feature dumps found");
        output.WriteLine($"E3 [{lang}]: {n} clips, worst max|delta| = {worst:E3} (on {worstClip}); " +
                         $"English bar was 9.34e-4");
        Assert.True(worst <= 1e-3, $"feature mismatch {worst:E3} exceeds the English bar band");
    }

    private static (int Mels, int Frames, float[] Data) ReadFeatureDump(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        int mels = br.ReadInt32(), frames = br.ReadInt32();
        var data = new float[mels * frames];
        for (int i = 0; i < data.Length; i++) data[i] = br.ReadSingle();
        return (mels, frames, data);
    }

    // ------------------------------------------------------------------ decode sanity

    /// <summary>
    /// Does OUR front-end, driving THEIR graph, produce sensible text in that language? Reported as
    /// WER against the corpus's own reference transcript. Not a WER benchmark — the reference is
    /// cased and punctuated while the corpus normalisation is not perfectly aligned — but a WER in
    /// the tens-of-percent band is "the model is transcribing German", and a WER near 100 % is
    /// "the front-end is wrong", and those are the only two answers this needs to tell apart.
    /// </summary>
    [ModelTheory("ml:de", "ml-audio:de", "ml-spm-oracle:de")]
    [InlineData("de")]
    public void Decode_ProducesSensibleTextInTheTargetLanguage_De(string lang) => AssertDecodes(lang);

    [ModelTheory("ml:pt", "ml-audio:pt", "ml-spm-oracle:pt")]
    [InlineData("pt")]
    public void Decode_ProducesSensibleTextInTheTargetLanguage_Pt(string lang) => AssertDecodes(lang);

    private void AssertDecodes(string lang)
    {
        Corpus corpus = Corpus.Load(AudioDir(lang));
        var tokens = CtcTokens.Load(TokensPath(lang));
        using var enc = new CtcEncoder(Graph(lang), new OnnxSessionFactory());
        var fe = new CtcMelFrontend();

        var wers = new List<double>();
        int shown = 0;
        foreach (Clip c in corpus.Clips.Take(30))
        {
            float[] pcm = WavAudio.ReadMono16k(Path.Combine(AudioDir(lang), c.File));
            float[] fm = fe.ComputeFeatureMajor(pcm, out int frames);
            float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);
            string hyp = tokens.Decode([.. CtcGreedyDecoder.Decode(lp, outFrames, vocab, tokens.BlankId)
                                                          .Select(t => t.Id)]);
            double wer = WordErrorRate(Normalize(c.Raw), Normalize(hyp));
            wers.Add(wer);
            if (shown++ < 6)
            {
                output.WriteLine($"  ref: {c.Raw}");
                output.WriteLine($"  hyp: {hyp}   (WER {wer * 100:F1}%)");
            }
        }

        wers.Sort();
        double median = wers[wers.Count / 2];
        double mean = wers.Average();
        output.WriteLine($"DECODE [{lang}]: {wers.Count} clips, median WER {median * 100:F1}%, " +
                         $"mean {mean * 100:F1}%, best {wers[0] * 100:F1}%, worst {wers[^1] * 100:F1}%");
        Assert.True(median < 0.50, $"median WER {median:P1} — our front-end is not driving this graph");
    }

    // ------------------------------------------------------------------ E2 + M2: the spotter

    /// <summary>
    /// The headline experiment. Runs the SHIPPING <see cref="CtcWordSpotter"/> DP, unmodified, over a
    /// non-English checkpoint's log-probs, and reports the two distributions the English calibration
    /// reported: planted-term scores and decoy scores.
    ///
    /// Recall is measured against REAL HUMAN SPEECH — Common Voice 17 (CC0) and FLEURS (CC-BY-4.0),
    /// one clip per distinct speaker — because TTS gives one canonical pronunciation per term and so
    /// cannot establish recall at all.
    ///
    /// Both casings are scored for every term. The <c>_pc</c> checkpoints emit capitalisation, so a
    /// term fed in the wrong case searches for a sequence the model never emits.
    /// </summary>
    [ModelTheory("ml:de", "ml-audio:de", "ml-spm-oracle:de")]
    [InlineData("de")]
    public void M2_SpotterRecallAndDecoySeparation_De(string lang) => AssertSpotter(lang);

    [ModelTheory("ml:pt", "ml-audio:pt", "ml-spm-oracle:pt")]
    [InlineData("pt")]
    public void M2_SpotterRecallAndDecoySeparation_Pt(string lang) => AssertSpotter(lang);

    private void AssertSpotter(string lang)
    {
        Corpus corpus = Corpus.Load(AudioDir(lang));
        var tokens = CtcTokens.Load(TokensPath(lang));
        using var enc = new CtcEncoder(Graph(lang), new OnnxSessionFactory());
        var fe = new CtcMelFrontend();

        // minScore = -inf: we want the SCORE of every term on every clip, not the accept/reject that
        // -3.0 would already have made. The threshold question is answered from the distributions.
        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds,
                                    minScore: float.NegativeInfinity, maxOccurrencesPerTerm: 8);

        // Term -> ids comes from the PYTHON sentencepiece oracle, not from CtcTokenizer, because
        // CtcTokenizer cannot load the German model at all (see
        // Tokenizer_MicrosoftMLTokenizersCannotLoadTheGermanUnigramModel). Substituting the oracle
        // isolates the DP question from the library defect — but note that this substitution is
        // itself part of the cost: shipping German needs a Unigram encoder we do not have.
        var ids = ProbeRows(lang).ToDictionary(r => r.Term, r => r, StringComparer.Ordinal);

        // Two query sets: the user's typed casing, and the same term lowercased.
        var cased = new List<CtcSpotQuery>();
        var lower = new List<CtcSpotQuery>();
        var unspottable = new List<string>();
        foreach (string term in corpus.Terms)
        {
            if (!ids.TryGetValue(term, out ProbeRow r) || r.Unk) { unspottable.Add(term); continue; }
            cased.Add(new CtcSpotQuery(term, [], r.Cased));
            lower.Add(new CtcSpotQuery(term, [], r.Lower));
        }
        output.WriteLine($"SPOTTER [{lang}]: {cased.Count} spottable terms" +
                         (unspottable.Count > 0 ? $", UNSPOTTABLE: {string.Join(", ", unspottable)}" : ""));

        var plantedCased = new List<double>();
        var plantedLower = new List<double>();
        var decoyCased = new List<double>();
        var missed = new List<string>();
        var perTerm = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        foreach (Clip c in corpus.Clips)
        {
            float[] pcm = WavAudio.ReadMono16k(Path.Combine(AudioDir(lang), c.File));
            float[] fm = fe.ComputeFeatureMajor(pcm, out int frames);
            float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);

            Dictionary<string, double> best = BestPerTerm(dp.Spot(lp, outFrames, vocab, cased));
            Dictionary<string, double> bestLower = BestPerTerm(dp.Spot(lp, outFrames, vocab, lower));

            if (c.Kind == "planted")
            {
                if (!best.TryGetValue(c.Term!, out double s))
                {
                    missed.Add($"{c.Term} in {c.File} (no alignment at all)");
                    continue;
                }
                plantedCased.Add(s);
                plantedLower.Add(bestLower.TryGetValue(c.Term!, out double sl) ? sl : double.NegativeInfinity);
                if (!perTerm.TryGetValue(c.Term!, out List<double>? l)) perTerm[c.Term!] = l = [];
                l.Add(s);
            }
            else
            {
                // Every term against a clip containing none of them: this is the false-positive band.
                foreach (double s in best.Values) decoyCased.Add(s);
            }
        }

        Report("planted (typed casing)", plantedCased);
        Report("planted (lowercased) ", plantedLower);
        Report("decoy   (typed casing)", decoyCased);

        output.WriteLine("  per-term planted scores (typed casing):");
        foreach ((string term, List<double> scores) in perTerm.OrderBy(p => p.Key))
        {
            scores.Sort();
            output.WriteLine($"    {term,-22} n={scores.Count,2}  " +
                             string.Join(" ", scores.Select(s => s.ToString("F2", CultureInfo.InvariantCulture))));
        }
        foreach (string m in missed) output.WriteLine($"  MISSED {m}");

        const float En = CtcWordSpotter.DefaultMinScore;
        int recallAt = plantedCased.Count(s => s >= En);
        int fpAt = decoyCased.Count(s => s >= En);
        double sep = plantedCased.Count > 0 && decoyCased.Count > 0
            ? plantedCased.Min() - decoyCased.Max() : double.NaN;

        output.WriteLine($"  at the ENGLISH threshold {En}: recall {recallAt}/{plantedCased.Count} " +
                         $"({(plantedCased.Count == 0 ? 0 : 100.0 * recallAt / plantedCased.Count):F1}%), " +
                         $"false positives {fpAt}/{decoyCased.Count} " +
                         $"({(decoyCased.Count == 0 ? 0 : 100.0 * fpAt / decoyCased.Count):F2}%)");
        output.WriteLine($"  band separation (worst planted - best decoy) = {sep:F2} nats " +
                         $"[English measured +4.35]. A NEGATIVE number means the two distributions " +
                         "overlap and no threshold separates them cleanly.");
        output.WriteLine($"  lowercased recall at {En}: {plantedLower.Count(s => s >= En)}/{plantedLower.Count}" +
                         " — the cost of feeding the DP the wrong casing.");

        // The calibration table. -3.0 is one row of it; the point is to show what the language would
        // need if -3.0 does not hold, rather than to assert that it does.
        output.WriteLine("  threshold sweep:  thr   recall        false positives");
        foreach (double thr in new[] { -1.0, -2.0, -3.0, -4.0, -5.0, -6.0, -7.5, -10.0 })
        {
            int r = plantedCased.Count(s => s >= thr);
            int f = decoyCased.Count(s => s >= thr);
            output.WriteLine($"                  {thr,5:F1}   {r,3}/{plantedCased.Count} " +
                             $"({100.0 * r / plantedCased.Count,5:F1}%)   {f,4}/{decoyCased.Count} " +
                             $"({100.0 * f / decoyCased.Count,5:F2}%)");
        }

        Assert.True(plantedCased.Count > 20, "planted set too small to say anything");
        Assert.True(decoyCased.Count > 100, "decoy set too small to say anything");
    }

    private static Dictionary<string, double> BestPerTerm(IReadOnlyList<VocabularyGate.Detection> hits)
    {
        var best = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (VocabularyGate.Detection d in hits)
            if (!best.TryGetValue(d.Term, out double cur) || d.Score > cur) best[d.Term] = d.Score;
        return best;
    }

    private void Report(string label, List<double> scores)
    {
        if (scores.Count == 0) { output.WriteLine($"  {label}: none"); return; }
        var s = scores.Where(x => !double.IsNegativeInfinity(x)).OrderBy(x => x).ToList();
        int nInf = scores.Count - s.Count;
        if (s.Count == 0) { output.WriteLine($"  {label}: all {nInf} unmatched"); return; }
        output.WriteLine($"  {label}: n={scores.Count}  min={s[0]:F2}  p10={s[s.Count / 10]:F2}  " +
                         $"median={s[s.Count / 2]:F2}  max={s[^1]:F2}" +
                         (nInf > 0 ? $"  ({nInf} with no alignment)" : ""));
    }

    // ------------------------------------------------------------------ tokenizer

    /// <summary>
    /// FINDING #2, pinned. <see cref="CtcTokenizer"/> is backed by Microsoft.ML.Tokenizers 2.0.0,
    /// and it <b>cannot load the German checkpoint's tokenizer at all</b>:
    /// <c>SentencePieceUnigramModel..ctor</c> throws <see cref="IndexOutOfRangeException"/>.
    ///
    /// Root cause, read off the model protos rather than guessed:
    ///   * our English <c>parakeet-tdt_ctc-110m</c> tokenizer is <b>model_type = BPE</b>;
    ///   * <c>stt_de_fastconformer_hybrid_large_pc</c>'s is <b>model_type = UNIGRAM</b>;
    ///   * both declare <c>bos_id = eos_id = pad_id = -1</c>, and the library's BPE path tolerates
    ///     that while its Unigram path indexes the piece array with those -1s unguarded.
    /// Portuguese is BPE (128 pieces) and loads fine — so this is a per-language property, not a
    /// per-family one, and "the family is a drop-in" is false for exactly the languages that use
    /// Unigram. Every Tier-A language has to be checked individually.
    ///
    /// Asserted as a THROW so a package bump that fixes it turns this red and we notice.
    /// </summary>
    [ModelFact("ml:de")]
    public void Tokenizer_MicrosoftMLTokenizersCannotLoadTheGermanUnigramModel()
    {
        Exception ex = Assert.ThrowsAny<Exception>(() => CtcTokenizer.Load(SpmPath("de")));
        output.WriteLine($"CtcTokenizer.Load(de) -> {ex.GetType().Name}: {ex.Message}");
        Assert.IsType<IndexOutOfRangeException>(ex);

        // The same file is fine for the reference implementation: the artifact is not corrupt.
        Assert.True(new FileInfo(SpmPath("de")).Length > 200_000);

        // Isolates WHICH field the library chokes on: the identical proto with bos_id/eos_id set to
        // real ids instead of -1. If this one loads, the defect is the unguarded -1 and a shipping
        // workaround exists (rewrite the proto at build time); if it also throws, the Unigram path
        // is broken outright and German needs our own encoder.
        string patched = Path.Combine(Dir("de"), "tokenizer.bos.model");
        if (File.Exists(patched))
        {
            try
            {
                CtcTokenizer tk = CtcTokenizer.Load(patched);
                output.WriteLine("  with bos_id/eos_id patched to 1/2: LOADS. " +
                                 $"'Zürich' -> [{string.Join(",", tk.Encode("Zürich"))}] " +
                                 "(oracle: [170,49,367]) — the -1 sentinel is the trigger.");
            }
            catch (Exception e2)
            {
                output.WriteLine($"  with bos_id/eos_id patched: still {e2.GetType().Name} — " +
                                 "the Unigram path is broken beyond the -1 sentinel.");
            }
        }
    }

    /// <summary>
    /// Task 5, for the language whose tokenizer our library CAN load. Our <see cref="CtcTokenizer"/>
    /// must agree id-for-id with Python <c>sentencepiece</c> on the same model file, for the same
    /// reason C2 exists in English: a merge sequence that round-trips but differs by one id is
    /// silent zero recall.
    ///
    /// It also pins the two answers the research document guessed at: whether the language's accents
    /// are really in the vocabulary, and whether casing changes the id sequence.
    /// </summary>
    [ModelTheory("ml:pt", "ml-spm-oracle:pt")]
    [InlineData("pt")]
    public void Tokenizer_MatchesSentencePieceOracleAndPinsTheCasingTrap_Pt(string lang)
    {
        var tk = CtcTokenizer.Load(SpmPath(lang));
        var tokens = CtcTokens.Load(TokensPath(lang));

        int agreed = 0, inScope = 0, casingDiffers = 0, unspottable = 0;
        var disagreements = new List<string>();

        foreach (ProbeRow row in ProbeRows(lang))
        {
            if (!row.Cased.SequenceEqual(row.Lower)) casingDiffers++;
            if (row.Unk) { unspottable++; continue; }
            if (row.Term.Trim().Length < 2) continue;   // documented Microsoft.ML.Tokenizers defect

            inScope++;
            int[] ours = [.. tk.Encode(row.Term)];
            if (row.Cased.SequenceEqual(ours)) agreed++;
            else disagreements.Add($"'{row.Term}': oracle [{string.Join(",", row.Cased)}] " +
                                   $"ours [{string.Join(",", ours)}]");
        }

        foreach (string d in disagreements) output.WriteLine("  " + d);
        int total = inScope + unspottable;
        output.WriteLine($"TOKENIZER [{lang}]: {agreed}/{inScope} in-scope terms id-for-id identical; " +
                         $"{unspottable}/{total} unspottable (<unk>); " +
                         $"casing changes the id sequence for {casingDiffers}/{total} terms");

        Assert.True(inScope >= 10, "probe fixture too small");
        Assert.Equal(inScope, agreed);

        // The accents claim, checked directly rather than inferred from the model card.
        foreach (string accented in new[] { "Conceição", "São Paulo", "Gonçalves", "Ministério" })
            Assert.True(tk.IsSpottable(accented, tokens), $"{accented} is not spottable in {lang}");
    }

    /// <summary>The casing question, answered for BOTH languages off the sentencepiece oracle so the
    /// German answer does not depend on the library defect above.</summary>
    [ModelTheory("ml-spm-oracle:de")]
    [InlineData("de")]
    public void E2_CasingChangesTheIdSequence_De(string lang) => ReportCasing(lang);

    [ModelTheory("ml-spm-oracle:pt")]
    [InlineData("pt")]
    public void E2_CasingChangesTheIdSequence_Pt(string lang) => ReportCasing(lang);

    private void ReportCasing(string lang)
    {
        ProbeRow[] rows = [.. ProbeRows(lang)];
        int differs = rows.Count(r => !r.Cased.SequenceEqual(r.Lower));
        int unk = rows.Count(r => r.Unk);
        foreach (ProbeRow r in rows.Where(r => r.Unk))
            output.WriteLine($"  UNSPOTTABLE (<unk>): {r.Term}");
        output.WriteLine($"E2 [{lang}]: casing changes the id sequence for {differs}/{rows.Length} " +
                         $"terms; {unk}/{rows.Length} contain a piece the head cannot emit");
        Assert.True(rows.Length >= 10);
    }

    private readonly record struct ProbeRow(string Term, int[] Cased, int[] Lower, bool Unk);

    private static IEnumerable<ProbeRow> ProbeRows(string lang)
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, $"{lang}_tokenizer_probe.json")));
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
            yield return new ProbeRow(
                row.GetProperty("term").GetString()!,
                [.. row.GetProperty("cased").EnumerateArray().Select(e => e.GetInt32())],
                [.. row.GetProperty("lower").EnumerateArray().Select(e => e.GetInt32())],
                row.GetProperty("unk").GetBoolean());
    }

    // ------------------------------------------------------------------ E4: cost

    [ModelTheory("ml:de", "ml-audio:de", "ml-spm-oracle:de")]
    [InlineData("de")]
    public void E4_LatencyAndMemoryVersusEnglish_De(string lang) => AssertCost(lang, Graph(lang));

    [ModelTheory("ml:pt", "ml-audio:pt", "ml-spm-oracle:pt")]
    [InlineData("pt")]
    public void E4_LatencyAndMemoryVersusEnglish_Pt(string lang) => AssertCost(lang, Graph(lang));

    /// <summary>
    /// The BASELINE the two above are compared against — the shipping English graph, run by the
    /// SAME harness on the SAME audio. Without it the comparison is against a number measured a
    /// different way (5 s chirp, one forward pass) and any "3.5x the memory" conclusion would be an
    /// artifact of the method rather than of the checkpoint.
    /// </summary>
    [ModelTheory("installed-ctc", "ml-audio:pt", "ml-spm-oracle:pt")]
    [InlineData("pt")]
    public void E4_LatencyAndMemoryBaseline_EnglishGraph(string lang) =>
        AssertCost(lang, new CtcModel().Graph, englishBaseline: true);

    private void AssertCost(string lang, string graphPath, bool englishBaseline = false)
    {
        Corpus corpus = Corpus.Load(AudioDir(lang));
        // The baseline runs the ENGLISH graph, so its blank id and term ids come from the English
        // model — the audio's language is irrelevant to latency and working set.
        var tokens = CtcTokens.Load(englishBaseline ? new CtcModel().Tokens : TokensPath(lang));

        // Concatenate real clips into 10 / 40 / 60 s of continuous speech — the same ladder the
        // English M0 measurement used, so the numbers are comparable.
        float[] pool = [.. corpus.Clips.Take(60)
            .SelectMany(c => WavAudio.ReadMono16k(Path.Combine(AudioDir(lang), c.File)))];

        var p = Process.GetCurrentProcess();
        GC.Collect(); GC.WaitForPendingFinalizers(); p.Refresh();
        long before = p.WorkingSet64;

        var sw = Stopwatch.StartNew();
        var enc = new CtcEncoder(graphPath, new OnnxSessionFactory());
        sw.Stop();
        output.WriteLine($"COST [{(englishBaseline ? "en BASELINE" : lang)}]: " +
                         $"session load {sw.ElapsedMilliseconds} ms, " +
                         $"graph {new FileInfo(graphPath).Length / 1048576.0:F1} MB on disk");

        var fe = new CtcMelFrontend();
        var ids = ProbeRows(lang).ToDictionary(r => r.Term, r => r, StringComparer.Ordinal);
        // The baseline's term ids are meaningless in the English id space; what matters for cost is
        // that the DP walks the same number of queries of comparable length.
        var queries = corpus.Terms
            .Where(t => ids.TryGetValue(t, out ProbeRow r) && !r.Unk
                        && r.Cased.All(id => id < tokens.Count && id != tokens.BlankId))
            .Select(t => new CtcSpotQuery(t, [], ids[t].Cased)).ToList();
        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds);

        foreach (int seconds in new[] { 10, 40, 60 })
        {
            int n = Math.Min(pool.Length, seconds * 16_000);
            if (n < seconds * 16_000) { output.WriteLine($"  {seconds}s: not enough audio"); continue; }
            float[] pcm = pool[..n];

            var times = new List<double>();
            for (int i = 0; i < 6; i++)
            {
                var t = Stopwatch.StartNew();
                float[] fm = fe.ComputeFeatureMajor(pcm, out int frames);
                float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);
                _ = dp.Spot(lp, outFrames, vocab, queries);
                t.Stop();
                if (i > 0) times.Add(t.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            output.WriteLine($"  {seconds,3}s speech, {queries.Count} terms: " +
                             $"p50 {times[times.Count / 2]:F0} ms  (min {times[0]:F0}, max {times[^1]:F0})" +
                             $"   [English CPU p50 @60s = 1987 ms]");
        }

        p.Refresh();
        long loaded = p.WorkingSet64;
        enc.Dispose();
        GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(400); p.Refresh();
        // Compare this ONLY against E4_LatencyAndMemoryBaseline_EnglishGraph, never against the
        // +283 MB in vocabulary-ctc-port.md: that figure came from one forward pass over a 5 s
        // chirp, and ORT's arena grows with the longest sequence it has seen. Different method,
        // different number, and reading one against the other invents a regression that is not there.
        output.WriteLine($"  working set +{(loaded - before) / 1048576.0:F0} MB resident, " +
                         $"returned {(loaded - p.WorkingSet64) / 1048576.0:F0} MB on dispose " +
                         "(compare only with the en BASELINE row above — same harness, same audio)");
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Clip(string Kind, string File, string Raw, string? Term, string Spk, string Src);

    private sealed record Corpus(string[] Terms, Clip[] Clips)
    {
        public static Corpus Load(string audioDir)
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(audioDir, "manifest.json")));
            string[] terms = [.. doc.RootElement.GetProperty("terms").EnumerateArray()
                                                .Select(e => e.GetString()!)];
            var clips = new List<Clip>();
            foreach (JsonElement c in doc.RootElement.GetProperty("clips").EnumerateArray())
                clips.Add(new Clip(
                    c.GetProperty("kind").GetString()!,
                    c.GetProperty("file").GetString()!,
                    c.GetProperty("raw").GetString()!,
                    c.TryGetProperty("term", out JsonElement t) ? t.GetString() : null,
                    c.TryGetProperty("spk", out JsonElement s) ? s.GetString()! : "",
                    c.GetProperty("src").GetString()!));
            return new Corpus(terms, [.. clips]);
        }
    }

    /// <summary>Lowercase, strip punctuation, collapse whitespace — so the WER measures the acoustics
    /// and not the corpus's punctuation conventions.</summary>
    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s.ToLowerInvariant())
            sb.Append(char.IsLetter(ch) || char.IsDigit(ch) ? ch : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

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
}
