using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jot.Transcription;
using Jot.Transcription.Nemotron;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// E6 — "is the common-word brake strong enough OUTSIDE English?", run per language on real human
/// speech through the real pipeline. Same shape as <see cref="VocabEvalHarness"/> (E5) and it shares
/// E5's scoring definitions on purpose, because the decision is a comparison against E5's English
/// number. NOT a unit test: off unless <c>JOT_VOCAB_EVAL_ML</c> names a stage.
///
///   <c>$env:JOT_VOCAB_EVAL_ML="transcribe"</c>  FLEURS wav → Nemotron fp16/DML at that locale
///   <c>$env:JOT_VOCAB_EVAL_ML="report"</c>      corrector through the real gate, brake on and OFF
///
/// The acoustic spotter has no stage here: it is an English checkpoint, and every language in this
/// experiment is served by the model-free corrector or by nothing.
/// </summary>
public class VocabEvalMultilingualHarness(ITestOutputHelper output)
{
    private const string Root = @"D:\caches\jot-vocab-eval";
    private static string Ml => Path.Combine(Root, "ml");
    private static string Out => Path.Combine(Root, "out");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>One evaluated language: the frequency list's suffix, the FLEURS config, and the
    /// Nemotron locale we actually transcribe it with. <paramref name="Resource"/> overrides which
    /// embedded list is used — only Croatian needs it, and only because that probe is the whole
    /// question there.</summary>
    private sealed record Lang(string Iso, string Fleurs, string Locale, string? Resource = null);

    /// <summary>
    /// The languages <see cref="VocabularyRunner.ModeFor"/> can actually put in Textual mode. That is
    /// the intersection of the 21 embedded frequency lists with Nemotron's 40 locales, MINUS English
    /// (acoustic) — 19, not the 20 the ship note claims: <b>Serbian has no Nemotron locale</b>, so
    /// <c>common-words-sr.txt</c> can never be reached by any setting the picker can produce.
    ///
    /// FLEURS ships one Spanish (es_419, Latin American) and one Portuguese (pt_br), so those two rows
    /// are measured against the es-US / pt-BR slots. The frequency list is per LANGUAGE, not per
    /// locale, so the brake being measured is the same object es-ES and pt-PT would get.
    /// </summary>
    private static readonly Lang[] Languages =
    [
        new("bg", "bg_bg", "bg-BG"),
        new("cs", "cs_cz", "cs-CZ"),
        new("da", "da_dk", "da-DK"),
        new("de", "de_de", "de-DE"),
        new("el", "el_gr", "el-GR"),
        new("es", "es_419", "es-US"),
        new("fi", "fi_fi", "fi-FI"),
        new("fr", "fr_fr", "fr-FR"),
        new("hu", "hu_hu", "hu-HU"),
        new("it", "it_it", "it-IT"),
        new("nl", "nl_nl", "nl-NL"),
        new("pl", "pl_pl", "pl-PL"),
        new("pt", "pt_br", "pt-BR"),
        new("ro", "ro_ro", "ro-RO"),
        new("ru", "ru_ru", "ru-RU"),
        new("sk", "sk_sk", "sk-SK"),
        new("sl", "sl_si", "sl-SI"),
        new("sv", "sv_se", "sv-SE"),
        new("uk", "uk_ua", "uk-UA"),

        // NOT a shipped language — a probe. Croatian is one of Nemotron's 40 locales and is Off today
        // because no `common-words-hr` exists; the unreachable Serbian list is Latin-script BCMS and
        // covers Croatian running text at 81.6 % of tokens / 65.0 % of types, which is inside the range
        // of languages that DO ship. Measured here so the option is decision-ready instead of an
        // opinion about how close two languages are. Enabling it is not this experiment's call.
        new("hr", "hr_hr", "hr-HR", "common-words-sr"),
    ];

    /// <summary>English is scored from E5's transcripts, by this harness's code, so the baseline every
    /// other row is judged against is not a number copied out of another document.</summary>
    private static readonly Lang English = new("en", "en_us", "en-US");

    /// <summary>The caps swept per language. 1.00 is the shipped corrector (its own edit budget is the
    /// only brake); the corrector's reachable maximum is 0.33, so 0.30 / 0.25 / 0.20 are real steps.</summary>
    private static readonly double[] Caps = [VocabularyLimits.NoLimit, 0.30, 0.25, 0.22, 0.20, 0.15];

    [Fact]
    public void Run()
    {
        string stage = Environment.GetEnvironmentVariable("JOT_VOCAB_EVAL_ML") ?? "";
        if (stage.Length == 0) return;

        Directory.CreateDirectory(Out);
        switch (stage)
        {
            case "transcribe": Transcribe(); break;
            case "report": Report(); break;
            default: throw new ArgumentException($"unknown JOT_VOCAB_EVAL_ML stage '{stage}'");
        }
    }

    // MARK: - Corpus

    private sealed record Clip(string Split, string Sentence, string File, string Reference, double Seconds);

    private sealed record Hypothesis(
        string Split, string File, string Sentence, string Reference, string Text, double Seconds);

    private static string LangDir(Lang lang) => Path.Combine(Ml, lang.Fleurs);

    private static string TranscriptPath(Lang lang) =>
        lang.Iso == "en"
            ? Path.Combine(Out, "transcripts.jsonl")          // E5's, unmodified
            : Path.Combine(LangDir(lang), "transcripts.jsonl");

    /// <summary>Every downloaded FLEURS utterance for one language, dev before test, capped so a very
    /// large split cannot eat the whole GPU budget.</summary>
    private static List<Clip> LoadCorpus(Lang lang, int maxClips)
    {
        var clips = new List<Clip>();
        foreach (string split in new[] { "dev", "test" })
        {
            string tsv = Path.Combine(LangDir(lang), split + ".tsv");
            if (!File.Exists(tsv)) continue;
            foreach (string line in File.ReadAllLines(tsv))
            {
                string[] p = line.Split('\t');
                if (p.Length < 7) continue;
                string wav = Path.Combine(LangDir(lang), "audio", split, p[1]);
                if (!File.Exists(wav)) continue;
                clips.Add(new Clip(split, p[0], p[1], p[2],
                    int.Parse(p[5], CultureInfo.InvariantCulture) / 16000.0));
                if (clips.Count >= maxClips) return clips;
            }
        }
        return clips;
    }

    /// <summary>
    /// E5's term rule, unchanged and applied mechanically per language: a reference word that is
    /// capitalised, purely alphabetic, 4+ characters, NOT sentence-initial, and NOT in THAT language's
    /// frequency list. Hand-picking terms per language would be the one thing that could not be
    /// defended, so the selection stays a function of the corpus and the shipped list.
    ///
    /// It has a known per-language consequence, reported rather than corrected: German capitalises
    /// every noun, so German's list is far larger and far more ordinary-word-ish than French's. That
    /// IS the honest comparison — it is the same rule reading a different language — and the
    /// focused-25 arm, which every language runs at the same list size, is what the decision uses.
    /// </summary>
    private static List<string> BuildTerms(IEnumerable<string> references, IReadOnlySet<string> common)
    {
        var terms = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string reference in references)
        {
            string[] words = reference.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
        int maxClips = int.TryParse(Environment.GetEnvironmentVariable("JOT_VOCAB_EVAL_MAXCLIPS"), out int m)
            ? m : 400;
        string only = Environment.GetEnvironmentVariable("JOT_VOCAB_EVAL_LANG") ?? "";

        var model = new NemotronFp16Model();
        Assert.True(model.IsInstalled, "fp16 Nemotron not installed");
        var factory = new OnnxSessionFactory();
        using var engine = new NemotronFp16Transcriber(model, factory, ComputeBackend.DirectML);

        foreach (Lang lang in Languages)
        {
            if (only.Length > 0 && !only.Split(',').Contains(lang.Iso)) continue;
            if (!Directory.Exists(LangDir(lang))) { output.WriteLine($"{lang.Iso}: no corpus"); continue; }

            List<Clip> clips = LoadCorpus(lang, maxClips);
            string path = TranscriptPath(lang);
            HashSet<string> done = [.. Resume<Hypothesis>(path).Select(h => h.File)];
            if (done.Count >= clips.Count) { output.WriteLine($"{lang.Iso}: done ({done.Count})"); continue; }

            Assert.True(NemotronLocales.TryGetSlot(lang.Locale, out long slot), $"no slot for {lang.Locale}");
            engine.SetLanguageSlot(slot);
            engine.WarmUp();

            // Same crash-resumability as E5: the DirectML session takes the test host down every few
            // hundred clips, and a clip the previous process died ON is banked empty rather than
            // retried forever.
            string inflight = Path.Combine(LangDir(lang), "inflight.txt");
            using var writer = new StreamWriter(path, append: true) { AutoFlush = true };
            if (File.Exists(inflight) && File.ReadAllText(inflight).Trim() is { Length: > 0 } stuck &&
                !done.Contains(stuck) && clips.FirstOrDefault(c => c.File == stuck) is { } victim)
            {
                output.WriteLine($"{lang.Iso}: CRASHED ON {stuck} — banking it empty");
                writer.WriteLine(JsonSerializer.Serialize(
                    new Hypothesis(victim.Split, victim.File, victim.Sentence, victim.Reference, "", victim.Seconds), Json));
                done.Add(stuck);
            }

            var sw = Stopwatch.StartNew();
            int n = done.Count;
            foreach (Clip c in clips.Where(c => !done.Contains(c.File)))
            {
                File.WriteAllText(inflight, c.File);
                float[] samples = WavAudio.ReadMono16k(Path.Combine(LangDir(lang), "audio", c.Split, c.File));
                string text;
                try
                {
                    string raw = engine.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult().Trim();
                    text = Jot.Text.TextPipeline.Clean(raw, lang.Locale, isNemotron: true);
                }
                catch (Exception ex)
                {
                    output.WriteLine($"FAILED {c.File}: {ex.Message}");
                    text = "";
                }
                writer.WriteLine(JsonSerializer.Serialize(new Hypothesis(
                    c.Split, c.File, c.Sentence, c.Reference, text,
                    samples.Length / (double)WavAudio.SampleRate), Json));
                if (++n % 100 == 0) output.WriteLine($"{lang.Iso} {n}/{clips.Count}  {sw.Elapsed:mm\\:ss}");
            }
            File.Delete(inflight);
            output.WriteLine($"{lang.Iso}: {n} clips in {sw.Elapsed:mm\\:ss}");
        }
    }

    /// <summary>Read a transcript file the transcribe stage may still be APPENDING to — so the report
    /// can be re-derived mid-run without waiting hours for the last language. A half-written final line
    /// is dropped, the same way <see cref="Resume{T}"/> drops one.</summary>
    private static List<string> ReadLinesShared(string path)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.EndsWith('}')) lines.Add(line);
        }
        return lines;
    }

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

    // MARK: - Stage 2 · score

    /// <summary>One arm's tallies. The decision number is <see cref="Fp1k"/>; everything else is either
    /// context (recall) or attribution (which guard actually did the work).</summary>
    private sealed class Tally
    {
        public int RefWords, Errors, Opportunities, Recovered, Tp, FpAbsent, FpOver;
        public int Blocked, BrakeStopped, CeilingStopped, OtherStopped;
        public int BrakeStoppedBad, CeilingStoppedBad, OtherStoppedBad;
        public int BrakeStoppedGood, Unplaced;

        public int Applied => Tp + FpAbsent + FpOver;
        public int FalseApplies => FpAbsent + FpOver;
        public double Fp1k => RefWords == 0 ? 0 : 1000.0 * FalseApplies / RefWords;
        public double Recall => Opportunities == 0 ? 0 : 100.0 * Recovered / Opportunities;
        public double Wer => RefWords == 0 ? 0 : 100.0 * Errors / RefWords;
        /// <summary>False applies the common-word brake stopped, per 1000 words — the weight the
        /// frequency list is carrying in this language.</summary>
        public double BrakeSaves1k => RefWords == 0 ? 0 : 1000.0 * BrakeStoppedBad / RefWords;
    }

    /// <summary>
    /// One language, one term list, one corrector tightness, through the REAL gate. <paramref
    /// name="brake"/> false swaps the frequency list for nothing at all, which is the counterfactual
    /// that says what the list is worth: the difference between the two arms' false applies is damage
    /// the brake — not the ceiling, not the corrector's own threshold — is preventing.
    /// </summary>
    private static Tally Score(
        List<Hypothesis> hyps, string[] termTexts, string? resource, double cap, bool brake,
        StringBuilder? rows = null, string label = "")
    {
        List<VocabularyTerm> terms = [.. termTexts.Select(t => new VocabularyTerm { Text = t })];
        var corrector = new VocabularyCorrector();
        var provider = EmbeddedCommonWordsProvider.Shared;
        IReadOnlySet<string> commonSet = provider.Words(brake ? resource : null);
        var tally = new Tally();

        foreach (Hypothesis h in hyps)
        {
            string[] refWords = VocabEvalScoring.Words(h.Reference);
            string[] hypWords = VocabEvalScoring.Words(h.Text);
            var missing = termTexts
                .Where(t => VocabEvalScoring.Contains(refWords, t) && !VocabEvalScoring.Contains(hypWords, t))
                .ToHashSet(StringComparer.Ordinal);

            IReadOnlyList<VocabularyGate.Detection> dets =
                corrector.Spot(h.Text, terms, h.Seconds, cap);
            VocabularyGate.Result r = VocabularyGate.ApplyFromDetections(
                h.Text, dets, h.Seconds, provider, brake ? resource : null);

            string[] outWords = VocabEvalScoring.Words(r.Text);
            tally.RefWords += refWords.Length;
            tally.Errors += VocabEvalScoring.WordErrors(refWords, outWords);
            tally.Opportunities += missing.Count;
            tally.Recovered += missing.Count(t => VocabEvalScoring.Contains(outWords, t));
            tally.Unplaced += Math.Max(0, dets.Count - r.Proposals.Count);

            // One term can legitimately be detected in two places in one transcript, so this is a
            // lookup, not a 1:1 map (ToDictionary would throw on the second hit).
            var aliases = dets.GroupBy(d => d.Term, StringComparer.Ordinal)
                              .ToDictionary(g => g.Key, g => g.First().Aliases, StringComparer.Ordinal);
            foreach (VocabularyGate.Proposal p in r.Proposals)
            {
                string verdict = VocabEvalScoring.Classify(refWords, p.Term, p.OriginalWord);
                if (p.Outcome == "applied")
                {
                    switch (verdict)
                    {
                        case "TP": tally.Tp++; break;
                        case "FP-absent": tally.FpAbsent++; break;
                        default: tally.FpOver++; break;
                    }
                    rows?.AppendLine($"{label}\tapplied\t{verdict}\t{p.OriginalWord}\t{p.Term}");
                    continue;
                }

                // WHICH guard stopped it, evaluated in the gate's own order (plausibility before the
                // common-word rule) with the gate's own predicates, so this is attribution and not a
                // second implementation of the gate.
                IReadOnlyList<string> al = aliases.GetValueOrDefault(p.Term) ?? [];
                string reason =
                    VocabularyGate.Gap(p.OriginalWord, p.Term, al) > VocabularyGate.PlausibilityCeiling ? "ceiling"
                    : VocabularyGate.IsCommonSpan(p.OriginalWord, commonSet) ? "brake"
                    : "other";
                tally.Blocked++;
                bool bad = verdict != "TP";
                switch (reason)
                {
                    case "ceiling": tally.CeilingStopped++; if (bad) tally.CeilingStoppedBad++; break;
                    case "brake":
                        tally.BrakeStopped++;
                        if (bad) tally.BrakeStoppedBad++; else tally.BrakeStoppedGood++;
                        break;
                    default: tally.OtherStopped++; if (bad) tally.OtherStoppedBad++; break;
                }
                rows?.AppendLine($"{label}\tblocked-{reason}\t{verdict}\t{p.OriginalWord}\t{p.Term}");
            }
        }
        return tally;
    }

    /// <summary>
    /// The 25 terms most likely to COLLIDE with an ordinary word of this language — the arm that
    /// actually stresses the brake, and the reason the shipped-25 arm alone would not settle E6.
    ///
    /// The shipped-25 arm is "the terms this engine got wrong most often", which in a language whose
    /// rare words happen to be long compounds selects terms nothing can collide with, and would then
    /// report a reassuring 0.00 that is a property of the corpus rather than of the brake. This picks,
    /// from the same mechanical pool, the terms sitting closest to a frequency-list word that really
    /// occurs in the corpus — the <c>lista</c>/<c>Lisa</c> shape, chosen by arithmetic, per language.
    ///
    /// Still real terms from real references, and still no hand-picking: the only thing that changes is
    /// WHICH mechanically-derived 25 are asked for.
    /// </summary>
    private static (string[] Terms, double MeanGap) Adversarial(
        IEnumerable<string> references, string[] pool, IReadOnlySet<string> common, int take)
    {
        // The words the brake is supposed to protect: everyday words that are actually SAID here.
        var protectedWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (string reference in references)
        {
            foreach (string raw in reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = CorrectionKey.Normalize(raw);
                if (w.Length >= 4 && w.All(char.IsLetter) && common.Contains(CorrectionKey.Lowercased(w)))
                    protectedWords.Add(w);
            }
        }
        string[] targets = [.. protectedWords];

        var scored = new List<(string Term, double Gap)>();
        foreach (string term in pool)
        {
            double best = double.MaxValue;
            foreach (string t in targets)
            {
                if (Math.Abs(t.Length - term.Length) > 2) continue;   // cannot beat the current best
                double g = VocabularyGate.Gap(t, term, []);
                if (g < best) best = g;
                if (best == 0) break;
            }
            if (best is > 0 and < double.MaxValue) scored.Add((term, best));
        }
        List<(string Term, double Gap)> picked =
            [.. scored.OrderBy(s => s.Gap).ThenBy(s => s.Term, StringComparer.Ordinal).Take(take)];
        // The mean gap says HOW adversarial this language's list could be made — a property of the
        // language, and the honest caveat on any cross-language comparison of this arm.
        return ([.. picked.Select(s => s.Term)], picked.Count == 0 ? 0 : picked.Average(s => s.Gap));
    }

    /// <summary>Fraction of the corpus a 24k frequency list actually covers — the thing §7.5 says the
    /// brake's strength IS. Tokens use the gate's own lookup fold; types are the distinct set.</summary>
    private static (double Token, double Type, int Types) Coverage(
        IEnumerable<string> texts, IReadOnlySet<string> common)
    {
        long tokens = 0, hits = 0;
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string text in texts)
        {
            foreach (string raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = CorrectionKey.Normalize(raw);
                if (w.Length == 0 || !w.All(c => char.IsLetter(c) || c == ' ')) continue;
                bool hit = common.Contains(CorrectionKey.Lowercased(w));
                tokens++;
                if (hit) hits++;
                seen[w] = hit;
            }
        }
        return (tokens == 0 ? 0 : 100.0 * hits / tokens,
                seen.Count == 0 ? 0 : 100.0 * seen.Values.Count(v => v) / seen.Count,
                seen.Count);
    }

    // MARK: - Stage 3 · report

    private void Report()
    {
        var sb = new StringBuilder();
        var summary = new StringBuilder(
            "lang\tclips\twords\twer\tterms\tfocusedOpp\trecall\tapplied\tfp\tfp1k\tbrakeSaves1k\t" +
            "brakeOffFp1k\tblkCeiling\tblkBrake\tblkOther\ttokenCov\ttypeCov\ttypes\tlen\tadvLen\t" +
            "advRecall\tadvApplied\tadvFp\tadvFp1k\tadvBrakeSaves1k\tadvBrakeOffFp1k\tadvFpOver\tfpOver\t" +
            "advGap\tbaseWer\tadvWer\n");
        var sweep = new StringBuilder(
            "lang\tcap\trecall\tapplied\tfp\tfp1k\tadvRecall\tadvApplied\tadvFp\tadvFp1k\n");
        var rows = new StringBuilder("lang\tarm\toutcome\tverdict\toriginal\tterm\n");

        foreach (Lang lang in new[] { English }.Concat(Languages))
        {
            string path = TranscriptPath(lang);
            if (!File.Exists(path)) { output.WriteLine($"{lang.Iso}: no transcripts"); continue; }
            List<Hypothesis> hyps = [.. ReadLinesShared(path)
                .Select(l => JsonSerializer.Deserialize<Hypothesis>(l)!)];
            if (hyps.Count == 0) continue;

            string? resource = lang.Resource ?? EmbeddedCommonWordsProvider.ResourceFor(lang.Locale);
            IReadOnlySet<string> common = EmbeddedCommonWordsProvider.Shared.Words(resource);
            string[] all = [.. BuildTerms(hyps.Select(h => h.Reference), common)];

            // The realistic list: the 25 terms this engine got wrong most often in this language.
            // Same size in every language, so the false-apply rates are comparable.
            var misses = all.ToDictionary(t => t, _ => 0);
            foreach (Hypothesis h in hyps)
            {
                string[] refWords = VocabEvalScoring.Words(h.Reference);
                string[] hypWords = VocabEvalScoring.Words(h.Text);
                foreach (string t in all)
                {
                    if (VocabEvalScoring.Contains(refWords, t) && !VocabEvalScoring.Contains(hypWords, t))
                        misses[t]++;
                }
            }
            string[] focused = [.. misses.Where(kv => kv.Value > 0)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(25).Select(kv => kv.Key)];

            (string[] adversarial, double advGap) = Adversarial(hyps.Select(h => h.Reference), all, common, 25);

            // Vocabulary OFF, so every arm can be reported as a NET effect on the transcript. A feature
            // that recovers 40 % of missed terms and leaves the text worse overall has not helped anyone.
            Tally baseline = Score(hyps, [], resource, VocabularyLimits.NoLimit, brake: true);
            Tally shipped = Score(hyps, focused, resource, VocabularyLimits.NoLimit, brake: true,
                                  rows, $"{lang.Iso}-focused");
            Tally noBrake = Score(hyps, focused, resource, VocabularyLimits.NoLimit, brake: false);
            Tally stress = Score(hyps, all, resource, VocabularyLimits.NoLimit, brake: true);
            Tally stressNoBrake = Score(hyps, all, resource, VocabularyLimits.NoLimit, brake: false);
            Tally adv = Score(hyps, adversarial, resource, VocabularyLimits.NoLimit, brake: true,
                              rows, $"{lang.Iso}-adversarial");
            Tally advNoBrake = Score(hyps, adversarial, resource, VocabularyLimits.NoLimit, brake: false);
            (double token, double type, int types) = Coverage(hyps.Select(h => h.Reference), common);
            double meanLen = focused.Length == 0 ? 0 : focused.Average(t => t.Length);
            double advLen = adversarial.Length == 0 ? 0 : adversarial.Average(t => t.Length);

            foreach (double cap in Caps)
            {
                Tally t = Score(hyps, focused, resource, cap, brake: true);
                Tally a = Score(hyps, adversarial, resource, cap, brake: true);
                sweep.AppendLine(string.Join('\t', lang.Iso, cap.ToString("F2", CultureInfo.InvariantCulture),
                    t.Recall.ToString("F1", CultureInfo.InvariantCulture), t.Applied, t.FalseApplies,
                    t.Fp1k.ToString("F2", CultureInfo.InvariantCulture),
                    a.Recall.ToString("F1", CultureInfo.InvariantCulture), a.Applied, a.FalseApplies,
                    a.Fp1k.ToString("F2", CultureInfo.InvariantCulture)));
            }

            summary.AppendLine(string.Join('\t', lang.Iso, hyps.Count, shipped.RefWords,
                shipped.Wer.ToString("F2", CultureInfo.InvariantCulture), all.Length, shipped.Opportunities,
                shipped.Recall.ToString("F1", CultureInfo.InvariantCulture), shipped.Applied,
                shipped.FalseApplies, shipped.Fp1k.ToString("F2", CultureInfo.InvariantCulture),
                shipped.BrakeSaves1k.ToString("F2", CultureInfo.InvariantCulture),
                noBrake.Fp1k.ToString("F2", CultureInfo.InvariantCulture),
                shipped.CeilingStopped, shipped.BrakeStopped, shipped.OtherStopped,
                token.ToString("F1", CultureInfo.InvariantCulture),
                type.ToString("F1", CultureInfo.InvariantCulture), types,
                meanLen.ToString("F1", CultureInfo.InvariantCulture),
                advLen.ToString("F1", CultureInfo.InvariantCulture),
                adv.Recall.ToString("F1", CultureInfo.InvariantCulture), adv.Applied, adv.FalseApplies,
                adv.Fp1k.ToString("F2", CultureInfo.InvariantCulture),
                adv.BrakeSaves1k.ToString("F2", CultureInfo.InvariantCulture),
                advNoBrake.Fp1k.ToString("F2", CultureInfo.InvariantCulture),
                adv.FpOver, shipped.FpOver, advGap.ToString("F3", CultureInfo.InvariantCulture),
                baseline.Wer.ToString("F2", CultureInfo.InvariantCulture),
                adv.Wer.ToString("F2", CultureInfo.InvariantCulture)));

            sb.AppendLine($"### {lang.Iso} ({lang.Fleurs} → {lang.Locale})  {hyps.Count} clips, " +
                          $"{shipped.RefWords} words, WER {baseline.Wer:F2} % → {shipped.Wer:F2} % " +
                          $"(adversarial {adv.Wer:F2} %)");
            sb.AppendLine($"  list coverage    token {token:F1} %   type {type:F1} %   ({types} types)");
            sb.AppendLine($"  terms            {all.Length} mechanical, focused-25 = {focused.Length}");
            sb.AppendLine($"  focused-25       opp {shipped.Opportunities}  recovered {shipped.Recovered} " +
                          $"({shipped.Recall:F1} %)  applied {shipped.Applied}  TP {shipped.Tp}  " +
                          $"FP {shipped.FalseApplies} (absent {shipped.FpAbsent}, overwrote {shipped.FpOver})  " +
                          $"FP/1k {shipped.Fp1k:F2}");
            sb.AppendLine($"  brake OFF        applied {noBrake.Applied}  FP {noBrake.FalseApplies}  " +
                          $"FP/1k {noBrake.Fp1k:F2}   → brake prevents {noBrake.FalseApplies - shipped.FalseApplies} " +
                          $"({shipped.BrakeSaves1k:F2}/1k by direct attribution)");
            sb.AppendLine($"  blocks           ceiling {shipped.CeilingStopped} (bad {shipped.CeilingStoppedBad})  " +
                          $"brake {shipped.BrakeStopped} (bad {shipped.BrakeStoppedBad}, cost-good {shipped.BrakeStoppedGood})  " +
                          $"other {shipped.OtherStopped}  unplaced {shipped.Unplaced}");
            sb.AppendLine($"  all-{all.Length,-5}       opp {stress.Opportunities}  recall {stress.Recall:F1} %  " +
                          $"FP/1k {stress.Fp1k:F2}   brake off {stressNoBrake.Fp1k:F2}");
            sb.AppendLine($"  adversarial-25   mean len {advLen:F1}  mean gap {advGap:F3}  opp {adv.Opportunities}  recall {adv.Recall:F1} %  " +
                          $"applied {adv.Applied}  FP {adv.FalseApplies} (overwrote {adv.FpOver})  " +
                          $"FP/1k {adv.Fp1k:F2}   brake off {advNoBrake.Fp1k:F2}   " +
                          $"brake stopped {adv.BrakeStoppedBad} bad ({adv.BrakeSaves1k:F2}/1k), " +
                          $"cost {adv.BrakeStoppedGood} good");
            sb.AppendLine($"  terms            focused mean len {meanLen:F1}   adversarial: " +
                          string.Join(", ", adversarial.Take(8)));
            sb.AppendLine();
            output.WriteLine($"{lang.Iso} done");
        }

        File.WriteAllText(Path.Combine(Out, "ml-report.txt"), sb.ToString());
        File.WriteAllText(Path.Combine(Out, "ml-summary.tsv"), summary.ToString());
        File.WriteAllText(Path.Combine(Out, "ml-sweep.tsv"), sweep.ToString());
        File.WriteAllText(Path.Combine(Out, "ml-rows.tsv"), rows.ToString());
        output.WriteLine(sb.ToString());
        output.WriteLine(summary.ToString());
        output.WriteLine(sweep.ToString());
    }
}
