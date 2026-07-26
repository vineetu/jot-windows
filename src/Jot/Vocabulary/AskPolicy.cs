namespace Jot.Vocabulary;

// Port of jot-shared `Sources/JotVocabCore/AskPolicy.swift` (pinned commit 5326460).
// Conformance-locked by ask_policy_select.json.

/// <summary>
/// The correction-ask decision core: the pure *decision* logic — worth-asking, prior ranking,
/// the max-asks cap, the one-shot merge-teach lane, and the mixed-payload rules — with none of
/// the plumbing.
///
/// **This is the answer to "don't nag me twice."** A pair the owner has already answered lands in
/// <c>keyboardSuppressed</c> and is never asked again; a pair they explicitly granted
/// ("always replace") stops consuming ask budget entirely. Both are read here and written by the
/// correction store, so the throttle survives with no per-word confidence signal — which matters
/// on Windows, where the engine supplies none (see DetectionPath_ConfidenceSignalsAreInert).
///
/// **Decide here, spend in the caller (invariant).** <see cref="Select"/> READS the
/// <c>mergeAsked</c> set and MARKS the chosen merge-teach asks, but never writes. Spending the
/// one-shot happens after publish, app-side, exactly as the shipping apps do it.
/// </summary>
public static class AskPolicy
{
    public const int MaxAsks = 3;

    /// <summary>A selected ask: which record, whether it is the one-shot merge-teach card (so the
    /// caller knows to spend its shot), and the chosen alternate.</summary>
    public sealed record Selection(
        CorrectionRecord Record,
        bool IsMergeTeach,
        string? AltTerm,
        string? AltFind);

    /// <summary>
    /// Select the ≤<see cref="MaxAsks"/> records worth confirming, closest-to-automatic first.
    /// Pure — no persistence, no I/O.
    /// </summary>
    /// <param name="unresolved">Records the owner has not adjudicated.</param>
    /// <param name="overrides">Store snapshot; drives both `prior` ranking and the
    /// always-replace exclusion.</param>
    /// <param name="keyboardSuppressed">Pairs the owner already answered — never ask again.</param>
    /// <param name="mergeAsked">Phrases whose one-shot teach card has already been spent.</param>
    public static IReadOnlyList<Selection> Select(
        IReadOnlyList<CorrectionRecord> unresolved,
        IReadOnlyList<OverrideEntry> overrides,
        IReadOnlySet<string> keyboardSuppressed,
        IReadOnlySet<string> mergeAsked)
    {
        string PairKey(CorrectionRecord r) => CorrectionKey.PairKey(r.OriginalWord, r.Term);

        // Match the store's normalization exactly, or `prior` silently reads 0 on a punctuated
        // original and loses its ranking.
        int Prior(CorrectionRecord r)
        {
            string ow = CorrectionKey.Normalize(r.OriginalWord);
            string tm = CorrectionKey.Lowercased(r.Term);
            foreach (OverrideEntry o in overrides)
            {
                if (o.OriginalWord == ow && CorrectionKey.Lowercased(o.Term) == tm) return o.Net;
            }
            return 0;
        }

        // One-shot teach lane: a merge-shaped BLOCKED proposal ("sri ram" → Sriram) gets exactly
        // ONE teach ask per phrase EVER. Memoized per-occurrence so eligibility is computed once
        // and read by both the selection filter and the final spend flag.
        var mergeEligibleMemo = new Dictionary<string, bool>();
        bool MergeTeachEligible(CorrectionRecord r)
        {
            if (mergeEligibleMemo.TryGetValue(r.Key, out bool cached)) return cached;
            bool v = r.Shape == "merge" && r.Outcome == "kept" && !mergeAsked.Contains(PairKey(r));
            mergeEligibleMemo[r.Key] = v;
            return v;
        }

        // A pair the owner explicitly granted "always replace" stops consuming ask budget — it
        // auto-applies, and is revocable in review.
        //
        // Same lowercase-then-compare relation as `Prior` above and as VocabularyGate.Decide's
        // override lookup. They must agree row-for-row: OrdinalIgnoreCase folds a different set
        // of characters, so mixing the two lets the gate auto-apply an override the ask policy
        // still thinks is unanswered (or the reverse).
        bool Granted(CorrectionRecord r)
        {
            string ow = CorrectionKey.Normalize(r.OriginalWord);
            string tm = CorrectionKey.Lowercased(r.Term);
            foreach (OverrideEntry o in overrides)
            {
                if (o.OriginalWord == ow && CorrectionKey.Lowercased(o.Term) == tm) return o.AlwaysReplace;
            }
            return false;
        }

        // Worth asking: an APPLIED correction (the gate changed the text — most worth a quick
        // confirm) or a mapping part-way to automatic (prior > 0). All other KEPT blocks stay on
        // the transcript only. Suppressed pairs and granted pairs are excluded. A merge-shaped
        // BLOCKED record is eligible ONLY through the one-shot lane — never via prior > 0, which
        // would resurrect a spent phrase.
        bool WorthAsking(CorrectionRecord r)
        {
            if (keyboardSuppressed.Contains(PairKey(r))) return false;
            if (Granted(r)) return false;
            if (r.Shape == "merge" && r.Outcome == "kept") return MergeTeachEligible(r);
            return r.Outcome == "applied" || Prior(r) > 0;
        }

        // OrderByDescending is a STABLE sort, so ties keep input order — matching the shared
        // implementation's behaviour on the small payloads this ever sees.
        List<CorrectionRecord> selected = unresolved
            .Where(WorthAsking)
            .OrderByDescending(Prior)
            .Take(MaxAsks)
            .ToList();

        // Mixed-payload rules:
        //  (a) pair-dedupe merge-teach asks — two occurrences of "sri ram" in one dictation must
        //      produce ONE card, not two;
        //  (b) if any normal (paste-holding) ask is selected, DROP the merge-teach asks from this
        //      publish WITHOUT spending their one shot — a teach card must never ride along.
        bool hasNormalAsk = selected.Any(r => !(r.Shape == "merge" && r.Outcome == "kept"));
        var seenMergePairs = new HashSet<string>();
        selected = selected.Where(r =>
        {
            if (r.Shape != "merge" || r.Outcome != "kept") return true;
            if (hasNormalAsk) return false;
            return seenMergePairs.Add(PairKey(r));
        }).ToList();

        return selected.Select(r => new Selection(
            r,
            MergeTeachEligible(r),
            r.Alternates is { Count: > 0 } a ? a[0].Term : null,
            r.Alternates is { Count: > 0 } b ? b[0].Find : null)).ToList();
    }
}
