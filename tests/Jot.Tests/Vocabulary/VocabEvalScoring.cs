using System;
using System.Collections.Generic;
using System.Linq;
using Jot.Vocabulary;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The measures both vocabulary evals score with — E5 (English, corrector vs spotter) and E6
/// (per-language brake strength). ONE definition of "a word", "the transcript contains this term",
/// word error rate and the TP/FP classes, because the whole point of E6 is comparing a language's
/// false-apply rate against E5's English baseline, and two harnesses that fold text differently are
/// not comparing anything.
/// </summary>
internal static class VocabEvalScoring
{
    /// <summary>Whitespace split, letters and digits only, lowercased — the fold both experiments
    /// count words on.</summary>
    public static string[] Words(string text) =>
        [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
              .Select(w => new string([.. w.Where(char.IsLetterOrDigit)]).ToLowerInvariant())
              .Where(w => w.Length > 0)];

    /// <summary>Whole-word (or word-window) containment on the same fold the gate keys on.</summary>
    public static bool Contains(string[] words, string phrase)
    {
        string[] want = Words(phrase);
        if (want.Length == 0) return false;
        for (int i = 0; i + want.Length <= words.Length; i++)
        {
            bool all = true;
            for (int k = 0; k < want.Length && all; k++) all = words[i + k] == want[k];
            if (all) return true;
        }
        return false;
    }

    /// <summary>How far the closest thing the engine actually wrote is from the term, on the gate's own
    /// axis — the number that says whether a miss was reachable at all.</summary>
    public static double NearestGap(string[] hypWords, string term)
    {
        double best = double.MaxValue;
        for (int w = 1; w <= 2; w++)
        {
            for (int i = 0; i + w <= hypWords.Length; i++)
                best = Math.Min(best, VocabularyGate.Gap(string.Join(' ', hypWords[i..(i + w)]), term, []));
        }
        return best;
    }

    /// <summary>
    /// What one applied correction was: <c>TP</c> when the term really was spoken and the span it
    /// replaced is not itself a word of the reference; <c>FP-absent</c> when the term was never said at
    /// all; <c>FP-overwrote</c> when the term WAS said but we replaced text the engine had already got
    /// right — the failure the gate exists to prevent, and the one worth counting separately.
    /// </summary>
    public static string Classify(string[] refWords, string term, string originalWord) =>
        !Contains(refWords, term) ? "FP-absent"
        : Contains(refWords, originalWord) ? "FP-overwrote"
        : "TP";

    public static int WordErrors(string[] reference, string[] hypothesis)
    {
        int[] prev = new int[hypothesis.Length + 1];
        int[] cur = new int[hypothesis.Length + 1];
        for (int j = 0; j <= hypothesis.Length; j++) prev[j] = j;
        for (int i = 1; i <= reference.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= hypothesis.Length; j++)
            {
                int cost = reference[i - 1] == hypothesis[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[hypothesis.Length];
    }
}
