using System.Text;

namespace Jot.Vocabulary;

/// <summary>
/// Seam 1b - vocabulary detections derived from the TRANSCRIPT instead of from audio.
///
/// Same output type as <see cref="IVocabularySpotter"/> on purpose: a detection source is a detection
/// source, and both feed the one <see cref="VocabularyGate"/> that owns every safety rule. The
/// difference is what it costs - no checkpoint, no download, no licence, and therefore every language
/// with a common-word list rather than the 16 locales an acoustic checkpoint can reach.
/// </summary>
public interface ITextVocabularySpotter
{
    /// <param name="totalAudioDuration">Only to encode WHERE a match sits, in the time axis the gate
    /// places detections on. Zero is legal and simply drops the positional hint.</param>
    /// <param name="language">The locale being dictated in, because how loose this may be is a
    /// PER-LANGUAGE question (E6: <c>docs/plans/vocabulary-brake-per-language.md</c>). Null means "no
    /// language known" and gets the safest setting, not the loosest.</param>
    IReadOnlyList<VocabularyGate.Detection> Spot(
        string transcript,
        IReadOnlyList<VocabularyTerm> terms,
        double totalAudioDuration,
        string? language = null,
        CancellationToken ct = default);
}

/// <summary>
/// The model-free corrector (<c>vocabulary-plan.md</c> L1): fuzzy-match the user's terms against word
/// windows of the finished transcript and hand the hits to the gate as detections.
///
/// It decides only ONE thing - "is this transcript span a near-miss spelling of a term?" - at a
/// threshold TIGHTER than the gate's, and then gets out of the way. Everything that can corrupt text
/// (common-word brake, learned demotions, identity no-op, punctuation-preserving splice, overlap
/// resolution) stays in <see cref="VocabularyGate.ApplyFromDetections"/>, so the two detection sources
/// cannot drift into two different safety stories.
///
/// CONSEQUENCE WORTH KNOWING BEFORE TUNING ANYTHING: the gate refuses any span more than
/// <see cref="VocabularyGate.PlausibilityCeiling"/> (0.45) from the term, and this accepts at roughly
/// 0.20-0.25, so the acoustic spotter's entire marginal APPLY value is the band between the two.
/// MEASURED on 1041 FLEURS clips: of 214 chances the engine missed, 101 sit at or under 0.30 (this
/// path's home, and this path wins it 72-65), 51 in the 0.30-0.45 band (the spotter's, 30 vs 7) and
/// 61 beyond 0.45, where the spotter still HEARS 35 of them and the gate applies exactly none. See
/// <c>docs/plans/vocabulary-corrector-vs-spotter.md</c>.
/// </summary>
public sealed class VocabularyCorrector : ITextVocabularySpotter
{
    /// <summary>At or below this skeleton length an exact match is required. Three characters at one
    /// edit is a third of the word - every short term would collide with ordinary text.</summary>
    public const int ExactMatchMaxLength = 3;

    /// <summary>Widest window considered, so a long dictation can't turn into an O(n*W) surprise.
    /// Only spelled-out acronyms ("W A S A P I") ever need more than a term's word count + 1.</summary>
    public const int MaxWindowWords = 8;

    /// <summary>
    /// One edit per this many term characters. MEASURED, not chosen: on the corpus above, loosening
    /// this to 4 bought +8 recoveries for +14 false applies, and to 3 bought +26 for +42. The band a
    /// looser threshold reaches is where near-neighbour NAMES live (Maria/Marie, Raku/Raju), which is
    /// exactly where a purely textual rule cannot tell a hit from a collision.
    /// </summary>
    public const int CharactersPerEdit = 5;

    /// <summary>Edits allowed against a term form of <paramref name="skeletonLength"/> characters.</summary>
    public static int EditBudget(int skeletonLength) =>
        skeletonLength <= ExactMatchMaxLength
            ? 0
            : (skeletonLength + CharactersPerEdit - 1) / CharactersPerEdit;

    /// <summary>The shipping entry point: resolve the language's acceptance distance
    /// (<see cref="VocabularyLimits"/>) and spot at it.</summary>
    public IReadOnlyList<VocabularyGate.Detection> Spot(
        string transcript,
        IReadOnlyList<VocabularyTerm> terms,
        double totalAudioDuration,
        string? language = null,
        CancellationToken ct = default) =>
        Spot(transcript, terms, totalAudioDuration, VocabularyLimits.TextualMaxDistance(language), ct);

    /// <summary>
    /// The same spot with the language's ceiling passed in explicitly, so E6's sweep measures the
    /// SHIPPING code path with only the resolved number varying — one mechanism, not a second one that
    /// can drift from it.
    /// </summary>
    public IReadOnlyList<VocabularyGate.Detection> Spot(
        string transcript,
        IReadOnlyList<VocabularyTerm> terms,
        double totalAudioDuration,
        double maxDistance,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript) || terms.Count == 0) return [];

        IReadOnlyList<(int Start, int End)> spans = VocabularyGate.WordSpans(transcript);
        if (spans.Count == 0) return [];

        List<Form> forms = BuildForms(terms);
        if (forms.Count == 0) return [];

        int maxWidth = Math.Min(Math.Min(forms.Max(f => f.WindowWidthLimit), MaxWindowWords), spans.Count);
        Window[][] windows = BuildWindows(transcript, spans, maxWidth);

        ct.ThrowIfCancellationRequested();

        // Best match per window, plus whether two different TERMS reached it equally well - an
        // ambiguous window is dropped rather than resolved by list order.
        var best = new Dictionary<(int Index, int Width), Match>();
        var ambiguous = new HashSet<(int Index, int Width)>();

        foreach (Form form in forms)
        {
            int budget = EditBudget(form.Skeleton.Length);
            for (int w = 1; w <= Math.Min(form.WindowWidthLimit, maxWidth); w++)
            {
                Window[] row = windows[w - 1];
                for (int i = 0; i < row.Length; i++)
                {
                    Window window = row[i];
                    if (!CouldMatch(window, form, budget)) continue;

                    int edits = RestrictedEdits(window.Skeleton, form.Skeleton, budget);
                    if (edits > budget) continue;

                    double normalized =
                        (double)edits / Math.Max(window.Skeleton.Length, form.Skeleton.Length);
                    // The per-language brake (E6). The edit budget alone lets a 6-character term match
                    // at 0.33, and in a language whose frequency list does not cover running text the
                    // gate has nothing behind that. Applied HERE rather than as a tighter gate ceiling
                    // because the gate's 0.45 never binds on this path — the corrector is already
                    // tighter than it — so tightening the ceiling would change nothing.
                    if (normalized > maxDistance) continue;
                    var key = (i, w);
                    if (!best.TryGetValue(key, out Match current))
                    {
                        best[key] = new Match(i, w, form, edits, normalized);
                        continue;
                    }
                    if (normalized < current.Normalized)
                    {
                        best[key] = new Match(i, w, form, edits, normalized);
                        ambiguous.Remove(key);
                    }
                    else if (normalized == current.Normalized &&
                             !ReferenceEquals(current.Form.Term, form.Term))
                    {
                        ambiguous.Add(key);
                    }
                }
            }
            ct.ThrowIfCancellationRequested();
        }

        return Place(transcript, spans, windows, best, ambiguous, totalAudioDuration);
    }

    // MARK: - Placement

    /// <summary>Resolve the surviving matches into non-overlapping detections, best first, and encode
    /// each one's word position as the mid-audio time the gate re-derives it from.</summary>
    private static IReadOnlyList<VocabularyGate.Detection> Place(
        string transcript,
        IReadOnlyList<(int Start, int End)> spans,
        Window[][] windows,
        Dictionary<(int Index, int Width), Match> best,
        HashSet<(int Index, int Width)> ambiguous,
        double totalAudioDuration)
    {
        List<Match> ordered = [.. best
            .Where(kv => !ambiguous.Contains(kv.Key))
            .Select(kv => kv.Value)
            .OrderBy(m => m.Normalized)
            .ThenByDescending(m => m.Width)
            .ThenBy(m => m.Index)];

        var claimed = new HashSet<int>();
        var accepted = new List<(Match Match, IReadOnlyList<string> Aliases)>();

        // A word the engine already spelled exactly as a term is settled, and claiming it FIRST is what
        // keeps a wider window off it. MEASURED: without this, "George W" won the two-word slot for the
        // term "George" (0.14, because the one-word exact match was merely skipped rather than claimed)
        // and the splice deleted the initial. Same shape for "John F", "25 Dunlap", "A Giza".
        foreach (Match m in best.Values)
        {
            if (!IsIdentity(windows[m.Width - 1][m.Index].Text, m)) continue;
            for (int k = 0; k < m.Width; k++) claimed.Add(m.Index + k);
        }

        foreach (Match m in ordered)
        {
            bool free = true;
            for (int k = 0; k < m.Width && free; k++) free = !claimed.Contains(m.Index + k);
            if (!free) continue;

            string spanText = windows[m.Width - 1][m.Index].Text;
            if (WidthUnlockAliases(spanText, m) is not { } aliases) continue;

            for (int k = 0; k < m.Width; k++) claimed.Add(m.Index + k);
            accepted.Add((m, aliases));
        }

        var detections = new List<VocabularyGate.Detection>(accepted.Count);
        foreach ((Match m, IReadOnlyList<string> aliases) in accepted.OrderBy(a => a.Match.Index))
        {
            // The gate places by |(i + w/2)/n - mid/duration|, so writing that fraction back as a time
            // makes our chosen window the zero-distance candidate. Without a duration the gate assumes
            // mid-transcript and picks by plausibility alone - degraded, never wrong-by-construction.
            double at = totalAudioDuration > 0
                ? (m.Index + m.Width / 2.0) / spans.Count * totalAudioDuration
                : 0;
            detections.Add(new VocabularyGate.Detection(
                Term: m.Form.Term.Text,
                Aliases: aliases,
                // Textual distance, negated. NOT comparable with the CTC spotter's log-prob score -
                // it shares the field because the gate only ever logs it.
                Score: (float)-m.Normalized,
                StartTime: at,
                EndTime: at));
        }
        return detections;
    }

    /// <summary>Nothing to fix. The single-word relation is case-INSENSITIVE because that is what the
    /// gate's identity no-op does (fixture <c>spot-identity-is-noop</c>); the multi-word one is ordinal
    /// because there the gate does correct casing.</summary>
    private static bool IsIdentity(string spanText, Match m) => m.Width == 1
        ? CorrectionKey.Normalize(spanText) == CorrectionKey.Normalize(m.Form.Term.Text)
        : string.Equals(spanText, m.Form.Term.Text, StringComparison.Ordinal);

    /// <summary>
    /// The alias list to hand the gate, or null when this match must be dropped.
    ///
    /// A window WIDER than the matched form is invisible to the gate: <c>WindowRange</c> only tries a
    /// width some term/alias shape actually has, so "nemo tron" -> "Nemotron" and "W A S A P I" ->
    /// "WASAPI" would be found here and then silently never placed. Feeding the matched span back as an
    /// alias unlocks that width - and ONLY that width, because it is added only when the span is
    /// already inside the plausibility ceiling on its own. The brake is never loosened, just reachable.
    /// </summary>
    private static IReadOnlyList<string>? WidthUnlockAliases(string spanText, Match m)
    {
        List<string> aliases = [.. m.Form.Term.Aliases];
        if (m.Width <= m.Form.WordCount) return aliases;
        if (VocabularyGate.Gap(spanText, m.Form.Term.Text, aliases) > VocabularyGate.PlausibilityCeiling)
            return null;

        string unlock = VocabularyStore.SanitizeTerm(spanText);
        if (unlock.Length == 0) return null;
        if (!aliases.Any(a => CorrectionKey.Normalize(a) == CorrectionKey.Normalize(unlock)))
            aliases.Add(unlock);
        return aliases;
    }

    // MARK: - Candidate forms and windows

    private sealed record Form(VocabularyTerm Term, Rune[] Skeleton, uint Letters, int WordCount)
    {
        /// <summary>Word n-grams up to the form's own word count + 1 cover merge and split. A form with
        /// no lowercase is treated as a possible spelled-out acronym, where each letter arrives as its
        /// own "word".</summary>
        public int WindowWidthLimit { get; } = Math.Max(
            WordCount + 1,
            Term.Text.Any(char.IsLower) ? 0 : Skeleton.Length);
    }

    private readonly record struct Window(string Text, Rune[] Skeleton, uint Letters);

    private readonly record struct Match(int Index, int Width, Form Form, int Edits, double Normalized);

    private static List<Form> BuildForms(IReadOnlyList<VocabularyTerm> terms)
    {
        var forms = new List<Form>(terms.Count * 2);
        foreach (VocabularyTerm term in terms)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable<string> candidates =
                new[] { term.Text }.Concat(VocabularyStore.FeedAliases(term));
            foreach (string raw in candidates)
            {
                string form = VocabularyStore.SanitizeTerm(raw);
                if (form.Length < VocabularyTermRules.MinTermLength || !seen.Add(form)) continue;

                Rune[] skeleton = VocabularyGate.SkeletonOf(form);
                if (skeleton.Length == 0) continue;
                forms.Add(new Form(
                    term, skeleton, LetterMask(skeleton),
                    form.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length));
            }
        }
        return forms;
    }

    private static Window[][] BuildWindows(
        string transcript, IReadOnlyList<(int Start, int End)> spans, int maxWidth)
    {
        var rows = new Window[maxWidth][];
        for (int w = 1; w <= maxWidth; w++)
        {
            var row = new Window[spans.Count - w + 1];
            for (int i = 0; i < row.Length; i++)
            {
                string text = transcript[spans[i].Start..spans[i + w - 1].End];
                Rune[] skeleton = VocabularyGate.SkeletonOf(text);
                row[i] = new Window(text, skeleton, LetterMask(skeleton));
            }
            rows[w - 1] = row;
        }
        return rows;
    }

    /// <summary>Cheap pre-filter. Every character present on one side and absent on the other needs at
    /// least one edit to account for it, so a symmetric-difference popcount over the budget cannot
    /// match - and this rejects almost everything before the O(a*b) distance runs.</summary>
    private static bool CouldMatch(Window window, Form form, int budget)
    {
        if (window.Skeleton.Length == 0) return false;
        if (Math.Abs(window.Skeleton.Length - form.Skeleton.Length) > budget) return false;
        return System.Numerics.BitOperations.PopCount(window.Letters & ~form.Letters) <= budget
            && System.Numerics.BitOperations.PopCount(form.Letters & ~window.Letters) <= budget;
    }

    /// <summary>Presence bitmap over a-z; everything else folds into one shared bit, which only ever
    /// makes the filter more permissive (and so never drops a real match).</summary>
    private static uint LetterMask(Rune[] skeleton)
    {
        uint mask = 0;
        foreach (Rune r in skeleton)
        {
            int v = r.Value;
            mask |= v is >= 'a' and <= 'z' ? 1u << (v - 'a') : 1u << 26;
        }
        return mask;
    }

    // MARK: - Distance

    /// <summary>
    /// Restricted Damerau-Levenshtein (optimal string alignment) over scalars, abandoned as soon as
    /// every cell in a row exceeds <paramref name="budget"/>.
    ///
    /// Transpositions are the point: an ASR that swaps two letters of a name is one mistake, and
    /// charging it two would put half the near-misses this exists to catch out of reach.
    /// </summary>
    public static int RestrictedEdits(ReadOnlySpan<Rune> a, ReadOnlySpan<Rune> b, int budget)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int overflow = budget + 1;
        int[] prev2 = new int[b.Length + 1];
        int[] prev = new int[b.Length + 1];
        int[] cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prev2[j - 2] + 1);
                cur[j] = v;
                rowMin = Math.Min(rowMin, v);
            }
            if (rowMin > budget) return overflow;
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }
}
