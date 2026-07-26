using System.Buffers;
using System.Globalization;
using System.Text;

namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/VocabularyGate.swift` (pinned commit 5326460).
// Behaviour is locked by the shared golden fixtures replayed in Jot.Tests — if you change logic
// here and those fail, Windows has diverged from Mac/iOS and that is the bug, not the test.
//
// PORTING NOTE — THE STRING MODEL IS THE WHOLE RISK IN THIS FILE. Swift's String is a sequence
// of Characters (extended grapheme clusters) over Unicode SCALARS; .NET's is UTF-16 code units.
// The two only coincide on ASCII, which is exactly what the golden fixtures cover — so every
// place the Swift source tests a Character, counts a String, or filters scalars, this port does
// the same explicitly (see the "Swift string model" helpers at the bottom). Indices INTO the
// text stay UTF-16 (that is what .NET slices with); only the emitted provenance offsets are
// converted to grapheme counts, because those are the numbers Mac/iOS persist.

/// <summary>
/// The gate. A safety filter over proposed vocabulary replacements so a custom term can
/// *never silently overwrite a word the transcriber already got right.*
///
/// A CTC word-spotter proposes "replace word X with term Y when Y's acoustic score beats X".
/// That swap has no brake — it fires even on a 0.998-confidence correct word. That is the
/// shipped over-correction bug on macOS: adding the term "Jamy" turned every "name" into "Jamy".
/// This gate is the brake:
///   1. Plausibility — the heard word must be an acoustic cousin of the term/alias.
///   2. Confidence ceiling — never auto-correct a word the transcriber was very sure about.
///   3. Common-word guard — never overwrite an everyday word unless the override is earned.
///   4. Earned override — a shaky word, or a term winning by a large margin, may still correct.
/// Multi-word terms ("Claude Code") are precise and self-gating, so they pass after plausibility.
///
/// Thresholds are START values inherited from the shipping Apple builds; a Windows calibration
/// pass is a pre-enable task. Keep the master vocabulary toggle off until then.
/// </summary>
public static class VocabularyGate
{
    /// <summary>A word above this confidence is never auto-corrected unless the term wins by a
    /// large margin. The 0.998-"name" protector.</summary>
    public const float ConfidenceCeiling = 0.95f;

    /// <summary>Below this confidence a word is "unsure" enough to be override-eligible.</summary>
    public const float LowConfidence = 0.85f;

    /// <summary>Margin above which a correction counts as "earned" even against a confident or
    /// common word.</summary>
    public const float EarnedMargin = 4.0f;

    /// <summary>Max normalized edit distance (Levenshtein over letter-skeletons / longer length)
    /// for the heard word to count as an acoustic cousin. Measured on real pairs:
    /// shriram→sriram 0.14, cloud→claude 0.33, jamie→jamy 0.40 (pass);
    /// vikram→sriram 0.50, ramanathan→ramaa 0.50, name→jamy 0.50 (block).</summary>
    public const double PlausibilityCeiling = 0.45;

    private static readonly IReadOnlyDictionary<string, string> NoMeta = new Dictionary<string, string>();

    /// <summary>An alternate candidate term for a proposal's span (the 3-option ask).
    /// <c>Find</c> is the EXACT in-text string it would replace.</summary>
    public readonly record struct Alternate(string Term, string Find);

    /// <summary>One proposal the spotter surfaced, with the gate's verdict.</summary>
    public sealed record Proposal(
        string OriginalWord,
        string Term,
        string Decision,          // "APPLY" | "BLOCK" | "OVERRIDE"
        string Outcome,           // "applied" (text became Term) | "kept" (text left Original)
        float Confidence,
        float Margin,
        bool Unsure,
        bool AskCandidate,
        int OccurrenceIndex,      // display-only FIFO arrival index — NOT an identity key
        // Offsets are GRAPHEME-CLUSTER counts (Swift's String.count), not UTF-16 indices: they
        // are persisted and compared against records written by Mac/iOS. Do not slice .NET
        // strings with them.
        int OriginalStart,        // stable identity: offset of the span in the ORIGINAL transcript
        int OriginalLength,
        int PublishedStart,       // span in the GATE-OUTPUT text (provenance live anchor)
        int PublishedLength,
        IReadOnlyList<Alternate> Alternates,
        string? Shape);           // "merge" = split-word class ("sri ram" → "Sriram"), else null

    public sealed record Result(
        string Text,
        int Applied,
        IReadOnlyList<string> Blocked,   // originalWords that were protected
        IReadOnlyList<Proposal> Proposals);

    /// <summary>One acoustically-spotted vocabulary term and where it lives in the audio.</summary>
    public readonly record struct Detection(
        string Term,
        IReadOnlyList<string> Aliases,
        float Score,
        double StartTime,
        double EndTime);

    private readonly record struct CharRange(int Start, int End)
    {
        public int Length => End - Start;
    }

    private readonly record struct Verdict(
        bool Pass, float Confidence, float Margin, string Label, bool Unsure, bool AskCandidate);

    // MARK: - Rescore-driven gate

    /// <summary>
    /// Apply the gate to a rescorer output. Each proposed replacement is re-checked and either
    /// kept or reverted. Reconstructs from <paramref name="originalTranscript"/> so a blocked
    /// replacement cleanly leaves the original word.
    ///
    /// Replacements are applied in POSITIONAL order — the input is not left-to-right (rescorers
    /// sort by span length / similarity), so a forward-only pass would silently drop edits.
    /// </summary>
    public static Result Apply(
        string originalTranscript,
        RescoreOutput output,
        IReadOnlyList<TokenTiming> tokenTimings,
        ICommonWordsProvider commonWords,
        IDiagnosticsSink? diagnostics = null,
        IReadOnlyList<OverrideEntry>? overrides = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? termAliases = null,
        string? commonWordsResource = "common-words",
        IReadOnlyList<string>? allTerms = null)
    {
        diagnostics ??= NoopDiagnosticsSink.Instance;
        overrides ??= [];
        termAliases ??= new Dictionary<string, IReadOnlyList<string>>();
        allTerms ??= [];

        if (!output.WasModified || output.Replacements.Count == 0)
            return new Result(output.Text, 0, [], []);

        Dictionary<string, float> wordConfidence = PerWordMinConfidence(tokenTimings);
        // Resolve the everyday-word set ONCE (seam 2). A null resource yields an empty set, so
        // the common-word guard is simply inert.
        IReadOnlySet<string> commonWordSet = commonWords.Words(commonWordsResource);

        var occurrence = new Dictionary<string, int>();
        var items = new List<Item>();

        foreach (RescoreProposal r in output.Replacements)
        {
            if (!r.ShouldReplace) continue;

            string key = CorrectionKey.Lowercased(r.OriginalWord);
            int n = occurrence.GetValueOrDefault(key, 0);
            CharRange? found = NthWholeWordRange(r.OriginalWord, originalTranscript, n);
            if (found is null) continue;
            CharRange range = found.Value;
            occurrence[key] = n + 1;

            // Alignment window: a multi-word term must align to ONE unique edge-touching window
            // inside its span; extra span words survive; anything ambiguous blocks. Runs BEFORE
            // Decide so identity/learning use the aligned span.
            bool alignmentBlocked = false;
            string effectiveOriginal = r.OriginalWord;
            if (r.ReplacementWord is { } term0)
            {
                string[] termWords = SplitWords(term0);
                string[] spanWords = SplitWords(r.OriginalWord);
                if (termWords.Length >= 2 && spanWords.Length >= termWords.Length)
                {
                    (bool unique, int wordIndex) = AlignmentWindow(termWords, spanWords);
                    if (unique)
                    {
                        if (spanWords.Length > termWords.Length &&
                            WordSubrange(range, wordIndex, termWords.Length, originalTranscript) is { } sub)
                        {
                            range = sub;
                            effectiveOriginal = Slice(originalTranscript, sub);
                            diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                                $"align-narrowed {r.OriginalWord} → {effectiveOriginal}",
                                new Dictionary<string, string> { ["term"] = term0 });
                        }
                    }
                    else
                    {
                        alignmentBlocked = true;
                    }
                }
            }

            // Repeated-occurrence guard: with no span position from the engine, the
            // k-th-arrival→k-th-occurrence mapping can hit the WRONG occurrence when a word
            // repeats. Safe only when the word occurs once, or every occurrence has a proposal
            // with the SAME replacement (then order cannot change the text).
            if (!alignmentBlocked && NthWholeWordRange(r.OriginalWord, originalTranscript, 1) is not null)
            {
                int occCount = 2;
                while (NthWholeWordRange(r.OriginalWord, originalTranscript, occCount) is not null) occCount++;
                var siblings = output.Replacements
                    .Where(x => x.ShouldReplace && CorrectionKey.Lowercased(x.OriginalWord) == key).ToList();
                bool sameTerm = siblings
                    .Select(x => CorrectionKey.Lowercased(x.ReplacementWord ?? ""))
                    .Distinct().Count() == 1;
                if (siblings.Count != occCount || !sameTerm)
                {
                    alignmentBlocked = true;
                    diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                        $"occurrence-ambiguous {r.OriginalWord} → {r.ReplacementWord ?? "—"}",
                        new Dictionary<string, string>
                        {
                            ["occurrences"] = occCount.ToString(CultureInfo.InvariantCulture),
                            ["proposals"] = siblings.Count.ToString(CultureInfo.InvariantCulture),
                        });
                }
            }

            // Dedup guard: saying "Claude code", heard as "clawed code", applied the two-word term
            // over just "clawed" → "Claude Code code". When a multi-word term covers FEWER words
            // than the term has and the term's trailing words duplicate what follows, widen the
            // span to absorb them. Runs BEFORE Decide: the widened span IS the identity, so a
            // learned demotion keys on "clawed code" and a revert blocks the next occurrence.
            if (r.ReplacementWord is { } term1 && !alignmentBlocked &&
                AbsorbTrailingDuplicates(term1, effectiveOriginal, range, originalTranscript) is { } widened)
            {
                range = widened;
                effectiveOriginal = Slice(originalTranscript, widened);
                diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                    $"dedup-absorbed {r.OriginalWord} → {effectiveOriginal}",
                    new Dictionary<string, string> { ["term"] = term1 });
            }

            // Decide consumes the EFFECTIVE identity (aligned-narrowed or dedup-widened span),
            // not the raw engine span: plausibility/confidence/common-word/learned lookups must
            // all see the same words the replacement actually touches.
            Verdict d = Decide(
                r, effectiveOriginal, wordConfidence, overrides,
                termAliases.GetValueOrDefault(CorrectionKey.Lowercased(r.ReplacementWord ?? "")) ?? [],
                commonWordSet);
            if (alignmentBlocked && d.Pass)
            {
                // Force-block a proposal whose span identity is unsafe; it stays visible for
                // review. A force-blocked unsafe span is never a live-ask candidate.
                d = d with { Pass = false, Label = "BLOCK", AskCandidate = false };
            }

            diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                $"{r.OriginalWord} → {r.ReplacementWord ?? "—"}",
                new Dictionary<string, string>
                {
                    ["decision"] = d.Label,
                    ["conf"] = d.Confidence.ToString("F3", CultureInfo.InvariantCulture),
                    ["margin"] = d.Margin.ToString("F2", CultureInfo.InvariantCulture),
                    // netMargin approximates "did the term win on acoustic merit alone?" by
                    // subtracting the engine's context-biasing head-start (cbw ≈ 3.0).
                    ["netMargin"] = (d.Margin - 3.0f).ToString("F2", CultureInfo.InvariantCulture),
                });

            items.Add(new Item(
                R: r,
                D: d,
                Range: range,
                OriginalWord: effectiveOriginal,
                OccurrenceIndex: n,
                PublishedText: d.Pass ? (r.ReplacementWord ?? r.OriginalWord) : Slice(originalTranscript, range)));
        }

        // Positional order; on an equal start (possible after alignment narrowing) the LONGER
        // span wins the slot — it is the more specific match.
        items.Sort((a, b) =>
            a.Range.Start != b.Range.Start
                ? a.Range.Start.CompareTo(b.Range.Start)
                : b.Range.End.CompareTo(a.Range.End));

        var result = new StringBuilder();
        int cursor = 0;
        int applied = 0;
        var blocked = new List<string>();
        var proposals = new List<Proposal>();

        foreach (Item item in items)
        {
            // Overlap guard — when two proposals claim overlapping spans (terms "Claude" AND
            // "Claude Code" matching the same audio), the leftmost-starting one wins. Logged
            // because a silent drop made "I added both but only one works" untraceable.
            if (item.Range.Start < cursor)
            {
                diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                    $"overlap-dropped {item.OriginalWord} → {item.R.ReplacementWord ?? "—"}",
                    new Dictionary<string, string>
                    {
                        ["decision"] = item.D.Label,
                        ["margin"] = item.D.Margin.ToString("F2", CultureInfo.InvariantCulture),
                    });
                continue;
            }

            result.Append(originalTranscript, cursor, item.Range.Start - cursor);
            // Swift reads `result.count` here — grapheme clusters of everything published so far.
            // Counted on the WHOLE prefix, not summed per segment, so a "\r" ending the prefix and
            // a "\n" opening this span fuse into one cluster exactly as they do on Mac.
            int publishedStart = GraphemeCount(result.ToString());
            result.Append(item.PublishedText);

            // Longer vocab siblings of the winning term whose extension matches what follows
            // ("Claude" won but "Claude Code" fits the audio + next word). Greedy merges prefer
            // the shorter span and drop the longer candidate upstream, so re-derive it here.
            IReadOnlyList<Alternate> alternates = ExtensionAlternates(
                item.R.ReplacementWord, item.PublishedText, item.Range.End, originalTranscript, allTerms);

            // Merge-shape classification: span words > term words AND the concatenated span
            // EXACTLY equals the normalized term or an alias — the split-word class.
            string? shape = null;
            if (item.R.ReplacementWord is { } shapeTerm)
            {
                string[] spanWords = SplitWords(item.OriginalWord);
                string[] termWords = SplitWords(shapeTerm);
                if (spanWords.Length > termWords.Length)
                {
                    Rune[] concat = Skeleton(string.Concat(spanWords));
                    var candidates = new List<string> { shapeTerm };
                    if (termAliases.GetValueOrDefault(CorrectionKey.Lowercased(shapeTerm)) is { } al) candidates.AddRange(al);
                    if (candidates.Any(c => Skeleton(c).AsSpan().SequenceEqual(concat))) shape = "merge";
                }
            }

            proposals.Add(new Proposal(
                // Effective span text (dedup-widened when the guard absorbed a following
                // duplicate) — reverts and ask chips must restore/show the FULL replaced span.
                OriginalWord: item.OriginalWord,
                Term: item.R.ReplacementWord ?? item.R.OriginalWord,
                Decision: item.D.Label,
                Outcome: item.D.Pass ? "applied" : "kept",
                Confidence: item.D.Confidence,
                Margin: item.D.Margin,
                Unsure: item.D.Unsure,
                AskCandidate: item.D.AskCandidate,
                OccurrenceIndex: item.OccurrenceIndex,
                OriginalStart: GraphemeCount(originalTranscript.AsSpan(0, item.Range.Start)),
                OriginalLength: GraphemeCount(originalTranscript.AsSpan(item.Range.Start, item.Range.Length)),
                PublishedStart: publishedStart,
                PublishedLength: GraphemeCount(item.PublishedText),
                Alternates: alternates,
                Shape: shape));

            if (item.D.Pass) applied++;
            else blocked.Add(Slice(originalTranscript, item.Range));
            cursor = item.Range.End;
        }

        result.Append(originalTranscript, cursor, originalTranscript.Length - cursor);
        return new Result(result.ToString(), applied, blocked, proposals);
    }

    private readonly record struct Item(
        RescoreProposal R,
        Verdict D,
        CharRange Range,
        string OriginalWord,
        int OccurrenceIndex,
        string PublishedText);

    // MARK: - Detection-driven gate (the no-fork Nemotron path)

    /// <summary>
    /// The entry point for engines that return bare text — no per-word timings, no confidence
    /// (Nemotron on Windows, exactly as on Mac). A CTC keyword spotter yields each term actually
    /// spoken plus its audio TIME RANGE; we place each spotted term onto the transcript here.
    ///
    /// Placement: for each detection find a transcript SPAN that is an acoustic near-miss of the
    /// term (the SAME plausibility metric Apply uses) — one word, or a contiguous N-word window when
    /// the term/alias is itself N words. When several qualify, disambiguate by PROPORTIONAL
    /// position — the detection's mid-audio time over total duration mapped to a fractional index
    /// across the words.
    ///
    /// Decision: the SAME Decide the rescore path uses. With no confidence, every word reads as
    /// LowConfidence, so the 0.998-protector cannot fire (correct — there is no confidence to
    /// protect), while plausibility, the common-word brake and learned overrides all still run.
    /// Margin is 0 (the spotter score is on a different scale), which keeps the earned-margin
    /// branch from mis-firing.
    ///
    /// ═══ DELIBERATE DIVERGENCE FROM jot-shared — DO NOT "RE-SYNC" THIS AWAY ═══
    /// Three guards below have NO counterpart in Swift's `applyFromDetections`, which has the same
    /// holes. They are marked inline with `WINDOWS DIVERGENCE`. No golden fixture covers them
    /// (`detections_apply.json` has only single-word terms), so a fixture refresh will NOT catch
    /// their removal — it will just silently re-introduce the corruption. Both bugs were observed
    /// end-to-end with the shipping Nemotron + CTC spotter, on 2026-07-25:
    ///
    ///     engine wrote:  "Claude code generated most of the boiler plate"
    ///     vocabulary ON: "Claude Code code generated most of the boiler plate"      ← defects 1 · 2
    ///
    ///     engine wrote:  "I love cloud code."
    ///     vocabulary ON: "I love cloud code."   (term dropped: spot-unplaced)       ← defect 3
    ///
    /// ONE cause, two opposite symptoms: a placement was ONE whitespace-delimited word measured
    /// against the WHOLE term. So the two-word term "Claude Code" either landed on just "Claude" and
    /// left the transcript's own "code" behind it (defect 1 — and Decide step (2) waves every
    /// multi-word term through as "precise and self-gating"), or, when the engine wrote no single
    /// word close enough on its own — skeleton("cloud") vs skeleton("Claude Code") is 0.56 against a
    /// 0.45 ceiling — it was discarded outright (defect 3), even though the two-word WINDOW
    /// "cloud code" measures 0.20 and is obviously the right host. Defect 1 CORRUPTS text the
    /// transcriber already got right, and the result is pasted before anyone can review it; defect 3
    /// is the feature simply not working on the shape of term users add most.
    ///
    /// Upstream: the Mac's Nemotron path runs the same unguarded Swift function, so it is expected
    /// to behave identically. The dedup half is filed as
    /// https://github.com/vineetu/jot-shared/issues/1 — the window-placement half is a SECOND finding
    /// on the same method and belongs on that issue too. Fixing it there is not what makes THIS path
    /// safe. Note the rescore path above already had BOTH halves (<see cref="AbsorbTrailingDuplicates"/>
    /// for the narrow span, <see cref="AlignmentWindow"/> for the wide one) since the port; only this
    /// path was missing them.
    ///
    /// Regression cover: <c>Jot.Tests/Vocabulary/DetectionPathDedupTests.cs</c>. It is the only thing
    /// that will notice if these guards are deleted.
    /// </summary>
    public static Result ApplyFromDetections(
        string originalTranscript,
        IReadOnlyList<Detection> detections,
        double totalAudioDuration,
        ICommonWordsProvider commonWords,
        string? commonWordsResource = "common-words",
        IReadOnlyList<OverrideEntry>? overrides = null,
        IDiagnosticsSink? diagnostics = null)
    {
        diagnostics ??= NoopDiagnosticsSink.Instance;
        overrides ??= [];

        if (detections.Count == 0 || originalTranscript.Length == 0)
            return new Result(originalTranscript, 0, [], []);

        IReadOnlySet<string> commonWordSet = commonWords.Words(commonWordsResource);

        // Tokenize into whole words with char ranges, in document order.
        var words = new List<(string Text, CharRange Range)>();
        int idx = 0;
        while (idx < originalTranscript.Length)
        {
            while (idx < originalTranscript.Length && char.IsWhiteSpace(originalTranscript[idx])) idx++;
            if (idx >= originalTranscript.Length) break;
            int start = idx;
            while (idx < originalTranscript.Length && !char.IsWhiteSpace(originalTranscript[idx])) idx++;
            words.Add((originalTranscript[start..idx], new CharRange(start, idx)));
        }
        if (words.Count == 0) return new Result(originalTranscript, 0, [], []);

        // Fractional center of each word over the word SEQUENCE — the same axis the detection's
        // time fraction maps onto. A W-word window's center is (i + W/2)/n, which for W = 1 is the
        // single-word formula unchanged.
        int n = words.Count;

        var claimed = new HashSet<int>();
        var picks = new List<(int WordIndex, CharRange Range, Detection Detection)>();

        foreach (Detection det in detections)
        {
            double mid = (det.StartTime + det.EndTime) / 2;
            double frac = totalAudioDuration > 0 ? Math.Clamp(mid / totalAudioDuration, 0, 1) : 0.5;

            // ── WINDOWS DIVERGENCE (defect 3 — "I love cloud code."). See the class-level block.
            // A candidate host is a CONTIGUOUS N-WORD WINDOW, not just one token. Comparing one word
            // against a whole multi-word term is near-hopeless: "cloud" vs "Claude Code" is 0.56
            // against the 0.45 ceiling and the detection is thrown away, while the window
            // "cloud code" is 0.20. Widths come from the term AND its aliases, so a 2-word alias can
            // host a 1-word term ("nemo tron" → "Nemotron") and vice versa; width 1 is always tried
            // and behaves exactly as before, which is what keeps the merged-token case
            // ("ramanathan" → "Ramaa Nathan") and all five golden fixtures byte-identical.
            string[][] shapes = [SplitWords(det.Term), .. det.Aliases.Select(SplitWords)];
            int maxWidth = Math.Clamp(shapes.Max(s => s.Length), 1, words.Count);

            int bestIndex = -1;
            int bestWidth = 0;
            CharRange bestRange = default;
            double bestDistance = double.MaxValue;
            // Diagnostics only — the CLOSEST eligible span the plausibility brake rejected, and by
            // how much. See the unplaced-detection log below for why this is worth carrying.
            string? nearestText = null;
            double nearestGap = double.MaxValue;
            for (int w = 1; w <= maxWidth; w++)
            {
                for (int i = 0; i + w <= words.Count; i++)
                {
                    if (WindowRange(words, i, w, originalTranscript, shapes, claimed) is not { } window)
                        continue;
                    string spanText = Slice(originalTranscript, window);
                    double gap = PlausibilityGap(Normalize(spanText), det.Term, det.Aliases);
                    if (gap < nearestGap) { nearestGap = gap; nearestText = spanText; }
                    if (gap > PlausibilityCeiling) continue;
                    double positional = Math.Abs((i + w / 2.0) / n - frac);
                    // Position first (unchanged semantics); on an exact tie the WIDER window wins —
                    // it is the more specific match, the same tie-break Apply's span sort uses.
                    if (bestIndex < 0 || positional < bestDistance ||
                        (positional == bestDistance && w > bestWidth))
                    {
                        bestIndex = i;
                        bestWidth = w;
                        bestRange = window;
                        bestDistance = positional;
                    }
                }
            }
            if (bestIndex >= 0)
            {
                // Claim EVERY word of the chosen window, so a second detection can never take a word
                // this one already spliced over.
                for (int k = 0; k < bestWidth; k++) claimed.Add(bestIndex + k);
                picks.Add((bestIndex, bestRange, det));
                continue;
            }

            // WINDOWS DIVERGENCE (defect 2). The spotter HEARD this term — often with the best score
            // of the whole dictation — but no transcript span is close enough to host it (`nearest` is
            // the closest ELIGIBLE candidate, of any width, the brake rejected), so there is no
            // proposal, no review row and no chip. Silently dropping it made "I added the term and
            // nothing happened" undiagnosable: the user cannot tell a spotter miss (recall) from a
            // plausibility block (precision), and those are opposite bugs with opposite fixes.
            // Observed: spotter found "Parakeet" at -0.313 (best of five) while the engine had written
            // "Herrakit" — gap 0.50 against the 0.45 ceiling, one guard too far.
            //
            // A LOG, not a review row: a review row needs OriginalStart/OriginalLength anchors into the
            // published text, and there is no span to anchor to. Synthesizing a zero-length one would
            // feed CorrectionProvenance's reconcile/NoteSelfEdit anchor maths a record that indexes
            // nothing, and every spurious detection (exactly what this brake exists to discard) would
            // become a "we heard X" row on an otherwise clean dictation. Deferred past v1.3 on purpose.
            // Do NOT "fix" this by raising PlausibilityCeiling — that threshold is the over-correction
            // brake and is pinned by the golden fixtures.
            diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                $"spot-unplaced {det.Term} — heard, but no plausible word in the transcript",
                new Dictionary<string, string>
                {
                    ["score"] = det.Score.ToString("F3", CultureInfo.InvariantCulture),
                    ["at"] = $"{det.StartTime.ToString("F2", CultureInfo.InvariantCulture)}-" +
                             $"{det.EndTime.ToString("F2", CultureInfo.InvariantCulture)}s",
                    ["nearest"] = nearestText ?? "—",
                    ["gap"] = nearestGap is double.MaxValue
                        ? "—"
                        : nearestGap.ToString("F2", CultureInfo.InvariantCulture),
                    ["ceiling"] = PlausibilityCeiling.ToString("F2", CultureInfo.InvariantCulture),
                });
        }

        if (picks.Count == 0) return new Result(originalTranscript, 0, [], []);

        picks.Sort((a, b) => a.WordIndex.CompareTo(b.WordIndex));

        var result = new StringBuilder();
        int cursor = 0;
        int applied = 0;
        var blocked = new List<string>();
        var proposals = new List<Proposal>();

        foreach ((int wordIndex, CharRange picked, Detection det) in picks)
        {
            CharRange range = picked;
            if (range.Start < cursor)
            {
                // Logged, not silently dropped — same reason Apply logs it: a vanished correction
                // ("I added both terms and only one works") was previously untraceable. Reachable
                // here because the dedup widening below can swallow a word a later pick claimed.
                diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                    $"overlap-dropped {Slice(originalTranscript, range)} → {det.Term}",
                    new Dictionary<string, string>
                    {
                        ["score"] = det.Score.ToString("F2", CultureInfo.InvariantCulture),
                    });
                continue;
            }
            string originalWord = Slice(originalTranscript, range);

            // ── WINDOWS DIVERGENCE (defect 1 — "Claude Code code"). See the class-level block on
            // ApplyFromDetections: Swift's applyFromDetections has neither of the next two guards and
            // is expected to corrupt the same way. No fixture covers them.
            //
            // A placement can still land NARROWER than the term — window placement needs a plausible
            // word-for-word window, and the leftover single word often wins on position anyway
            // ("Claude" alone is 0.40, inside the ceiling) — leaving the term's own tail duplicated
            // behind it. Widen the span to absorb the following words when they duplicate the term's
            // trailing words — the identical guard the rescore path runs, reused verbatim rather than
            // re-derived, so the two paths can never disagree about what a "Claude Code" span is.
            //
            // COMPOSES WITH WINDOW PLACEMENT BY CONSTRUCTION, not by luck: maxAbsorb is
            // (term words − span words), so a window that already covers every term word absorbs
            // NOTHING (maxAbsorb < 1 → null) and the two mechanisms can never both fire on one span.
            bool dedupAmbiguous = false;
            string[] termWords = SplitWords(det.Term);
            if (termWords.Length >= 2 &&
                AbsorbTrailingDuplicates(det.Term, originalWord, range, originalTranscript) is { } widened)
            {
                if (EndsOnContentCluster(originalWord))
                {
                    string hosted = originalWord;
                    range = widened;
                    originalWord = Slice(originalTranscript, widened);
                    diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                        $"dedup-absorbed {hosted} → {originalWord}",
                        new Dictionary<string, string> { ["term"] = det.Term });
                }
                else
                {
                    // The host token carries its OWN trailing punctuation ("Claude, code"). Widening
                    // would silently eat that comma; not widening would publish the duplicated tail
                    // this guard exists to prevent. Both change text on a guess, so do neither —
                    // block, leave the transcript byte-for-byte as the engine wrote it, and leave a
                    // reviewable "kept" row.
                    dedupAmbiguous = true;
                    diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                        $"dedup-ambiguous {originalWord} → {det.Term}",
                        new Dictionary<string, string> { ["reason"] = "span ends on punctuation" });
                }
            }

            // Identity no-op: the spotter fires on the audio whether or not the decoder already wrote
            // the term correctly. Skip so we never emit a spurious "Vikram → Vikram".
            //
            // TWO relations, and the difference is deliberate:
            //
            //  * SINGLE-WORD span — NORMALIZED, i.e. case-insensitive. Locked by the golden fixture
            //    `spot-identity-is-noop` ("talk to sriram" + term "Sriram" leaves the text alone):
            //    Mac does not churn a word the engine already wrote just to re-case it. Do not
            //    tighten this to ordinal; the fixture is the contract.
            //
            //  * MULTI-WORD span (window-placed or dedup-widened) — ORDINAL. Either route only
            //    reaches here through a word-for-word skeleton match against the term, so the
            //    transcript really does contain the term's words; the ONLY thing left to fix is their
            //    casing, and for a multi-word proper noun ("Claude Code") that casing IS the term —
            //    it is what the user typed and the reason they added it. "Claude code" therefore
            //    corrects, while an already-perfect "Claude Code" skips and fires no chip or review
            //    row for a correction that never happened. No fixture covers a multi-word span (see
            //    the divergence note on this method), so this relation is ours to choose.
            string[] spanWords = SplitWords(originalWord);
            if (spanWords.Length >= 2
                ? string.Equals(originalWord, det.Term, StringComparison.Ordinal)
                : Normalize(originalWord) == Normalize(det.Term))
                continue;

            // WINDOWS DIVERGENCE, part 2 — the WIDER-span case. Reachable two ways now: a widened span
            // that reached EQUAL width, and a window sized by an ALIAS with more words than the term.
            // AlignmentWindow is the brake for both — it demands the term's words form one unique,
            // ordered, edge-touching window inside the span, so a span whose head is not actually the
            // term's head blocks instead of rewriting. A block still publishes the original text and
            // still leaves a reviewable "kept" row.
            bool alignmentBlocked =
                termWords.Length >= 2 && spanWords.Length >= termWords.Length
                && !AlignmentWindow(termWords, spanWords).Unique;

            // Synthesize a neutral proposal so the SAME Decide runs: margin 0, term as the
            // replacement.
            var synthetic = new RescoreProposal(originalWord, det.Term, true, null, 0);
            Verdict d = Decide(synthetic, originalWord, new Dictionary<string, float>(),
                overrides, det.Aliases, commonWordSet);
            // A span whose identity is unsafe is force-blocked and is never a live-ask candidate —
            // the same treatment Apply gives an alignment-blocked proposal.
            if ((alignmentBlocked || dedupAmbiguous) && d.Pass)
                d = d with { Pass = false, Label = "BLOCK", AskCandidate = false };

            diagnostics.Record(DiagnosticsCategory.VocabularyGate,
                $"gate(spot) {originalWord} → {det.Term}",
                new Dictionary<string, string>
                {
                    ["decision"] = d.Label,
                    ["score"] = det.Score.ToString("F2", CultureInfo.InvariantCulture),
                });

            result.Append(originalTranscript, cursor, range.Start - cursor);
            int publishedStart = GraphemeCount(result.ToString());   // Swift's `result.count`
            // Preserve edge punctuation so a bare term doesn't eat the original's comma/period.
            string publishedText = d.Pass
                ? PreservingEdgePunctuation(det.Term, originalWord)
                : originalWord;
            result.Append(publishedText);

            proposals.Add(new Proposal(
                OriginalWord: originalWord,
                Term: det.Term,
                Decision: d.Label,
                Outcome: d.Pass ? "applied" : "kept",
                Confidence: d.Confidence,
                Margin: d.Margin,
                Unsure: d.Unsure,
                AskCandidate: d.AskCandidate,
                OccurrenceIndex: wordIndex,
                OriginalStart: GraphemeCount(originalTranscript.AsSpan(0, range.Start)),
                OriginalLength: GraphemeCount(originalTranscript.AsSpan(range.Start, range.Length)),
                PublishedStart: publishedStart,
                PublishedLength: GraphemeCount(publishedText),
                Alternates: [],
                Shape: null));

            if (d.Pass) applied++;
            else blocked.Add(originalWord);
            cursor = range.End;
        }

        result.Append(originalTranscript, cursor, originalTranscript.Length - cursor);
        return new Result(result.ToString(), applied, blocked, proposals);
    }

    /// <summary>Carry LEADING and TRAILING punctuation from the original token onto the bare
    /// term, so "(Jamie," → "(Jamy," not "Jamy". Only non-alphanumeric edge characters move; an
    /// internal apostrophe in the term is left intact.
    ///
    /// Swift's edge test is `!isLetter &amp;&amp; !isNumber`, and its `isNumber` covers EVERY Unicode
    /// number — so "½shriram" carries nothing and publishes "Sriram", where a Nd-only test reads
    /// the ½ as punctuation and publishes "½Sriram". This one changes the TEXT, not an
    /// anchor.</summary>
    private static string PreservingEdgePunctuation(string replacement, string original)
    {
        int lead = 0;
        while (lead < original.Length && !IsContentCluster(original, lead))
            lead += ClusterLength(original, lead);

        // Walk forward once and remember where the last content cluster ended: everything after
        // it is the trailing run. (Scanning backwards by char would split an NFD "é" and treat
        // its accent as punctuation.)
        int lastContentEnd = 0;
        for (int i = 0; i < original.Length;)
        {
            int len = ClusterLength(original, i);
            if (IsContentCluster(original, i)) lastContentEnd = i + len;
            i += len;
        }
        int trail = original.Length - lastContentEnd;

        if (lead + trail >= original.Length) return replacement;
        return original[..lead] + replacement + original[^trail..];
    }

    // MARK: - Gate decision

    private static Verdict Decide(
        RescoreProposal r,
        string? originalWord,
        IReadOnlyDictionary<string, float> wordConfidence,
        IReadOnlyList<OverrideEntry> overrides,
        IReadOnlyList<string> aliases,
        IReadOnlySet<string> commonWords)
    {
        float margin = (r.ReplacementScore ?? r.OriginalScore) - r.OriginalScore;
        // The EFFECTIVE span identity when the caller adjusted it; every guard keys on this so
        // the decision matches the words the replacement actually touches.
        string @base = Normalize(originalWord ?? r.OriginalWord);
        string term = r.ReplacementWord ?? "";
        string[] baseWords = SplitWords(@base);

        float? measured = null;
        foreach (string w in baseWords)
        {
            if (wordConfidence.TryGetValue(w, out float c) && (measured is null || c < measured)) measured = c;
        }
        float confidence = measured ?? LowConfidence;
        bool isCommon = baseWords.Any(w => commonWords.Contains(CorrectionKey.Lowercased(w)));
        // Genuine acoustic uncertainty: a MEASURED confidence between "shaky" and "sure".
        // Unknown confidence (common for the OOV names this feature targets) is NOT unsure, so
        // it doesn't over-prioritise asks.
        bool unsure = measured is { } m && m >= LowConfidence && m < ConfidenceCeiling;

        // (0) USER-CONFIRMED OVERRIDE. Fires on the proposal alone, bypassing the guards, for
        //     this exact (originalWord → term) pair. A common-word original is NEVER auto-applied
        //     without an explicit grant: silently rewriting an everyday word everywhere is the
        //     headline over-correction bug, and review can't undo a paste that already left.
        //     Case-insensitive term compare — engines re-case replacements from sentence context
        //     while the store keys terms lowercased, and a case-sensitive compare silently missed
        //     learned overrides for differently-capitalized occurrences. The relation is
        //     lowercase-then-compare, NOT OrdinalIgnoreCase: AskPolicy.Prior/Granted decide
        //     whether to ASK about the very same row this decides whether to APPLY, and two
        //     different case relations can disagree about one override.
        foreach (OverrideEntry ov in overrides)
        {
            if (ov.OriginalWord != @base || CorrectionKey.Lowercased(ov.Term) != CorrectionKey.Lowercased(term))
                continue;
            // DEMOTED: the owner reverted this mapping → stop auto-applying it.
            if (ov.Net <= -1) return new Verdict(false, confidence, margin, "BLOCK", unsure, false);
            // EXPLICIT GRANT ("always replace X with Y") — auto-apply even over a common word;
            // the user consented on-screen for this exact pair. Armed → never an ask.
            if (ov.AlwaysReplace) return new Verdict(true, confidence, margin, "OVERRIDE", unsure, false);
            // CONFIRMED: auto-apply for rare/OOV originals only.
            if (!isCommon && ov.Net >= 1) return new Verdict(true, confidence, margin, "OVERRIDE", unsure, false);
            break;
        }

        // (1) PLAUSIBILITY — the heard word must be an acoustically plausible cousin of the term
        //     (or of ANY alias — an alias is the user TELLING us the pair is plausible). Spotters
        //     fuzzy-match with a low floor and concatenate neighbours, so they can propose
        //     "Vikram"→"Sriram" — half the letters different. No acoustic margin makes that right.
        //     A block here still emits a reviewable "kept" record.
        if (!Plausible(@base, term, aliases))
            return new Verdict(false, confidence, margin, "BLOCK", unsure, false);

        // (2) A multi-word TERM is precise and self-gating. Applied silently → not an ask.
        if (term.Contains(' '))
            return new Verdict(true, confidence, margin, "APPLY", unsure, false);

        // (3) Never overwrite a very confident word unless the term wins big — the 0.998
        //     protector. The gate working as intended, so not an ask.
        if (confidence >= ConfidenceCeiling && margin <= EarnedMargin)
            return new Verdict(false, confidence, margin, "BLOCK", unsure, false);

        // (4) Everyday word → NEVER silently rewrite. This is the headline "every name becomes
        //     Jamy" protection: a common original is surfaced for review, never swapped. It IS
        //     flagged as an ask candidate (a plausible common-word near-miss — "did you mean
        //     Lisa?") so the UI can offer it once.
        if (isCommon)
            return new Verdict(false, confidence, margin, "BLOCK", unsure, true);

        // (5) OOV-ish word (a likely name/jargon mis-hear) → allow, and flag the silent-OOV
        //     "did you mean Vikram?" ask.
        return new Verdict(true, confidence, margin, "APPLY", unsure, true);
    }

    // MARK: - Alignment window (multi-word term over a WIDER span)

    /// <summary>
    /// A multi-word term may only apply to a span with MORE words than the term when the term's
    /// words form ONE unique, ordered, contiguous, edge-touching window inside the span. Extra
    /// words outside the window SURVIVE ("use Claude code" → window "Claude code", "use" stays).
    /// Anything ambiguous blocks — precision first, never trim-and-guess.
    /// </summary>
    public static (bool Unique, int WordIndex) AlignmentWindow(string[] termWords, string[] spanWords)
    {
        if (termWords.Length < 2 || spanWords.Length <= termWords.Length)
        {
            // Equal width: require EVERY word to align, so shifted spans block
            // ("Claude Claude" never aligns to "Claude Code").
            if (spanWords.Length == termWords.Length)
            {
                bool ok = spanWords.Zip(termWords).All(p => WordAligns(p.First, p.Second));
                return (ok, 0);
            }
            return (true, 0);   // narrower span — the dedup guard's domain
        }

        var matches = new List<int>();
        for (int start = 0; start <= spanWords.Length - termWords.Length; start++)
        {
            bool all = true;
            for (int k = 0; k < termWords.Length; k++)
            {
                if (!WordAligns(spanWords[start + k], termWords[k])) { all = false; break; }
            }
            if (all) matches.Add(start);
        }
        if (matches.Count != 1) return (false, 0);
        int s = matches[0];
        bool touchesEdge = s == 0 || s + termWords.Length == spanWords.Length;
        return touchesEdge ? (true, s) : (false, 0);
    }

    /// <summary>Per-word alignment at the same ceiling as the plausibility guard.</summary>
    private static bool WordAligns(string spanWord, string termWord)
    {
        Rune[] a = Skeleton(spanWord);
        Rune[] b = Skeleton(termWord);
        if (a.Length == 0 || b.Length == 0) return false;
        return (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length) <= PlausibilityCeiling;
    }

    // MARK: - Window placement (detection path only)

    /// <summary>
    /// The char range a <paramref name="width"/>-word window starting at word
    /// <paramref name="start"/> would replace, or null when that window may not host the term.
    ///
    /// Width 1 is the ORIGINAL single-token behaviour, byte for byte — no punctuation rule, no
    /// alignment rule, whatever the tokenizer produced. Every extra rule below applies only to
    /// genuine multi-word windows:
    ///
    ///  * WORD-FOR-WORD ALIGNMENT against a term/alias of the same width. Whole-window plausibility
    ///    alone is far too loose to place text with: "Claude wrote" measures 0.36 against
    ///    "Claude Code", inside the ceiling, and would silently overwrite "wrote".
    ///  * CROSS PLAIN SPACES ONLY, and never a word's own punctuation. Widening across the comma in
    ///    "ask Claude, code review" would eat a boundary the engine deliberately wrote; the dedup
    ///    guard already refuses that guess (dedup-ambiguous) and this must refuse it identically.
    ///  * END AT THE LAST ALPHANUMERIC of the final word, so a trailing "." / "," stays OUTSIDE the
    ///    replaced span — the same boundary <see cref="AbsorbTrailingDuplicates"/> picks, so a
    ///    window-placed span and a dedup-widened one are the same shape and carry the same anchors.
    /// </summary>
    private static CharRange? WindowRange(
        IReadOnlyList<(string Text, CharRange Range)> words,
        int start, int width, string text, string[][] shapes, HashSet<int> claimed)
    {
        for (int k = 0; k < width; k++)
        {
            if (claimed.Contains(start + k)) return null;
        }
        if (width == 1) return words[start].Range;

        for (int k = 0; k < width; k++)
        {
            (string word, CharRange range) = words[start + k];
            if (k > 0)
            {
                for (int i = words[start + k - 1].Range.End; i < range.Start; i++)
                {
                    if (text[i] != ' ') return null;     // newline/tab is a boundary, not a gap
                }
                if (!IsContentCluster(text, range.Start)) return null;
            }
            if (k < width - 1 && !EndsOnContentCluster(word)) return null;
        }

        var window = new string[width];
        for (int k = 0; k < width; k++) window[k] = words[start + k].Text;
        if (!shapes.Any(s => s.Length == width && AlignmentWindow(s, window).Unique)) return null;

        CharRange lastWord = words[start + width - 1].Range;
        int end = -1;
        for (int i = lastWord.Start; i < lastWord.End;)
        {
            int len = ClusterLength(text, i);
            if (IsContentCluster(text, i)) end = Math.Min(i + len, lastWord.End);
            i += len;
        }
        int begin = words[start].Range.Start;
        return end > begin ? new CharRange(begin, end) : null;
    }

    /// <summary>Character range of <paramref name="count"/> whole words starting at word index
    /// <paramref name="wordIndex"/> within <paramref name="range"/>. Null if the slice doesn't
    /// have that many words.</summary>
    private static CharRange? WordSubrange(CharRange range, int wordIndex, int count, string text)
    {
        var starts = new List<int>();
        var ends = new List<int>();
        bool inWord = false;
        for (int i = range.Start; i < range.End; i++)
        {
            if (text[i] == ' ')
            {
                if (inWord) { ends.Add(i); inWord = false; }
            }
            else if (!inWord)
            {
                starts.Add(i);
                inWord = true;
            }
        }
        if (inWord) ends.Add(range.End);
        if (wordIndex + count > starts.Count || ends.Count != starts.Count) return null;

        // Trim the window's ends to alphanumerics: boundary punctuation must stay OUTSIDE the
        // replaced range so "cloud code," → "Claude Code," keeps its comma. Cluster-wise (same
        // predicate as PreservingEdgePunctuation) so a trailing accent is never trimmed off its
        // own letter.
        int lo = starts[wordIndex];
        int wordEnd = ends[wordIndex];
        while (lo < wordEnd && !IsContentCluster(text, lo)) lo += ClusterLength(text, lo);

        int lastStart = starts[wordIndex + count - 1];
        int end = ends[wordIndex + count - 1];
        int hi = lastStart;
        for (int i = lastStart; i < end;)
        {
            int len = ClusterLength(text, i);
            if (IsContentCluster(text, i)) hi = Math.Min(i + len, end);
            i += len;
        }
        return lo < hi ? new CharRange(lo, hi) : null;
    }

    // MARK: - Dedup guard (multi-word term over a NARROWER span)

    /// <summary>
    /// When a multi-word term applies over a span with FEWER words than the term ("Claude Code"
    /// over just "clawed") and the term's trailing words duplicate the transcript words right
    /// after the span, return the span widened to absorb them — so the apply yields "Claude Code"
    /// once, never "Claude Code code".
    ///
    /// Precision rules: absorb at most (term words − span words), so a span already covering
    /// every term word absorbs NOTHING and a real following "code" is never eaten; cross plain
    /// spaces only; compare letter skeletons and require EXACT equality; end the widened span at
    /// the absorbed word's last alphanumeric so its trailing punctuation survives outside.
    /// </summary>
    private static CharRange? AbsorbTrailingDuplicates(string term, string spanWord, CharRange range, string text)
    {
        Rune[][] termSkels = SplitWords(term).Select(Skeleton).ToArray();
        int spanWordCount = SplitWords(spanWord).Length;
        int maxAbsorb = termSkels.Length - spanWordCount;
        if (maxAbsorb < 1) return null;

        var follow = new List<(Rune[] Skel, int LetterEnd)>();
        int i = range.End;
        while (follow.Count < maxAbsorb && i < text.Length)
        {
            // Cross plain spaces only — punctuation/newline ends the run.
            if (text[i] != ' ') break;
            while (i < text.Length && text[i] == ' ') i++;
            if (i >= text.Length || !IsContentCluster(text, i)) break;

            // Read to the next space/newline; track the end of the last alphanumeric CLUSTER so
            // "code," absorbs as "code" and keeps the comma (and "café," keeps its accent).
            int wordEnd = i;
            int letterEnd = i + ClusterLength(text, i);
            while (wordEnd < text.Length && text[wordEnd] != ' ' && text[wordEnd] != '\n')
            {
                int len = ClusterLength(text, wordEnd);
                if (IsContentCluster(text, wordEnd)) letterEnd = wordEnd + len;
                wordEnd += len;
            }
            follow.Add((Skeleton(text[i..letterEnd]), letterEnd));
            // Trailing punctuation on this token ends the absorbable run.
            if (letterEnd != wordEnd) break;
            i = wordEnd;
        }
        if (follow.Count == 0) return null;

        // Longest suffix of the term's words that equals the following words.
        for (int k = Math.Min(maxAbsorb, follow.Count); k > 0; k--)
        {
            bool equal = true;
            for (int j = 0; j < k; j++)
            {
                if (!termSkels[termSkels.Length - k + j].AsSpan().SequenceEqual(follow[j].Skel))
                {
                    equal = false;
                    break;
                }
            }
            if (equal) return new CharRange(range.Start, follow[k - 1].LetterEnd);
        }
        return null;
    }

    // MARK: - Extension alternates (3-option ask)

    /// <summary>
    /// Longer vocab siblings of the winning term whose extra words match the transcript words
    /// that FOLLOW the span. Greedy merges prefer the SHORTER span, so when the list has both
    /// "Claude" and "Claude Code", "Claude" always wins and the longer candidate is dropped
    /// upstream — re-derive it here so an ask can offer all three choices. The extension must be
    /// an acoustic cousin of what follows, and never crosses a paragraph break. Capped at 2.
    /// </summary>
    private static IReadOnlyList<Alternate> ExtensionAlternates(
        string? winnerTerm, string publishedText, int upperBound, string transcript, IReadOnlyList<string> allTerms)
    {
        if (string.IsNullOrEmpty(winnerTerm) || allTerms.Count == 0) return [];
        string winnerLower = CorrectionKey.Lowercased(winnerTerm);
        var output = new List<Alternate>();

        foreach (string term in allTerms)
        {
            if (output.Count >= 2) break;
            string termLower = CorrectionKey.Lowercased(term);
            if (termLower == winnerLower || !termLower.StartsWith(winnerLower + " ", StringComparison.Ordinal)) continue;

            // dropFirst counts CHARACTERS in Swift, and the trim is the Zs+TAB set — same two
            // string-model traps as everywhere else in this file.
            string extensionText = TrimSpaces(DropFirstClusters(term, GraphemeCount(winnerTerm)));
            string[] extWords = SplitWords(extensionText);
            if (extWords.Length == 0) continue;

            var follow = NextWords(extWords.Length, upperBound, transcript);
            if (follow.Count != extWords.Length) continue;
            int lastEnd = follow[^1].End;

            Rune[] a = Skeleton(string.Concat(follow.Select(f => f.Word)));
            Rune[] b = Skeleton(extensionText);
            if (a.Length == 0 || b.Length == 0) continue;
            if ((double)Levenshtein(a, b) / Math.Max(a.Length, b.Length) > PlausibilityCeiling) continue;

            // `find` is built from the EXACT transcript slice after the span so double spaces or
            // odd separators are preserved verbatim and the strict anchored splice can match.
            output.Add(new Alternate(term, publishedText + transcript[upperBound..lastEnd]));
        }
        return output;
    }

    /// <summary>The next <paramref name="count"/> space-separated words after
    /// <paramref name="start"/>, each with its end index. Stops at a newline so an alternate
    /// never extends across a paragraph break.</summary>
    private static List<(string Word, int End)> NextWords(int count, int start, string text)
    {
        var words = new List<(string Word, int End)>();
        var current = new StringBuilder();
        int i = start;
        while (i < text.Length && words.Count < count)
        {
            char ch = text[i];
            if (ch == '\n') break;
            if (ch == ' ')
            {
                if (current.Length > 0) { words.Add((current.ToString(), i)); current.Clear(); }
            }
            else
            {
                current.Append(ch);
            }
            i++;
        }
        if (current.Length > 0 && words.Count < count) words.Add((current.ToString(), i));
        return words;
    }

    // MARK: - Plausibility

    private static bool Plausible(string original, string term, IReadOnlyList<string> aliases) =>
        PlausibilityGap(original, term, aliases) <= PlausibilityCeiling;

    /// <summary>The number <see cref="Plausible"/> compares against
    /// <see cref="PlausibilityCeiling"/>: the SMALLEST normalized edit distance from the heard word
    /// to the term or any alias. Split out so a rejection can be REPORTED with its margin ("herrakit
    /// vs parakeet, 0.50 against a 0.45 ceiling") instead of vanishing as a bare false.
    ///
    /// Two edges preserved exactly as the boolean had them: an empty heard skeleton is always
    /// plausible (0), and a candidate with an empty skeleton is no candidate at all
    /// (<see cref="double.MaxValue"/> when it is the only one).</summary>
    private static double PlausibilityGap(string original, string term, IReadOnlyList<string> aliases)
    {
        Rune[] heard = Skeleton(original);
        if (heard.Length == 0) return 0;

        double best = Distance(heard, term);
        foreach (string alias in aliases)
        {
            best = Math.Min(best, Distance(heard, alias));
        }
        return best;

        static double Distance(Rune[] heard, string candidate)
        {
            Rune[] c = Skeleton(candidate);
            if (c.Length == 0) return double.MaxValue;
            return (double)Levenshtein(heard, c) / Math.Max(heard.Length, c.Length);
        }
    }

    /// <summary>Does <paramref name="s"/> end on a letter/number cluster? Trailing punctuation is a
    /// boundary the engine put there, and the detection path's dedup widening must not absorb across
    /// one.</summary>
    private static bool EndsOnContentCluster(string s)
    {
        if (s.Length == 0) return false;
        int lastStart = 0;
        for (int i = 0; i < s.Length;)
        {
            lastStart = i;
            i += ClusterLength(s, i);
        }
        return IsContentCluster(s, lastStart);
    }

    /// <summary>Lowercased alphanumeric SCALARS only — spaces and punctuation dropped so a merged
    /// ASR word ("ramanathan") measures fairly against a multi-word term ("Ramaa Nathan" →
    /// "ramaanathan"). NFC-precomposed first so canonically-equivalent Unicode (composed vs
    /// decomposed "é") skeletons identically instead of dropping a combining mark.
    ///
    /// SCALARS, not chars: this is the plausibility brake's unit of measure, and a char-based
    /// filter loosens it in the one direction that matters. `char.IsLetterOrDigit` drops
    /// combining marks (every Devanagari/Thai/Hebrew vowel sign) and both halves of an astral
    /// letter, so distances shrink toward 0 and terms that Mac blocks would apply here.</summary>
    private static Rune[] Skeleton(string s)
    {
        string nfc = CorrectionKey.Lowercased(s.Normalize(NormalizationForm.FormC));
        var runes = new List<Rune>(nfc.Length);
        foreach (Rune r in nfc.EnumerateRunes())
        {
            if (IsAlphanumericScalar(r)) runes.Add(r);
        }
        return runes.ToArray();
    }

    /// <summary>Foundation's <c>CharacterSet.alphanumerics</c>: general categories L*, M* AND N*
    /// — marks and non-decimal numbers included, which `char.IsLetterOrDigit` (L* + Nd) is
    /// not.</summary>
    private static bool IsAlphanumericScalar(Rune r)
    {
        // ASCII is the hot path (this runs per word per proposal); skip the category lookup.
        if (r.IsAscii) return char.IsLetterOrDigit((char)r.Value);
        return Rune.GetUnicodeCategory(r) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber;
    }

    /// <summary>Plain two-row Levenshtein over SCALARS (Swift measures `[Character]`, so a UTF-16
    /// pass would charge 2 for one astral letter and inflate the denominator). Inputs are short
    /// (words / short phrases), so O(a·b) is trivially cheap even on the transcription hot
    /// path.</summary>
    private static int Levenshtein(ReadOnlySpan<Rune> a, ReadOnlySpan<Rune> b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // MARK: - Swift string model
    //
    // Swift counts and tests CHARACTERS (extended grapheme clusters) over SCALARS; .NET counts
    // UTF-16 code units. These are the only places that difference is handled, so a fix lands
    // once instead of drifting between call sites.

    private static readonly CompareInfo Invariant = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Swift's `String.count` — grapheme clusters, so "\r\n" is ONE, not two. The
    /// provenance anchors are persisted and compared across platforms, so they must be counted
    /// on the same axis Mac/iOS use.</summary>
    private static int GraphemeCount(ReadOnlySpan<char> s)
    {
        int n = 0;
        while (!s.IsEmpty)
        {
            s = s[StringInfo.GetNextTextElementLength(s)..];
            n++;
        }
        return n;
    }

    /// <summary>Chars in the grapheme cluster starting at <paramref name="index"/> — Swift's
    /// `index(after:)` step.</summary>
    private static int ClusterLength(string text, int index) =>
        StringInfo.GetNextTextElementLength(text.AsSpan(index));

    /// <summary>Swift's `String.dropFirst(n)`, where n counts Characters.</summary>
    private static string DropFirstClusters(string s, int count)
    {
        int i = 0;
        for (int k = 0; k < count && i < s.Length; k++) i += ClusterLength(s, i);
        return s[i..];
    }

    /// <summary>Swift's `Character.isLetter || Character.isNumber` for the cluster at
    /// <paramref name="index"/>. Decided by the cluster's BASE scalar, so an NFD "é" reads as a
    /// letter and its combining accent is never mistaken for edge punctuation.</summary>
    private static bool IsContentCluster(string text, int index)
    {
        Rune.DecodeFromUtf16(text.AsSpan(index), out Rune r, out _);
        return IsAlphanumericScalar(r);
    }

    /// <summary>Swift's `trimmingCharacters(in: .whitespaces)` — Unicode Zs plus TAB, and
    /// deliberately NOT newlines (that set is `.whitespacesAndNewlines`).</summary>
    private static string TrimSpaces(string s)
    {
        int start = 0;
        while (start < s.Length && IsSpaceOrTab(s[start])) start++;
        int end = s.Length;
        while (end > start && IsSpaceOrTab(s[end - 1])) end--;
        return s[start..end];

        static bool IsSpaceOrTab(char c) =>
            c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
    }

    /// <summary>Swift's `rangeOfCharacter(from: .letters)` — that set is L* ∪ M*.</summary>
    private static bool ContainsLetterScalar(string s)
    {
        foreach (Rune r in s.EnumerateRunes())
        {
            if (Rune.IsLetter(r)) return true;
            UnicodeCategory c = Rune.GetUnicodeCategory(r);
            if (c is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark) return true;
        }
        return false;
    }

    /// <summary>Is the Character at <paramref name="index"/> a letter (Swift's whole-word
    /// boundary test)? Rune-based so an astral letter isn't read as two non-letter halves.
    /// </summary>
    private static bool IsLetterAt(string text, int index)
    {
        if (index >= text.Length) return false;
        Rune.DecodeFromUtf16(text.AsSpan(index), out Rune r, out _);
        return Rune.IsLetter(r);
    }

    /// <summary>Same test for the Character ENDING at <paramref name="index"/>. Combining marks
    /// are skipped so the cluster's base scalar decides, as it does in Swift.</summary>
    private static bool IsLetterBefore(string text, int index)
    {
        int i = index;
        while (i > 0)
        {
            OperationStatus status = Rune.DecodeLastFromUtf16(text.AsSpan(0, i), out Rune r, out int consumed);
            if (status != OperationStatus.Done) return false;
            UnicodeCategory c = Rune.GetUnicodeCategory(r);
            if (c is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                i -= consumed;
                continue;
            }
            return Rune.IsLetter(r);
        }
        return false;
    }

    // MARK: - Helpers

    /// <summary>MUST be the same normalization the correction store keys use — the override
    /// lookup compares this output to store-normalized keys, and any divergence (NFC, whitespace
    /// runs) silently misses learned overrides and demotions.</summary>
    private static string Normalize(string s) => CorrectionKey.Normalize(s);

    private static string[] SplitWords(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Slice(string text, CharRange r) => text[r.Start..r.End];

    /// <summary>Per-word minimum *content-token* confidence, keyed by normalized word. A new word
    /// begins at a token with a leading space or "▁"; the minimum is taken over alphabetic
    /// (content) tokens only — punctuation and casing tokens would otherwise produce false
    /// low-confidence flags. When a word repeats, the lowest occurrence wins (conservative).</summary>
    private static Dictionary<string, float> PerWordMinConfidence(IReadOnlyList<TokenTiming> timings)
    {
        var output = new Dictionary<string, float>();
        var word = new StringBuilder();
        float minConf = 1.0f;

        void Flush()
        {
            string key = Normalize(word.ToString());
            if (key.Length > 0)
            {
                output[key] = output.TryGetValue(key, out float existing) ? Math.Min(existing, minConf) : Math.Min(1.0f, minConf);
            }
            word.Clear();
            minConf = 1.0f;
        }

        foreach (TokenTiming t in timings)
        {
            bool startsWord = t.Token.StartsWith(' ') || t.Token.StartsWith('▁');
            // TrimSpaces, not Trim(): Swift trims `CharacterSet.whitespaces` (Zs + TAB), which
            // does NOT include newlines. Stripping a token's "\n" here changes the key this map
            // is written under, so `decide` finds a confidence it should not have and the
            // 0.998-protector fires on Windows where Mac applies — a different transcript from
            // the same audio.
            string piece = TrimSpaces(t.Token.Replace("▁", ""));
            if (startsWord) Flush();
            word.Append(piece);
            // Swift's `.letters` is L* ∪ M*, so a token that is only a combining mark still
            // counts as content and contributes its confidence.
            if (ContainsLetterScalar(piece)) minConf = Math.Min(minConf, t.Confidence);
        }
        Flush();
        return output;
    }

    /// <summary>The <paramref name="n"/>-th (0-based) whole-word range of
    /// <paramref name="word"/> in <paramref name="text"/>.</summary>
    private static CharRange? NthWholeWordRange(string word, string text, int n)
    {
        int search = 0;
        int count = 0;
        while (WholeWordRange(word, text, search) is { } r)
        {
            if (count == n) return r;
            count++;
            search = r.End;
        }
        return null;
    }

    /// <summary>Whole-word range of <paramref name="word"/> at/after <paramref name="from"/>, so
    /// "name" does not match inside "rename". The word may itself be a multi-word phrase.
    ///
    /// LINGUISTIC, not ordinal. Swift searches with `range(of:options:[.caseInsensitive])`, and
    /// without `.literal` that is ICU loose matching: it compares under CANONICAL EQUIVALENCE, so
    /// an NFC needle matches NFD text and the MATCH LENGTH can differ from the needle's. That is
    /// reachable — transcript text comes from the engine but terms are user-typed, and macOS
    /// keyboards emit NFD — and an ordinal search fails it SILENTLY: no range, no proposal, no
    /// diagnostic, the correction just never happens.
    ///
    /// The alternative considered was NFC-normalizing transcript and needle before searching.
    /// Rejected: every index this returns is spliced into and reported against the ORIGINAL
    /// transcript, so offsets found in a normalized copy would not map back — it would trade a
    /// dropped proposal for a mis-anchored one. `CompareInfo.IndexOf(..., out matchLength)` is
    /// ICU-backed like Foundation's, keeps indices in the original text, and inherits the same
    /// quirks Mac has (fully-ignorable characters can match) rather than inventing new ones.
    /// </summary>
    private static CharRange? WholeWordRange(string word, string text, int from)
    {
        if (word.Length == 0) return null;
        int search = from;
        while (search <= text.Length)
        {
            int i = Invariant.IndexOf(text.AsSpan(search), word, CompareOptions.IgnoreCase, out int matched);
            if (i < 0) return null;
            i += search;
            int end = i + matched;
            // A needle of only ignorable scalars matches everywhere with length 0; Swift would
            // spin on it forever, so step past instead of hanging the transcription thread.
            if (matched <= 0) { search = i + 1; continue; }
            bool okBefore = !IsLetterBefore(text, i);
            bool okAfter = !IsLetterAt(text, end);
            if (okBefore && okAfter) return new CharRange(i, end);
            search = end;
        }
        return null;
    }
}
