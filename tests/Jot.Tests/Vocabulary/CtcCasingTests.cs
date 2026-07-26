using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Jot.Transcription;
using Jot.Transcription.Ctc;
using Jot.Transcription.Onnx;
using Jot.Vocabulary;
using Xunit;
using Xunit.Abstractions;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The CASING measurement for the SHIPPED English path — the fact that
/// <c>vocabulary-multilingual-research.md §13.7 / F3</c> says exists and that nothing in the English
/// suite had ever looked for.
///
/// Why it needed its own file: every English fact we shipped (<c>CtcSpotterTests</c>) typed its terms
/// canonically — `Nemotron`, `Okta`, `Claude Code` — so a spotter that only ever finds canonically-cased
/// terms passes all of them. The failure mode is silent zero recall on `okta`, which no assertion in the
/// repo could have caught.
///
/// Everything here is model-gated (<see cref="ModelFactAttribute"/>): CI downloads nothing and reports
/// real skips. The numbers are printed, not just asserted, because this is a calibration surface.
/// </summary>
public class CtcCasingTests(ITestOutputHelper output)
{
    private static string Wav(string name) => Path.Combine(ModelGate.ResolvedAudioDir!, name);

    /// <summary>The English terms the F3 probe used, so this fact and the Python probe are comparable.</summary>
    private static readonly string[] ProbeTerms =
    [
        "Nemotron", "Parakeet", "Okta", "Claude Code", "Sriram", "Zurich", "Sundarbans",
        "Birmingham", "Mendoza", "Kubernetes", "Anthropic", "WASAPI", "DirectML",
    ];

    // MARK: - Is the English checkpoint really a P&C model?

    /// <summary>
    /// Read straight off the shipped <c>tokens.txt</c>. If the head has uppercase-bearing pieces then the
    /// model can and does emit capitals, and a lowercase query is searching for a different sequence.
    /// </summary>
    [ModelFact("installed-ctc")]
    public void English_HeadHasUppercasePieces()
    {
        var tokens = CtcTokens.Load(new CtcModel().Tokens);
        var upper = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string p = tokens.Piece(i);
            if (p.Any(char.IsUpper)) upper.Add(p);
        }
        output.WriteLine($"uppercase-bearing pieces: {upper.Count}/{tokens.Count}");
        output.WriteLine("  " + string.Join(" ", upper.Take(40)));
        Assert.True(upper.Count > 0,
            "the English head has no uppercase pieces — F3 would be wrong and this whole file is moot");
    }

    /// <summary>Casing changes the id sequence. The English half of E2.</summary>
    [ModelFact("installed-ctc")]
    public void English_CasingChangesTheIdSequence()
    {
        var model = new CtcModel();
        var tk = CtcTokenizer.Load(model.Tokenizer);
        int differs = 0;
        foreach (string t in ProbeTerms)
        {
            int[] cased = [.. tk.Encode(t)];
            int[] lower = [.. tk.Encode(t.ToLowerInvariant())];
            if (!cased.SequenceEqual(lower)) differs++;
            output.WriteLine($"  {t,-14} cased=[{string.Join(",", cased)}]  " +
                             $"lower=[{string.Join(",", lower)}]  {(cased.SequenceEqual(lower) ? "same" : "DIFFERENT")}");
        }
        output.WriteLine($"casing changes the id sequence for {differs}/{ProbeTerms.Length} English terms");
        Assert.Equal(ProbeTerms.Length, differs);
    }

    /// <summary>What the model actually emits for the planted clip, greedily decoded. Prints the
    /// transcript so the capitalisation is visible rather than argued about.</summary>
    [ModelFact("installed-ctc", "audio:tts-terms.wav")]
    public void English_GreedyTranscriptIsCapitalised()
    {
        var model = new CtcModel();
        var tokens = CtcTokens.Load(model.Tokens);
        using var enc = new CtcEncoder(model.Graph, new OnnxSessionFactory());
        float[] fm = new CtcMelFrontend().ComputeFeatureMajor(
            WavAudio.ReadMono16k(Wav("tts-terms.wav")), out int frames);
        float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);
        List<CtcToken> emitted = CtcGreedyDecoder.Decode(lp, outFrames, vocab, tokens.BlankId);
        string text = tokens.Decode([.. emitted.Select(t => t.Id)]);
        output.WriteLine(text);
        Assert.Contains(text, c => char.IsUpper(c));
    }

    // MARK: - THE measurement: recall per casing, on real audio

    /// <summary>
    /// The table. For every surface form, one single-form query against the real log-probs, best score
    /// reported. A form scoring below <see cref="CtcWordSpotter.DefaultMinScore"/> would be a term the
    /// user typed and the spotter can never find — with no error and no log line.
    ///
    /// Note which spellings the CLIP carries: tts-terms.wav says "Sri Ram", "Octa" and "Claud code",
    /// so those rows measure the ALIAS surface, which is exactly what the shipped feature searches for.
    /// </summary>
    [ModelFact("installed-ctc", "audio:tts-terms.wav")]
    public void English_RecallCollapsesWhenTheTermIsTypedInTheWrongCase()
    {
        var model = new CtcModel();
        var tokens = CtcTokens.Load(model.Tokens);
        var tk = CtcTokenizer.Load(model.Tokenizer);
        using var enc = new CtcEncoder(model.Graph, new OnnxSessionFactory());

        (float[] speech, _) = SilenceTrim.Trim(WavAudio.ReadMono16k(Wav("tts-terms.wav")), 16_000);
        float[] fm = new CtcMelFrontend().ComputeFeatureMajor(speech, out int frames);
        float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);

        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds, minScore: -30f);

        // Canonical spellings the CLIP actually carries. Okta/Claude Code are said as "Octa"/"Claud
        // code", so their canonical rows are expected to be weak for a reason that is NOT casing —
        // both surfaces are listed so the two effects stay separable.
        string[] canonical = ["Nemotron", "Parakeet", "Thursday", "Octa", "Claud code", "Sri Ram",
                              "Okta", "Claude Code"];

        output.WriteLine($"{"surface",-16}{"as typed",10}{"lower",10}{"UPPER",10}{"Title",10}   verdict");
        int lostToLower = 0, keptCanonical = 0;
        foreach (string term in canonical)
        {
            double c = Best(dp, lp, outFrames, vocab, tk, term);
            double l = Best(dp, lp, outFrames, vocab, tk, term.ToLowerInvariant());
            double u = Best(dp, lp, outFrames, vocab, tk, term.ToUpperInvariant());
            double t = Best(dp, lp, outFrames, vocab, tk, TitleCase(term));

            bool okC = c >= CtcWordSpotter.DefaultMinScore, okL = l >= CtcWordSpotter.DefaultMinScore;
            if (okC) keptCanonical++;
            if (okC && !okL) lostToLower++;
            output.WriteLine($"{term,-16}{F(c),10}{F(l),10}{F(u),10}{F(t),10}   " +
                             $"{(okC ? "hit" : "miss")}/{(okL ? "hit" : "MISS")}");
        }

        output.WriteLine($"at the shipped threshold {CtcWordSpotter.DefaultMinScore:F1}: " +
                         $"{keptCanonical}/{canonical.Length} surfaces found as typed; " +
                         $"{lostToLower} of those are LOST when the same term is typed lowercase");

        // The bug, stated as an assertion so a future model change re-raises it rather than quietly
        // making this file decorative.
        Assert.True(lostToLower > 0,
            "lowercasing cost nothing — F3 does not reproduce on this checkpoint/clip");
    }

    /// <summary>
    /// The same question at CORPUS scale, on REAL human speech instead of six TTS plants.
    ///
    /// The corpus builds itself, which is the only honest way to get one here: greedy-decode the clip,
    /// keep the long alphabetic words, and treat each as a term the user might have added — spelled
    /// exactly as the model emitted it. That makes the emitted casing KNOWN per word, so "typed in the
    /// wrong case" is a controlled variable rather than a guess, and it is drawn from the same
    /// distribution of sentence-initial capitals and lowercase mid-sentence words a real dictation has.
    ///
    /// Prints the recall table the fix is judged against. Asserts only the direction (wrong casing is
    /// strictly worse), because the magnitude is exactly what this fact exists to report.
    /// </summary>
    [ModelFact("installed-ctc", "audio:real-180s.wav")]
    public void English_RecallTableAcrossCasingsOnRealSpeech()
    {
        var model = new CtcModel();
        var tokens = CtcTokens.Load(model.Tokens);
        var tk = CtcTokenizer.Load(model.Tokenizer);
        using var enc = new CtcEncoder(model.Graph, new OnnxSessionFactory());
        var fe = new CtcMelFrontend();
        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds, minScore: -30f);

        var emitted = new List<double>();
        var lower = new List<double>();
        var upper = new List<double>();
        var title = new List<double>();
        var bestOf = new List<double>();
        int capitalised = 0, words = 0;

        // real-180s is the superset of the real-10/40/60/90 prefixes, so listing those too would count
        // the same acoustic instance repeatedly and inflate every row equally.
        foreach (string clip in new[] { "real-180s.wav", "public-0.wav", "tts-terms.wav" })
        {
            if (!File.Exists(Wav(clip))) continue;
            (float[] speech, _) = SilenceTrim.Trim(WavAudio.ReadMono16k(Wav(clip)), 16_000);
            float[] fm = fe.ComputeFeatureMajor(speech, out int frames);
            float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);

            string text = tokens.Decode(
                [.. CtcGreedyDecoder.Decode(lp, outFrames, vocab, tokens.BlankId).Select(t => t.Id)]);

            // Long words only: a 2–4 char word is not a vocabulary term, and short sequences align
            // spuriously often enough to drown the signal this fact is measuring.
            var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Select(w => new string([.. w.Where(char.IsLetter)]))
                            .Where(w => w.Length >= 6)
                            .Distinct(StringComparer.Ordinal)
                            .Take(80)
                            .ToList();

            foreach (string w in terms)
            {
                double e = Best(dp, lp, outFrames, vocab, tk, w);
                if (double.IsNegativeInfinity(e)) continue;
                double l = Best(dp, lp, outFrames, vocab, tk, w.ToLowerInvariant());
                double u = Best(dp, lp, outFrames, vocab, tk, w.ToUpperInvariant());
                double t = Best(dp, lp, outFrames, vocab, tk, TitleCase(w));

                words++;
                if (char.IsUpper(w[0])) capitalised++;
                emitted.Add(e); lower.Add(l); upper.Add(u); title.Add(t);
                bestOf.Add(Math.Max(Math.Max(e, l), Math.Max(u, t)));
            }
        }

        Assert.True(words >= 20, $"corpus too small ({words} words) to say anything");
        output.WriteLine($"corpus: {words} words from real speech, {capitalised} emitted capitalised");
        output.WriteLine($"{"typed as",-24}{"recall @-3.0",14}{"median",10}{"p10",10}{"worst",10}");
        Row("as the model emitted it", emitted);
        Row("all lowercase", lower);
        Row("ALL UPPERCASE", upper);
        Row("Title Case", title);
        Row("best of all four", bestOf);

        // The point, as an assertion: typing the wrong case costs real recall on real speech.
        Assert.True(Recall(upper) < Recall(emitted), "uppercase cost nothing — F3 does not reproduce");
        Assert.True(Recall(bestOf) >= Recall(emitted), "searching more casings lost a term it used to find");

        void Row(string label, List<double> xs)
        {
            var s = xs.Where(x => !double.IsNegativeInfinity(x)).OrderBy(x => x).ToList();
            output.WriteLine($"{label,-24}{Recall(xs) + "/" + xs.Count,14}" +
                             $"{F(s.Count == 0 ? double.NegativeInfinity : s[s.Count / 2]),10}" +
                             $"{F(s.Count == 0 ? double.NegativeInfinity : s[s.Count / 10]),10}" +
                             $"{F(s.Count == 0 ? double.NegativeInfinity : s[0]),10}");
        }

        static int Recall(List<double> xs) => xs.Count(x => x >= CtcWordSpotter.DefaultMinScore);
    }

    /// <summary>
    /// The price of searching more casings: FALSE POSITIVES. The threshold is derived from a measured
    /// gap (<see cref="CtcWordSpotter.DefaultMinScore"/>), and four queries per term is four chances to
    /// land in it — so the decoy band is re-measured with the expansion switched on rather than
    /// asserted to be unchanged.
    /// </summary>
    [ModelFact("installed-ctc", "audio:tts-terms.wav")]
    public void English_CasingVariantsDoNotManufactureFalsePositives()
    {
        var model = new CtcModel();
        var tokens = CtcTokens.Load(model.Tokens);
        var tk = CtcTokenizer.Load(model.Tokenizer);
        using var enc = new CtcEncoder(model.Graph, new OnnxSessionFactory());
        var fe = new CtcMelFrontend();
        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds, minScore: -30f);

        string[] terms = ["Nemotron", "Parakeet", "Sriram", "Okta", "Claude Code", "Thursday"];
        var plain = new List<CtcSpotQuery>();
        var expanded = new List<CtcSpotQuery>();
        foreach (string t in terms)
        {
            plain.Add(new CtcSpotQuery(t, [], [.. tk.Encode(t)]));
            foreach (string f in CtcSpotForms.Expand(t))
            {
                int[] ids = [.. tk.Encode(f)];
                if (ids.Length > 0) expanded.Add(new CtcSpotQuery(t, [], ids));
            }
        }
        output.WriteLine($"queries: {plain.Count} plain -> {expanded.Count} expanded");

        double worstPlain = double.NegativeInfinity, worstExpanded = double.NegativeInfinity;
        int fpPlain = 0, fpExpanded = 0, n = 0;
        foreach (string clip in new[] { "public-0.wav", "real-40s.wav", "real-90s.wav", "seg-22-60.wav" })
        {
            if (!File.Exists(Wav(clip))) continue;
            (float[] speech, _) = SilenceTrim.Trim(WavAudio.ReadMono16k(Wav(clip)), 16_000);
            float[] fm = fe.ComputeFeatureMajor(speech, out int frames);
            float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);

            foreach (var d in dp.Spot(lp, outFrames, vocab, plain))
            {
                worstPlain = Math.Max(worstPlain, d.Score);
                if (d.Score >= CtcWordSpotter.DefaultMinScore) fpPlain++;
            }
            foreach (var d in dp.Spot(lp, outFrames, vocab, expanded))
            {
                worstExpanded = Math.Max(worstExpanded, d.Score);
                if (d.Score >= CtcWordSpotter.DefaultMinScore) fpExpanded++;
            }
            n++;
        }

        Assert.True(n > 0, "no decoy clips");
        output.WriteLine($"decoy clips: {n}. strongest false candidate: " +
                         $"plain {F(worstPlain)} -> expanded {F(worstExpanded)}");
        output.WriteLine($"false positives at {CtcWordSpotter.DefaultMinScore:F1}: " +
                         $"plain {fpPlain} -> expanded {fpExpanded}");
        Assert.Equal(0, fpExpanded);
    }

    /// <summary>
    /// The END-TO-END shipped path, which is what the user experiences: the same term typed four ways,
    /// through <see cref="CtcVocabularySpotter"/> exactly as <c>VocabularyRunner</c> calls it.
    ///
    /// This is the fact that must go from red to green with the variant fix. It deliberately uses the
    /// SHIPPED threshold: a fix that works by lowering the acceptance floor is not a fix, it is a
    /// false-positive generator.
    /// </summary>
    [ModelFact("installed-ctc", "audio:tts-terms.wav")]
    public void English_ShippedSpotterFindsATermWhateverCaseTheUserTyped()
    {
        float[] pcm = WavAudio.ReadMono16k(Wav("tts-terms.wav"));
        using var s = new CtcVocabularySpotter(new CtcModel(), new OnnxSessionFactory());

        // "Okta"/"Claude Code" carry the aliases the clip actually says, so this exercises the variant
        // logic COMPOSED with the alias mechanism, not instead of it.
        (string Text, string[] Aliases)[] cases =
        [
            ("Nemotron", []), ("nemotron", []), ("NEMOTRON", []),
            ("Parakeet", []), ("parakeet", []),
            ("Okta", ["Octa"]), ("okta", ["octa"]), ("OKTA", ["OCTA"]),
            ("Claude Code", ["Claud code"]), ("claude code", ["claud code"]),
        ];

        var failures = new List<string>();
        foreach ((string text, string[] aliases) in cases)
        {
            var term = new VocabularyTerm { Text = text, Aliases = [.. aliases] };
            var hits = s.Spot(pcm, 16_000, [term], default);
            double best = hits.Count == 0 ? double.NegativeInfinity : hits.Max(h => h.Score);
            output.WriteLine($"  {text,-14} -> {hits.Count} detection(s), best {F(best)}" +
                             (hits.Count > 0 ? $", reported as \"{hits[0].Term}\"" : ""));
            if (hits.Count == 0) failures.Add(text);
            // The gate places the text the USER typed. A variant that reports its own spelling would
            // paste "nemotron" into a sentence the user wanted "Nemotron" in.
            foreach (var h in hits) Assert.Equal(text, h.Term);
        }

        Assert.True(failures.Count == 0,
            "never spotted, silently: " + string.Join(", ", failures));
    }

    /// <summary>
    /// The cost side of the variant fix. The DP measured 19.2 ms for 200 terms at 60 s; expanding each
    /// term into casing variants multiplies the QUERY count, so the multiplier is measured here rather
    /// than assumed to be affordable.
    /// </summary>
    [ModelFact("installed-ctc", "audio:real-60s.wav")]
    public void English_CasingVariantsCostIsBounded()
    {
        var model = new CtcModel();
        var tokens = CtcTokens.Load(model.Tokens);
        var tk = CtcTokenizer.Load(model.Tokenizer);
        using var enc = new CtcEncoder(model.Graph, new OnnxSessionFactory());
        float[] fm = new CtcMelFrontend().ComputeFeatureMajor(
            WavAudio.ReadMono16k(Wav("real-60s.wav")), out int frames);
        float[] lp = enc.Run(fm, frames, out int outFrames, out int vocab);

        var dp = new CtcWordSpotter(tokens.BlankId, CtcEncoder.FrameSeconds);
        var terms = Enumerable.Range(0, 200)
            .Select(i => new VocabularyTerm { Text = "Nemotron" + i % 7 })
            .ToList();

        foreach (bool variants in new[] { false, true })
        {
            var queries = new List<CtcSpotQuery>();
            foreach (VocabularyTerm t in terms)
            {
                IEnumerable<string> forms = variants ? CtcSpotForms.Expand(t.Text) : [t.Text];
                foreach (string f in forms)
                {
                    int[] ids = [.. tk.Encode(f)];
                    if (ids.Length > 0) queries.Add(new CtcSpotQuery(t.Text, [], ids));
                }
            }

            var sw = Stopwatch.StartNew();
            for (int r = 0; r < 5; r++) dp.Spot(lp, outFrames, vocab, queries);
            sw.Stop();
            output.WriteLine($"  {(variants ? "with" : "without"),-8} casing variants: " +
                             $"{queries.Count,5} queries over {outFrames} frames -> " +
                             $"{sw.Elapsed.TotalMilliseconds / 5:F1} ms");
        }
    }

    // MARK: - The variant generator itself (no model, so CI runs these)

    [Fact]
    public void Expand_AlwaysLeadsWithTheFormTheUserTyped()
    {
        // The as-typed form is the one the shipped build searched for. If it ever stops being emitted
        // first, the MaxQueriesPerTerm budget could truncate the only query that used to work.
        foreach (string typed in new[] { "Okta", "okta", "OKTA", "claude code", "Claude Code", "wAsApI" })
            Assert.Equal(typed, CtcSpotForms.Expand(typed)[0]);
    }

    [Fact]
    public void Expand_CoversTheCasingsTheModelActuallyEmits()
    {
        Assert.Equal(["okta", "Okta", "OKTA"], CtcSpotForms.Expand("okta"));
        Assert.Equal(["OKTA", "okta", "Okta"], CtcSpotForms.Expand("OKTA"));
        Assert.Equal(["Nemotron", "nemotron", "NEMOTRON"], CtcSpotForms.Expand("Nemotron"));

        // Multi-word picks up sentence case as well — "Claude code" is how a P&C model renders a
        // product name whose second word is not itself a proper noun.
        Assert.Equal(["claude code", "Claude Code", "CLAUDE CODE", "Claude code"],
                     CtcSpotForms.Expand("claude code"));
    }

    [Fact]
    public void Expand_IsBoundedAndDeduplicated()
    {
        foreach (string t in new[] { "a b c d", "Okta", "OKTA okta Okta", "x", "ALLCAPS TWO WORDS" })
        {
            IReadOnlyList<string> forms = CtcSpotForms.Expand(t);
            Assert.True(forms.Count <= CtcSpotForms.MaxFormsPerSurface,
                        $"{t} expanded to {forms.Count} forms");
            Assert.Equal(forms.Count, forms.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void Expand_OfNothingIsNothing()
    {
        Assert.Empty(CtcSpotForms.Expand(null));
        Assert.Empty(CtcSpotForms.Expand(""));
        Assert.Empty(CtcSpotForms.Expand("   "));
    }

    [Fact]
    public void Expand_CapitalisesTheFirstLetter_NotTheFirstCharacter()
    {
        // A term list is exactly where "(okta)" and "'tis" turn up; blindly upper-casing index 0 would
        // leave those forms identical to the lowercase one and waste the variant.
        Assert.Contains("(Okta)", CtcSpotForms.Expand("(okta)"));
        Assert.Contains("'Tis", CtcSpotForms.Expand("'tis"));
    }

    // MARK: - helpers

    private static double Best(CtcWordSpotter dp, float[] lp, int frames, int vocab,
                               CtcTokenizer tk, string form)
    {
        int[] ids = [.. tk.Encode(form)];
        if (ids.Length == 0) return double.NegativeInfinity;
        var hits = dp.Spot(lp, frames, vocab, [new CtcSpotQuery(form, [], ids)]);
        return hits.Count == 0 ? double.NegativeInfinity : hits.Max(h => h.Score);
    }

    private static string F(double x) =>
        double.IsNegativeInfinity(x) ? "--" : x.ToString("F3", CultureInfo.InvariantCulture);

    private static string TitleCase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
