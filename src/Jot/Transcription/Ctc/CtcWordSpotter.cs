using System;
using System.Collections.Generic;
using Jot.Vocabulary;

namespace Jot.Transcription.Ctc;

/// <summary>One token sequence to look for. Several queries can share a <see cref="Term"/> — the term's
/// own spelling plus each of its aliases — because the audio may carry a form that tokenizes nothing
/// like the canonical one ("Sri Ram" vs "Sriram"), and a hit on either is a hit on the term.</summary>
public readonly record struct CtcSpotQuery(
    string Term,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<int> TokenIds);

/// <summary>
/// NVIDIA's CTC-based Word Spotter (arXiv 2406.07096), reduced to what a post-stop, whole-utterance
/// vocabulary pass actually needs: for each term, the best-scoring CTC alignment of its token sequence
/// against ANY contiguous span of encoder frames.
///
/// The formulation, stated because the subtleties are all here:
///
/// * The search is a Viterbi (max, not sum) pass over the standard CTC extended sequence
///   <c>y0 ␣ y1 ␣ … yL-1</c>, so blanks between tokens are optional and a token held across several
///   frames collapses to one emission. The leading and trailing blanks of the textbook lattice are
///   DROPPED: a spot is a subsequence match, so padding the match with blanks can only cost score.
/// * Free start — state 0 may (re)enter at every frame. That is what makes it a spotter rather than a
///   forced alignment, and it is why one pass finds every occurrence instead of only the first.
/// * The label-to-label skip <c>j-2 → j</c> is gated on <c>ext[j] != ext[j-2]</c>. That single condition
///   is the whole repeated-token rule: "bookkeeper"'s doubled piece MUST route through the blank
///   between its two copies, or the collapse would merge them back into one.
/// * Every log-prob is ≤ 0, so a longer path is always a worse raw path. The raw-optimal alignment is
///   therefore the tight one — it does not stretch itself across cheap blank frames to farm score.
///
/// SCORE / NORMALIZATION. The reported score is <c>(total path log-prob) / (token count)</c>: the mean
/// log-probability the model assigns per TERM TOKEN, in (-inf, 0]. Two deliberate choices:
///   * dividing by token count, not by path length, because blank frames bridging a real pause inside a
///     multi-word term ("Claude Code") cost ~0 each — they would inflate a per-FRAME mean without adding
///     any evidence, and a 1 s gap can move a per-frame mean by 2.5x (measured on the calibration clip);
///   * dividing at all, because raw path score falls roughly linearly with token count, so an unnormalized
///     threshold would accept every 2-token term and reject every 6-token one regardless of the audio.
/// The unit is interpretable, which is the point: -0.5 means "the model was, on average, e^-0.5 ≈ 61 %
/// sure of each token of this term". Thresholds are derived from measured separation, never inherited —
/// see <see cref="DefaultMinScore"/>.
/// </summary>
public sealed class CtcWordSpotter
{
    /// <summary>
    /// Per-term acceptance floor on the normalized score, in mean log-prob per token.
    ///
    /// DERIVED, not inherited. FluidAudio's -15.0 / -12.0 are hearsay in our own docs AND on a different
    /// (unnormalized) scale — on this scale they would accept literally everything, including silence.
    ///
    /// MEASURED on this machine (Ryzen 7 3700X, int8 export, CPU EP) by
    /// <c>CtcSpotterTests.M2_ThresholdSeparatesPlantedTermsFromDecoys</c>, which prints both bands:
    ///
    ///   planted  6/6 terms found in tts-terms.wav: -0.038 -0.313 -0.363 -0.415 -0.830 -1.313
    ///            (median -0.363; the same clip at -12 dB moves the worst only to -1.325, so the
    ///            threshold transfers across recording level despite the front-end's known
    ///            gain-sensitivity below mel bin 40)
    ///   decoys   the same 6 terms against 4 clips containing none of them, 24 scores:
    ///            -5.679 … -13.149, i.e. the STRONGEST false candidate is -5.68
    ///
    /// The bands are 4.35 nats apart and do not come close to touching. -3.0 = ln(0.05) sits near the
    /// middle: 1.7 nats below every true hit measured at either level, 2.7 nats above the strongest
    /// decoy. It also reads as something: "on average the model gave each token of this term at least a
    /// 5 % chance".
    ///
    /// Re-derive it, do not nudge it by feel — the calibration fact prints both distributions, and the
    /// corpus is small and TTS-clean, so widen the corpus before widening the threshold.
    /// </summary>
    public const float DefaultMinScore = -3.0f;

    /// <summary>
    /// Occurrence cap per term. A term legitimately said five times should be corrected five times, but a
    /// pathological term (a common syllable) would otherwise emit one hit per frame and hand the gate
    /// thousands of proposals to walk. Overlap suppression already collapses the near-duplicates; this is
    /// the backstop.
    /// </summary>
    public const int DefaultMaxOccurrencesPerTerm = 8;

    private readonly int _blankId;
    private readonly double _frameSeconds;
    private readonly float _minScore;
    private readonly int _maxOccurrences;

    /// <param name="blankId">CTC blank in the head's id space (1024 for this export — read from
    /// <c>tokens.txt</c>, never assumed).</param>
    /// <param name="frameSeconds">Seconds of audio per ENCODER-OUTPUT frame, i.e. after subsampling.
    /// Passed in rather than assumed because every detection time is this number times a frame index:
    /// get it wrong and the gate places every correction on the wrong word.</param>
    public CtcWordSpotter(
        int blankId,
        double frameSeconds,
        float minScore = DefaultMinScore,
        int maxOccurrencesPerTerm = DefaultMaxOccurrencesPerTerm)
    {
        _blankId = blankId;
        _frameSeconds = frameSeconds;
        _minScore = minScore;
        _maxOccurrences = Math.Max(1, maxOccurrencesPerTerm);
    }

    /// <summary>
    /// Search every query in ONE traversal of the log-prob matrix: the outer loop is frames, the inner
    /// loop is queries, so the matrix is read front-to-back once and per-term cost is O(frames x tokens)
    /// with no re-read. That keeps a 200-term list (the UI's cap) linear in list size rather than
    /// quadratic in anything.
    /// </summary>
    /// <param name="logProbs">Flat [frames, vocab] row-major, ALREADY log-softmaxed (this export's
    /// <c>logprobs</c> output is — applying a second one would silently flatten every score).</param>
    public IReadOnlyList<VocabularyGate.Detection> Spot(
        ReadOnlySpan<float> logProbs, int frames, int vocab, IReadOnlyList<CtcSpotQuery> queries)
    {
        if (frames <= 0 || vocab <= 0 || queries.Count == 0) return [];
        if (logProbs.Length < (long)frames * vocab) return [];

        List<State> states = BuildStates(queries, vocab);
        if (states.Count == 0) return [];

        var hits = new List<Hit>();

        for (int t = 0; t < frames; t++)
        {
            ReadOnlySpan<float> row = logProbs.Slice(t * vocab, vocab);

            foreach (State st in states)
            {
                Advance(st, row, t);

                // The accepting state is the LAST label, not the trailing blank: reaching the blank would
                // only add its cost to an already-complete match.
                int last = st.Ext.Length - 1;
                double raw = st.V[last];
                if (double.IsNegativeInfinity(raw)) continue;

                float score = (float)(raw / st.TokenCount);
                if (score < _minScore) continue;
                hits.Add(new Hit(st.QueryIndex, score, st.Start[last], t));
            }
        }

        return Resolve(hits, queries);
    }

    // MARK: - DP

    private sealed class State
    {
        public int QueryIndex;
        public int[] Ext = [];      // y0 ␣ y1 ␣ … yL-1  (even = label, odd = blank)
        public int TokenCount;
        public double[] V = [];     // best raw path score ending in each extended state, this frame
        public int[] Start = [];    // frame that path started on, carried alongside V
    }

    private List<State> BuildStates(IReadOnlyList<CtcSpotQuery> queries, int vocab)
    {
        var states = new List<State>(queries.Count);
        for (int q = 0; q < queries.Count; q++)
        {
            IReadOnlyList<int> ids = queries[q].TokenIds;
            if (ids is null || ids.Count == 0) continue;   // an empty encode can never match; not an error

            // An id the head cannot emit (out of range, or the blank itself) makes the query unmatchable.
            // Dropping it here rather than indexing past the row is the difference between zero recall and
            // an IndexOutOfRange inside a post-stop pass that must never break a dictation.
            bool usable = true;
            foreach (int id in ids)
                if ((uint)id >= (uint)vocab || id == _blankId) { usable = false; break; }
            if (!usable) continue;

            int l = ids.Count;
            int m = 2 * l - 1;
            var ext = new int[m];
            for (int j = 0; j < m; j++) ext[j] = (j & 1) == 0 ? ids[j / 2] : _blankId;

            var v = new double[m];
            for (int j = 0; j < m; j++) v[j] = double.NegativeInfinity;

            states.Add(new State
            {
                QueryIndex = q,
                Ext = ext,
                TokenCount = l,
                V = v,
                Start = new int[m],
            });
        }
        return states;
    }

    /// <summary>
    /// One frame of the Viterbi recursion, IN PLACE. Descending j is not a micro-optimisation: state j
    /// depends only on j, j-1 and j-2 of the PREVIOUS frame, so walking backwards means those three are
    /// still un-overwritten and no second column is needed.
    /// </summary>
    private static void Advance(State st, ReadOnlySpan<float> row, int t)
    {
        int[] ext = st.Ext;
        double[] v = st.V;
        int[] start = st.Start;

        for (int j = ext.Length - 1; j >= 1; j--)
        {
            double best = v[j];
            int bestStart = start[j];

            if (v[j - 1] > best) { best = v[j - 1]; bestStart = start[j - 1]; }

            // Label-to-label skip: legal only between two DIFFERENT labels. Identical neighbours must go
            // through the blank at j-1 or CTC's collapse would fuse them into a single emission.
            if (j >= 2 && (j & 1) == 0 && ext[j] != ext[j - 2] && v[j - 2] > best)
            {
                best = v[j - 2];
                bestStart = start[j - 2];
            }

            if (double.IsNegativeInfinity(best)) continue;   // v[j] is already -inf
            v[j] = best + row[ext[j]];
            start[j] = bestStart;
        }

        // Free start. Continuing an older path into state 0 is never better than restarting here (every
        // log-prob is ≤ 0), but the max is written out rather than assumed so the invariant is visible.
        double head = v[0];
        int headStart = start[0];
        if (head < 0.0) { head = 0.0; headStart = t; }   // also covers the -inf seed on the first frame
        v[0] = head + row[ext[0]];
        start[0] = headStart;
    }

    // MARK: - Occurrence selection

    private readonly record struct Hit(int QueryIndex, float Score, int StartFrame, int EndFrame);

    /// <summary>
    /// Collapse the per-frame candidate stream into occurrences. Candidates ending on adjacent frames
    /// describe the SAME utterance of the term, and so do hits from two queries of one term (its spelling
    /// and an alias) that land on the same audio — so suppression is per TERM, not per query: highest
    /// score wins, anything overlapping it in time is dropped.
    /// </summary>
    private IReadOnlyList<VocabularyGate.Detection> Resolve(
        List<Hit> hits, IReadOnlyList<CtcSpotQuery> queries)
    {
        if (hits.Count == 0) return [];

        var byTerm = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
        foreach (Hit h in hits)
        {
            string term = queries[h.QueryIndex].Term;
            if (!byTerm.TryGetValue(term, out List<Hit>? list)) byTerm[term] = list = [];
            list.Add(h);
        }

        var detections = new List<VocabularyGate.Detection>();
        foreach ((string term, List<Hit> list) in byTerm)
        {
            list.Sort(static (a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : a.StartFrame.CompareTo(b.StartFrame);   // stable, so output is reproducible
            });

            var kept = new List<Hit>();
            foreach (Hit h in list)
            {
                if (kept.Count >= _maxOccurrences) break;
                bool overlaps = false;
                foreach (Hit k in kept)
                    if (h.StartFrame <= k.EndFrame && k.StartFrame <= h.EndFrame) { overlaps = true; break; }
                if (!overlaps) kept.Add(h);
            }

            kept.Sort(static (a, b) => a.StartFrame.CompareTo(b.StartFrame));   // document order for the gate
            foreach (Hit h in kept)
            {
                detections.Add(new VocabularyGate.Detection(
                    term,
                    queries[h.QueryIndex].Aliases ?? [],
                    h.Score,
                    h.StartFrame * _frameSeconds,
                    (h.EndFrame + 1) * _frameSeconds,   // the end frame's audio runs to the NEXT boundary
                    // THE score field's provenance, and the only place it can be set honestly: this is
                    // the mean log-prob per term token described above. The gate reads it to decide how
                    // much string distance the acoustics have earned (VocabularyGate.EffectiveCeiling),
                    // and it must never be true for a source whose "score" is on some other scale.
                    Acoustic: true));
            }
        }

        detections.Sort(static (a, b) => a.StartTime.CompareTo(b.StartTime));
        return detections;
    }
}
