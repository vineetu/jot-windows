using System.Runtime.CompilerServices;
using System.Text;

namespace Jot.Vocabulary;

// NO SWIFT COUNTERPART. Windows-only, and it exists because of a number: E6 measured the
// over-correction brake in 19 languages and found it is a TYPE lookup over a 24 000-entry frequency
// list, so an inflected form the list never lists is invisible to it even when its own lemma is
// listed. See docs/plans/vocabulary-inflection-guard.md.

/// <summary>
/// Every everyday word MINUS its final character. Membership therefore answers one question:
/// <b>"is this span an everyday word with its last character missing?"</b> — `пирамид` against
/// `пирамиды`, `piramid` against `piramida`, `πολιτικέ` against `πολιτικές`, `severe` against
/// `severni`. That is a language's commonest inflectional shape, reachable without a single
/// per-language morphology table, which we do not have and will not ship for nineteen languages.
///
/// ONE character, and only in this direction. Both halves are MEASURED on E6's 1207 applied
/// corrections across 20 languages, not chosen:
///
///  * Also allowing the SPAN to be shaved (so "span minus its last character is a list word") widens
///    the class from 21 rows to 63 and turns a 4:17 recall-to-precision trade into 30:33 — most of
///    what it adds is correct corrections, because a mangled rare name lands on some list word's
///    prefix by luck far more often than it lands one character short of a whole one.
///  * Two characters instead of one takes the class from 70 % false applies to 39 %.
///
/// Built lazily, once per common-word set instance, and only for a span that has already passed the
/// cheap structural test — a dictation that proposes nothing never pays for it. Keyed on the set
/// object, which <see cref="EmbeddedCommonWordsProvider"/> caches per language, so this cache has
/// exactly that lifetime and cannot outlive it.
/// </summary>
internal static class CommonWordStems
{
    /// <summary>Shortest span this may fire on. MEASURED: at six it also blocks `wigili` → `Wigilii`
    /// and three more correct corrections to catch two extra false ones; at eight it loses the
    /// `piramid` / `пирамид` rows, which are the only overwrites it catches at all. Seven is where
    /// the rule's precision peaks on this corpus — and the reason it has a floor at all is that a
    /// five-letter prefix is shared by a large slice of any inflecting language's dictionary.</summary>
    public const int MinLength = 7;

    private static readonly ConditionalWeakTable<IReadOnlySet<string>, HashSet<string>> Cache = new();

    /// <summary>Is <paramref name="skeleton"/> an everyday word minus its final character? The
    /// argument must already be a <see cref="VocabularyGate.SkeletonOf"/> sequence — the set is built
    /// on that same axis, so a caller measuring raw text would compare two different alphabets.
    /// </summary>
    public static bool IsEverydayWordMinusItsEnding(
        IReadOnlySet<string> commonWords, IReadOnlyList<Rune> skeleton)
    {
        if (skeleton.Count < MinLength) return false;
        var sb = new StringBuilder(skeleton.Count);
        foreach (Rune r in skeleton) sb.Append(r);
        return Cache.GetValue(commonWords, Build).Contains(sb.ToString());
    }

    private static HashSet<string> Build(IReadOnlySet<string> commonWords)
    {
        // Skeletons on both sides, not raw list entries: the query side is the gate's skeleton (NFC,
        // lowercased, punctuation dropped), and a set built on raw entries would miss every word the
        // list happens to spell with a mark the skeleton folds.
        var stems = new HashSet<string>(commonWords.Count, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string word in commonWords)
        {
            Rune[] skeleton = VocabularyGate.SkeletonOf(word);
            if (skeleton.Length <= MinLength) continue;      // its stem would be under the floor
            sb.Clear();
            for (int i = 0; i < skeleton.Length - 1; i++) sb.Append(skeleton[i]);
            stems.Add(sb.ToString());
        }
        return stems;
    }
}
