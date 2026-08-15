using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Nemotron;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// E5 — "how much does the acoustic spotter add over the model-free corrector?", run on real human
/// speech through the real pipeline. NOT a unit test: every stage is off unless <c>JOT_VOCAB_EVAL</c>
/// names it, because each one wants a 1.2 GB model, a 2.8 h corpus on D:, and tens of minutes.
///
/// Staged and cached on purpose — transcription is the expensive half and the scoring rules were
/// rewritten several times against the same decoded text.
///
///   <c>$env:JOT_VOCAB_EVAL="transcribe"</c>  FLEURS wav → Nemotron fp16/DML → TextPipeline.Clean
///   <c>$env:JOT_VOCAB_EVAL="spot"</c>        the shipping CTC spotter over the same clips
///   <c>$env:JOT_VOCAB_EVAL="report"</c>      corrector vs spotter vs both, through VocabularyGate
///   <c>$env:JOT_VOCAB_EVAL="concat-scan"</c>  exact/near multi-word-span × term, for the concat-span rule
/// </summary>
public class VocabEvalHarness(ITestOutputHelper output)
{
    private const string Root = @"D:\caches\jot-vocab-eval";
    private static string Fleurs => Path.Combine(Root, "fleurs");
    private static string Out => Path.Combine(Root, "out");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [Fact]
    public void Run()
    {
        string stage = Environment.GetEnvironmentVariable("JOT_VOCAB_EVAL") ?? "";
        if (stage.Length == 0) return;

        Directory.CreateDirectory(Out);
        switch (stage)
        {
            case "transcribe": Transcribe(); break;
            case "spot": Spot(); break;
            case "calibrate": Calibrate(); break;
            case "report": Report(); break;
            case "concat-scan": ConcatScan(); break;
            default: throw new ArgumentException($"unknown JOT_VOCAB_EVAL stage '{stage}'");
        }
    }

    // MARK: - Corpus

    private sealed record Clip(string Split, string Sentence, string File, string Reference, double Seconds);

    /// <summary>Every FLEURS en_us dev+test utterance. Each sentence is read by ~2 speakers, which is
    /// the whole point — a term's recall has to hold across voices, not on one TTS rendering.</summary>
    private static List<Clip> LoadCorpus()
    {
        var clips = new List<Clip>();
        foreach (string split in new[] { "test", "dev" })
        {
            foreach (string line in File.ReadAllLines(Path.Combine(Fleurs, split + ".tsv")))
            {
                string[] p = line.Split('\t');
                if (p.Length < 7) continue;
                string wav = Path.Combine(Fleurs, "audio", split, p[1]);
                if (!File.Exists(wav)) continue;
                clips.Add(new Clip(split, p[0], p[1], p[2],
                    int.Parse(p[5], CultureInfo.InvariantCulture) / 16000.0));
            }
        }
        return clips;
    }

    /// <summary>
    /// The term list, derived from the references so the selection is reproducible and not hand-picked:
    /// a capitalised, purely-alphabetic word of 4+ characters that is NOT sentence-initial (which says
    /// nothing about the word) and NOT in the shipped 24k English frequency list. That is exactly the
    /// class custom vocabulary exists for — names, places, jargon — and it is what an ASR mis-spells.
    ///
    /// Possessives and hyphenated forms are excluded: a user types "Martelly", not "Martelly's", and
    /// keeping them would only hand the corrector free wins on terms the CTC tokenizer cannot even
    /// represent (a difference already documented, not one this corpus needs to prove).
    /// </summary>
    private static List<string> BuildTerms(List<Clip> clips)
    {
        IReadOnlySet<string> common = EmbeddedCommonWordsProvider.Shared.Words("common-words");
        var terms = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Clip c in clips)
        {
            string[] words = c.Reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < words.Length; i++)
            {
                string w = new([.. words[i].Where(char.IsLetter)]);
                if (w.Length < 4 || w.Length != words[i].Trim('.', ',', '"', '\'', '?', '!', ';', ':', ')', '(').Length)
                    continue;
                if (!char.IsUpper(w[0]) || !w.All(char.IsLetter)) continue;
                if (common.Contains(CorrectionKey.Lowercased(w))) continue;
                terms.Add(w);
            }
        }
        return [.. terms];
    }

    // MARK: - Stage 1 · transcribe

    private void Transcribe()
    {
        List<Clip> clips = LoadCorpus();
        List<string> terms = BuildTerms(clips);
        File.WriteAllLines(Path.Combine(Out, "terms.txt"), terms);
        output.WriteLine($"corpus: {clips.Count} clips, {clips.Sum(c => c.Seconds) / 3600:F2} h, {terms.Count} terms");

        var model = new NemotronFp16Model();
        Assert.True(model.IsInstalled, "fp16 Nemotron not installed");
        var factory = new OnnxSessionFactory();
        using var engine = new NemotronFp16Transcriber(model, factory, ComputeBackend.DirectML);
        engine.SetLanguageSlot(0);
        engine.WarmUp();

        // Resumable and append-only: the ONNX/DirectML session takes the whole test host down every few
        // hundred clips, and re-decoding 2.8 h to get past it is not a debugging strategy.
        string path = Path.Combine(Out, "transcripts.jsonl");
        string inflight = Path.Combine(Out, "inflight.txt");
        HashSet<string> done = [.. Resume<Hypothesis>(path).Select(h => h.File)];
        using var writer = new StreamWriter(path, append: true) { AutoFlush = true };

        // A clip the previous process died ON is banked as an empty transcript rather than retried, or
        // the outer retry loop reruns the crash forever.
        if (File.Exists(inflight) && File.ReadAllText(inflight).Trim() is { Length: > 0 } stuck &&
            !done.Contains(stuck) && clips.FirstOrDefault(c => c.File == stuck) is { } victim)
        {
            output.WriteLine($"CRASHED ON {stuck} — banking it empty");
            writer.WriteLine(JsonSerializer.Serialize(
                new Hypothesis(victim.Split, victim.File, victim.Sentence, victim.Reference, "", victim.Seconds), Json));
            done.Add(stuck);
        }

        var sw = Stopwatch.StartNew();
        int n = done.Count;
        foreach (Clip c in clips.Where(c => !done.Contains(c.File)))
        {
            File.WriteAllText(inflight, c.File);
            float[] samples = WavAudio.ReadMono16k(Path.Combine(Fleurs, "audio", c.Split, c.File));
            string text;
            try
            {
                string raw = engine.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult().Trim();
                text = Jot.Text.TextPipeline.Clean(raw, "en-US", isNemotron: true);
            }
            catch (Exception ex)
            {
                output.WriteLine($"FAILED {c.File}: {ex.Message}");
                text = "";
            }
            writer.WriteLine(JsonSerializer.Serialize(new Hypothesis(
                c.Split, c.File, c.Sentence, c.Reference, text,
                samples.Length / (double)WavAudio.SampleRate), Json));
            if (++n % 50 == 0) output.WriteLine($"{n}/{clips.Count}  {sw.Elapsed:mm\\:ss}");
        }
        File.Delete(inflight);
        output.WriteLine($"done {n} clips in {sw.Elapsed:mm\\:ss}");
    }

    private sealed record Hypothesis(
        string Split, string File, string Sentence, string Reference, string Text, double Seconds);

    // MARK: - Stage 2 · the acoustic spotter

    private sealed record SpotRow(string File, double Ms, List<DetectionRow> Detections);

    private sealed record DetectionRow(string Term, float Score, double Start, double End);

    private void Spot()
    {
        List<Hypothesis> hyps = ReadHypotheses();
        List<VocabularyTerm> terms =
            [.. File.ReadAllLines(Path.Combine(Out, "terms.txt")).Select(t => new VocabularyTerm { Text = t })];

        var model = new CtcModel();
        Assert.True(model.IsInstalled, "CTC spotter model not installed");
        var factory = new OnnxSessionFactory();
        using var spotter = new CtcVocabularySpotter(model, factory);

        long before = GC.GetTotalMemory(true);
        Process proc = Process.GetCurrentProcess();
        proc.Refresh();
        long wsBefore = proc.WorkingSet64;
        spotter.Warm();
        proc.Refresh();
        output.WriteLine($"spotter load: working set +{(proc.WorkingSet64 - wsBefore) / (1024.0 * 1024):F0} MB " +
                         $"(managed +{(GC.GetTotalMemory(false) - before) / (1024.0 * 1024):F0} MB)");

        // Spottability is a property of the checkpoint's BPE, and a term it cannot encode is silent
        // zero recall — recorded so the head-to-head can be reported with and without those terms.
        File.WriteAllLines(Path.Combine(Out, "spottability.tsv"),
            terms.Select(t => $"{t.Text}\t{spotter.CheckTerm(t.Text)}"));

        string path = Path.Combine(Out, "detections.jsonl");
        HashSet<string> done = [.. Resume<SpotRow>(path).Select(r => r.File)];
        using var writer = new StreamWriter(path, append: true) { AutoFlush = true };
        var total = new List<double>();
        int n = done.Count;
        foreach (Hypothesis h in hyps.Where(h => !done.Contains(h.File)))
        {
            float[] samples = WavAudio.ReadMono16k(Path.Combine(Fleurs, "audio", h.Split, h.File));
            var sw = Stopwatch.StartNew();
            IReadOnlyList<VocabularyGate.Detection> dets =
                spotter.Spot(samples, WavAudio.SampleRate, terms, default);
            sw.Stop();
            total.Add(sw.Elapsed.TotalMilliseconds);
            writer.WriteLine(JsonSerializer.Serialize(new SpotRow(h.File, sw.Elapsed.TotalMilliseconds,
                [.. dets.Select(d => new DetectionRow(d.Term, d.Score, d.StartTime, d.EndTime))]), Json));
            if (++n % 100 == 0) output.WriteLine($"{n}/{hyps.Count}");
        }
        total.Sort();
        if (total.Count > 0)
            output.WriteLine($"spotter latency p50 {total[total.Count / 2]:F0} ms, p95 {total[(int)(total.Count * 0.95)]:F0} ms");
    }

    /// <summary>What a previous run finished, with a half-written last line dropped (the crash that
    /// makes resume necessary can land mid-write).</summary>
    private static List<T> Resume<T>(string path)
    {
        if (!File.Exists(path)) return [];
        var rows = new List<T>();
        var kept = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            try { rows.Add(JsonSerializer.Deserialize<T>(line)!); kept.Add(line); }
            catch (JsonException) { }
        }
        File.WriteAllLines(path, kept);
        return rows;
    }

    private static List<Hypothesis> ReadHypotheses() =>
        [.. File.ReadAllLines(Path.Combine(Out, "transcripts.jsonl"))
              .Select(l => JsonSerializer.Deserialize<Hypothesis>(l)!)];

    // MARK: - Stage 2b · calibration for the confidence-conditional ceiling (E8)

    /// <summary>
    /// One row per ACOUSTIC detection: what the spotter heard, how sure it was, and how far the nearest
    /// thing the engine wrote is from the term. This is the joint distribution the ceiling has to be a
    /// function of, and until it is on disk any mapping from score to ceiling is a number picked by
    /// feel — which is the one thing E5 §8.1 asked this experiment not to be.
    ///
    /// <c>truth</c> is the reference, not the gate: <c>said</c> = the term really was spoken in this
    /// clip, <c>absent</c> = it was not (so any apply is a false apply), <c>already</c> = it was spoken
    /// AND the engine already spelled it right (so there is nothing to recover and an apply somewhere
    /// else in the clip is an overwrite waiting to happen).
    /// </summary>
    private void Calibrate()
    {
        List<Hypothesis> hyps = ReadHypotheses();
        string[] all = File.ReadAllLines(Path.Combine(Out, "terms.txt"));
        var focused = Focused(hyps, all).ToHashSet(StringComparer.Ordinal);

        var sb = new StringBuilder("file\tterm\tscore\ttruth\tinFocused\tgap\tnearest\n");
        foreach (string line in File.ReadAllLines(Path.Combine(Out, "detections.jsonl")))
        {
            SpotRow row = JsonSerializer.Deserialize<SpotRow>(line)!;
            Hypothesis? h = hyps.FirstOrDefault(x => x.File == row.File);
            if (h is null) continue;
            string[] refWords = Words(h.Reference);
            string[] hypWords = Words(h.Text);

            foreach (DetectionRow d in row.Detections)
            {
                string truth = !Contains(refWords, d.Term) ? "absent"
                    : Contains(hypWords, d.Term) ? "already"
                    : "said";
                sb.AppendLine(string.Join('\t', row.File, d.Term,
                    d.Score.ToString("F3", CultureInfo.InvariantCulture), truth,
                    focused.Contains(d.Term),
                    NearestGap(hypWords, d.Term).ToString("F3", CultureInfo.InvariantCulture),
                    NearestSpan(hypWords, d.Term)));
            }
        }
        File.WriteAllText(Path.Combine(Out, "detection-calibration.tsv"), sb.ToString());
        output.WriteLine($"wrote detection-calibration.tsv");
    }

    /// <summary>The 1- or 2-word window of the transcript that <see cref="NearestGap"/> measured, so a
    /// calibration row can be read by a human without re-deriving it.</summary>
    private static string NearestSpan(string[] hypWords, string term)
    {
        double best = double.MaxValue;
        string span = "—";
        for (int w = 1; w <= 2; w++)
        {
            for (int i = 0; i + w <= hypWords.Length; i++)
            {
                string candidate = string.Join(' ', hypWords[i..(i + w)]);
                double g = VocabularyGate.Gap(candidate, term, []);
                if (g < best) { best = g; span = candidate; }
            }
        }
        return span;
    }

    /// <summary>The realistic 25 — the terms this engine got wrong most often. Extracted so the report
    /// and the calibration stage cannot pick two different lists and call them the same arm.</summary>
    private static string[] Focused(List<Hypothesis> hyps, string[] all)
    {
        Dictionary<string, int> misses = all.ToDictionary(t => t, _ => 0);
        foreach (Hypothesis h in hyps)
        {
            string[] refWords = Words(h.Reference);
            string[] hypWords = Words(h.Text);
            foreach (string t in all)
            {
                if (Contains(refWords, t) && !Contains(hypWords, t)) misses[t]++;
            }
        }
        return [.. misses.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(25).Select(kv => kv.Key)];
    }

    /// <summary>
    /// Would a bare acoustic detection of <paramref name="term"/> (empty aliases — the English
    /// spotter shape) apply <em>this</em> multi-word window? Returns the published original span
    /// when it did, otherwise empty. Times sit on the window so position cannot prefer a shard
    /// in another sentence half.
    /// </summary>
    private static string BareSpotterApplied(
        Hypothesis h, IReadOnlyList<(int Start, int End)> spans, int index, int width, string term)
    {
        double at = h.Seconds > 0
            ? (index + width / 2.0) / spans.Count * h.Seconds
            : 0;
        var det = new VocabularyGate.Detection(term, [], -1.0f, at, at, Acoustic: true);
        VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
            h.Text, [det], h.Seconds, EmbeddedCommonWordsProvider.Shared);
        string spanText = h.Text[spans[index].Start..spans[index + width - 1].End];
        Rune[] want = VocabularyGate.SkeletonOf(spanText);
        foreach (VocabularyGate.Proposal p in r.Proposals)
        {
            if (p.Outcome != "applied") continue;
            Rune[] got = VocabularyGate.SkeletonOf(p.OriginalWord);
            if (got.AsSpan().SequenceEqual(want)) return p.OriginalWord;
        }
        return "";
    }

    // MARK: - Stage 3 · head to head

    /// <summary>
    /// Every width-2..4 window of every cached transcript against every term. Answers whether the
    /// concat-span rule is exercised on this corpus and what a full-unit (not exact) rewrite of
    /// <c>IsCommonSpan</c> would newly admit. Also: would a bare spotter detection (empty aliases,
    /// Acoustic) apply that window — the width-unlock hole. Deterministic; no audio.
    /// </summary>
    private void ConcatScan()
    {
        List<Hypothesis> hyps = ReadHypotheses();
        string[] all = File.ReadAllLines(Path.Combine(Out, "terms.txt"));
        string[] focused = Focused(hyps, all);
        var focusedSet = focused.ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> common = EmbeddedCommonWordsProvider.Shared.Words("common-words");
        var corrector = new VocabularyCorrector();

        int exactWindows = 0, exactTp = 0, exactFpAbsent = 0, exactFpOver = 0;
        int exactCommon = 0, exactFocused = 0;
        int nearWindows = 0, nearCommon = 0, nearFpAbsent = 0, nearTp = 0;
        // Bare spotter (empty aliases, Acoustic, strong score): does the gate APPLY this window?
        // This is the width-unlock hole — the concat-span rule is inert until the pair is a host.
        int exactBareApplied = 0, exactBareTp = 0, exactBareFp = 0, nearBareApplied = 0;
        var exactRows = new StringBuilder("file\tterm\tspan\twidth\tfocused\tclass\tanyCommon\tinCorrector\tbareSpotter\n");
        var nearFpRows = new StringBuilder("file\tterm\tspan\twidth\tgap\tclass\n");
        var nearBareRows = new StringBuilder("file\tterm\tspan\twidth\tgap\tappliedAs\n");

        foreach (Hypothesis h in hyps)
        {
            string[] refWords = Words(h.Reference);
            string[] hypWords = Words(h.Text);
            IReadOnlyList<(int Start, int End)> spans = VocabularyGate.WordSpans(h.Text);
            var present = all.Where(t => Contains(refWords, t)).ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<VocabularyGate.Detection> textual =
                corrector.Spot(h.Text, [.. all.Select(t => new VocabularyTerm { Text = t })], h.Seconds, "en-US");
            var textualTerms = textual.Select(d => d.Term).ToHashSet(StringComparer.Ordinal);

            for (int w = 2; w <= Math.Min(4, spans.Count); w++)
            {
                for (int i = 0; i + w <= spans.Count; i++)
                {
                    string spanText = h.Text[spans[i].Start..spans[i + w - 1].End];
                    bool anyCommon = VocabularyGate.IsCommonSpan(spanText, common);
                    foreach (string term in all)
                    {
                        double gap = VocabularyGate.Gap(spanText, term, []);
                        if (gap > VocabularyGate.PlausibilityCeiling) continue;
                        bool said = present.Contains(term);
                        string klass = !said ? "FP-absent"
                            : Contains(refWords, spanText) ? "FP-overwrote"
                            : "TP";
                        if (gap == 0)
                        {
                            exactWindows++;
                            if (anyCommon) exactCommon++;
                            if (focusedSet.Contains(term)) exactFocused++;
                            switch (klass)
                            {
                                case "TP": exactTp++; break;
                                case "FP-absent": exactFpAbsent++; break;
                                default: exactFpOver++; break;
                            }
                            string bare = BareSpotterApplied(h, spans, i, w, term);
                            if (bare.Length > 0)
                            {
                                exactBareApplied++;
                                if (klass == "TP") exactBareTp++;
                                else exactBareFp++;
                            }
                            exactRows.AppendLine(string.Join('\t',
                                h.File, term, spanText.Replace('\t', ' '), w,
                                focusedSet.Contains(term), klass, anyCommon, textualTerms.Contains(term),
                                bare.Length > 0 ? bare : "—"));
                        }
                        else
                        {
                            nearWindows++;
                            if (anyCommon)
                            {
                                nearCommon++;
                                if (klass == "FP-absent")
                                {
                                    nearFpAbsent++;
                                    if (nearFpAbsent <= 40)
                                        nearFpRows.AppendLine(string.Join('\t',
                                            h.File, term, spanText.Replace('\t', ' '), w,
                                            gap.ToString("F3", CultureInfo.InvariantCulture), klass));
                                }
                                else if (klass == "TP") nearTp++;
                            }
                            string bare = BareSpotterApplied(h, spans, i, w, term);
                            if (bare.Length > 0)
                            {
                                nearBareApplied++;
                                if (nearBareApplied <= 20)
                                    nearBareRows.AppendLine(string.Join('\t',
                                        h.File, term, spanText.Replace('\t', ' '), w,
                                        gap.ToString("F3", CultureInfo.InvariantCulture), bare));
                            }
                        }
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"clips            {hyps.Count}");
        sb.AppendLine($"terms            {all.Length}  (focused-25 {focused.Length})");
        sb.AppendLine($"focused-25       {string.Join(", ", focused)}");
        sb.AppendLine();
        sb.AppendLine("### exact concat (gap 0.00, width 2-4)");
        sb.AppendLine($"windows          {exactWindows}");
        sb.AppendLine($"  any-common     {exactCommon}");
        sb.AppendLine($"  focused-25     {exactFocused}");
        sb.AppendLine($"  TP             {exactTp}");
        sb.AppendLine($"  FP-absent      {exactFpAbsent}");
        sb.AppendLine($"  FP-overwrote   {exactFpOver}");
        sb.AppendLine($"  bare-spotter applied     {exactBareApplied}  (TP {exactBareTp} / FP {exactBareFp})");
        sb.AppendLine();
        sb.AppendLine("### near concat (0 < gap ≤ 0.45, width 2-4) — full-unit rewrite population");
        sb.AppendLine($"windows          {nearWindows}");
        sb.AppendLine($"  any-common     {nearCommon}");
        sb.AppendLine($"    of those TP  {nearTp}");
        sb.AppendLine($"    of those FP-absent (first 40 listed)  {nearFpAbsent}");
        sb.AppendLine($"  bare-spotter applied as this multi-word span  {nearBareApplied}");
        sb.AppendLine();
        sb.Append(exactRows);
        sb.AppendLine();
        sb.Append(nearFpRows);
        if (nearBareApplied > 0)
        {
            sb.AppendLine();
            sb.Append(nearBareRows);
        }

        string report = sb.ToString();
        output.WriteLine(report);
        File.WriteAllText(Path.Combine(Out, "concat-scan.txt"), report);
    }

    private sealed record Applied(string Original, string Term, string Verdict);

    private void Report()
    {
        List<Hypothesis> hyps = ReadHypotheses();
        string[] all = File.ReadAllLines(Path.Combine(Out, "terms.txt"));

        // Two list sizes, because false applies scale with the list and one number would be misleading
        // either way. 155 terms is a stress list nobody would type; the "focused" one is the realistic
        // shape — a user adds the terms they actually say — and is picked from the corpus, not by hand:
        // the terms the engine got wrong most often.
        string[] focused = Focused(hyps, all);

        var sb = new StringBuilder();
        sb.AppendLine($"clips            {hyps.Count}   ({hyps.Select(h => h.Sentence).Distinct().Count()} distinct sentences)");
        sb.AppendLine($"audio hours      {hyps.Sum(h => h.Seconds) / 3600:F2}");
        sb.AppendLine($"reference words  {hyps.Sum(h => Words(h.Reference).Length)}");
        Evaluate(hyps, all, "all-155", sb, dumpRows: true);
        Evaluate(hyps, focused, "focused-25", sb, dumpRows: false);

        string report = sb.ToString();
        output.WriteLine(report);
        File.WriteAllText(Path.Combine(Out, "report.txt"), report);
    }

    private void Evaluate(List<Hypothesis> hyps, string[] termTexts, string label, StringBuilder sb, bool dumpRows)
    {
        List<VocabularyTerm> terms = [.. termTexts.Select(t => new VocabularyTerm { Text = t })];
        var inList = termTexts.ToHashSet(StringComparer.Ordinal);

        Dictionary<string, List<VocabularyGate.Detection>> spotted = [];
        Dictionary<string, double> spotMs = [];
        foreach (string line in File.ReadAllLines(Path.Combine(Out, "detections.jsonl")))
        {
            SpotRow row = JsonSerializer.Deserialize<SpotRow>(line)!;
            // Subsetting the recorded detections stands in for re-running the spotter on the smaller
            // list. The DP scores each term independently and suppresses overlaps per TERM, so the
            // only thing dropping a query changes is that its own hits disappear.
            spotted[row.File] = [.. row.Detections.Where(d => inList.Contains(d.Term)).Select(d =>
                // Acoustic: these came off the CTC spotter, and E8's earned ceiling is a function of
                // that. Replaying them without the flag would score the experiment against the
                // shipped fixed ceiling and report no change at all.
                new VocabularyGate.Detection(d.Term, [], d.Score, d.Start, d.End, Acoustic: true))];
            spotMs[row.File] = row.Ms;
        }

        var spottable = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Path.Combine(Out, "spottability.tsv")))
        {
            string[] p = line.Split('\t');
            if (p.Length == 2 && p[1] == nameof(TermSpottability.Ok)) spottable.Add(p[0]);
        }

        var corrector = new VocabularyCorrector();
        // The "-fixed" arms are E8's counterfactual and cost nothing to carry: the earned ceiling is a
        // function of the detection's own Acoustic flag, so replaying the SAME detections with it off
        // reproduces the shipped fixed-0.45 gate exactly, in the same process, on the same rows.
        string[] armNames = ["corrector", "spotter-fixed", "spotter", "both-fixed", "both"];
        var arms = armNames.ToDictionary(a => a, a => new Arm(a));
        var baseline = new Arm("baseline");
        var correctorMs = new List<double>();

        // Raw rows as well as the summary: every conclusion below has to be re-sliceable without
        // re-decoding 2.8 h of audio, and an aggregate that cannot be audited is not evidence.
        var corrections = new StringBuilder("arm\tfile\toriginal\tterm\tclass\tdecoyClip\n");
        var chances = new StringBuilder("file\tterm\tnearestGap\tspottable\tdetText\tdetAcoustic\trecCorrector\trecSpotter\trecBoth\n");

        foreach (Hypothesis h in hyps)
        {
            string[] refWords = Words(h.Reference);
            string[] hypWords = Words(h.Text);
            var present = termTexts.Where(t => Contains(refWords, t)).ToHashSet(StringComparer.Ordinal);
            var missing = present.Where(t => !Contains(hypWords, t)).ToHashSet(StringComparer.Ordinal);

            baseline.Add(refWords, hypWords, missing, [], spottable);

            var sw = Stopwatch.StartNew();
            // en-US, not omitted: Spot(language: null) is Unmeasured 0.15 after E6, and E5 scored
            // English's uncapped setting. A remasurement that forgets this is not an E5 comparison.
            IReadOnlyList<VocabularyGate.Detection> textual = corrector.Spot(h.Text, terms, h.Seconds, "en-US");
            sw.Stop();
            correctorMs.Add(sw.Elapsed.TotalMilliseconds);
            IReadOnlyList<VocabularyGate.Detection> acoustic = spotted.GetValueOrDefault(h.File) ?? [];
            IReadOnlyList<VocabularyGate.Detection> fixedCeiling =
                [.. acoustic.Select(d => d with { Acoustic = false })];

            var outWords = new Dictionary<string, string[]>();
            foreach ((string name, IReadOnlyList<VocabularyGate.Detection> dets) in new[]
            {
                ("corrector", textual),
                ("spotter-fixed", fixedCeiling),
                ("spotter", acoustic),
                ("both-fixed", Merge(fixedCeiling, textual)),
                ("both", Merge(acoustic, textual)),
            })
            {
                VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
                    h.Text, dets, h.Seconds, EmbeddedCommonWordsProvider.Shared, "common-words");
                List<Applied> applied = [.. r.Proposals
                    .Where(p => p.Outcome == "applied")
                    .Select(p => new Applied(p.OriginalWord, p.Term, p.Decision))];
                outWords[name] = Words(r.Text);
                arms[name].Add(refWords, outWords[name], missing, applied, spottable);

                foreach (Applied a in applied)
                    corrections.AppendLine($"{name}\t{h.File}\t{a.Original}\t{a.Term}\t{Classify(refWords, a)}\t{present.Count == 0}");
            }

            foreach (string t in missing)
            {
                chances.AppendLine(string.Join('\t',
                    h.File, t, NearestGap(hypWords, t).ToString("F3", CultureInfo.InvariantCulture),
                    spottable.Contains(t),
                    textual.Any(d => d.Term == t), acoustic.Any(d => d.Term == t),
                    Contains(outWords["corrector"], t), Contains(outWords["spotter"], t),
                    Contains(outWords["both"], t)));
            }
        }

        correctorMs.Sort();
        List<double> spotList = [.. spotMs.Values.OrderBy(x => x)];

        sb.AppendLine();
        sb.AppendLine($"### {label}: {termTexts.Length} terms " +
                      $"({termTexts.Count(spottable.Contains)} spottable by the CTC checkpoint's BPE)");
        sb.AppendLine($"corrector ms     p50 {correctorMs[correctorMs.Count / 2]:F2}  p95 {correctorMs[(int)(correctorMs.Count * 0.95)]:F2}  max {correctorMs[^1]:F2}");
        if (spotList.Count > 0)
            sb.AppendLine($"spotter ms       p50 {spotList[spotList.Count / 2]:F0}  p95 {spotList[(int)(spotList.Count * 0.95)]:F0}  max {spotList[^1]:F0}");
        sb.AppendLine(Arm.Header);
        sb.AppendLine(baseline.Line);
        foreach (string n in armNames) sb.AppendLine(arms[n].Line);

        if (!dumpRows) return;
        File.WriteAllText(Path.Combine(Out, "corrections.tsv"), corrections.ToString());
        File.WriteAllText(Path.Combine(Out, "opportunities.tsv"), chances.ToString());
    }

    /// <summary>Acoustic detections win per TERM: a transcript can legitimately host the same term
    /// twice, and letting both sources place it independently would splice it twice.</summary>
    private static IReadOnlyList<VocabularyGate.Detection> Merge(
        IReadOnlyList<VocabularyGate.Detection> acoustic, IReadOnlyList<VocabularyGate.Detection> textual)
    {
        var claimed = acoustic.Select(d => d.Term).ToHashSet(StringComparer.Ordinal);
        return [.. acoustic, .. textual.Where(d => !claimed.Contains(d.Term))];
    }

    private static string Classify(string[] refWords, Applied a) =>
        VocabEvalScoring.Classify(refWords, a.Term, a.Original);

    private sealed class Arm(string name)
    {
        public static string Header =>
            "arm          WER%  opportunities  recovered  recall%   spottable-recall%   applied    TP  FP-absent  FP-overwrote  FP/1k-words";

        private int _opportunities, _recovered, _tp, _fpAbsent, _fpOver, _refWords, _errors,
                    _spottableOpp, _spottableRec;

        public void Add(string[] refWords, string[] outWords, HashSet<string> missing,
                        List<Applied> applied, HashSet<string> spottable)
        {
            _refWords += refWords.Length;
            _errors += WordErrors(refWords, outWords);
            _opportunities += missing.Count;
            _spottableOpp += missing.Count(spottable.Contains);
            foreach (string t in missing)
            {
                if (!Contains(outWords, t)) continue;
                _recovered++;
                if (spottable.Contains(t)) _spottableRec++;
            }
            foreach (Applied a in applied)
            {
                switch (Classify(refWords, a))
                {
                    case "TP": _tp++; break;
                    case "FP-absent": _fpAbsent++; break;
                    default: _fpOver++; break;
                }
            }
        }

        public string Line =>
            $"{name,-11} {100.0 * _errors / _refWords,5:F2}  {_opportunities,13}  {_recovered,9}  " +
            $"{(_opportunities == 0 ? 0 : 100.0 * _recovered / _opportunities),6:F1}  " +
            $"{(_spottableOpp == 0 ? 0 : 100.0 * _spottableRec / _spottableOpp),17:F1}  " +
            $"{_tp + _fpAbsent + _fpOver,8}  {_tp,4}  {_fpAbsent,9}  {_fpOver,12}  " +
            $"{1000.0 * (_fpAbsent + _fpOver) / _refWords,10:F2}";
    }

    // MARK: - Text measures
    //
    // SHARED with the per-language harness (E6) on purpose — its whole job is comparing a language's
    // false-apply rate against the English baseline below, and two harnesses that fold text
    // differently are not comparing anything. See VocabEvalScoring.

    private static string[] Words(string text) => VocabEvalScoring.Words(text);

    private static bool Contains(string[] words, string phrase) => VocabEvalScoring.Contains(words, phrase);

    private static double NearestGap(string[] hypWords, string term) =>
        VocabEvalScoring.NearestGap(hypWords, term);

    private static int WordErrors(string[] reference, string[] hypothesis) =>
        VocabEvalScoring.WordErrors(reference, hypothesis);
}
