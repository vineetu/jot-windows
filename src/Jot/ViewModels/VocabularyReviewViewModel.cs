using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Models;
using Jot.Services.Abstractions;
using Jot.Vocabulary;

namespace Jot.ViewModels;

/// <summary>One occurrence the gate adjudicated, as the review block draws it. Per-occurrence and
/// never grouped: "the same surface word can legitimately differ by location — 'cloud code' may
/// really be *cloud code* in one spot and *claude code* in another."</summary>
public sealed class VocabularyReviewRow
{
    public required CorrectionRecord Record { get; init; }

    /// <summary>Where the word currently sits in the LIVE transcript (UTF-16), strictly resolved.</summary>
    public required VocabularySplice.Span Span { get; init; }

    /// <summary>The word actually in the text right now — the one carrying the IN TEXT tag.</summary>
    public required string InTextWord { get; init; }

    public required int Occurrence { get; init; }

    public string OriginalWord => Record.OriginalWord;
    public string Term => Record.Term;

    /// <summary>The gate's own outcome, which the badge reports. Not the same thing as the owner's
    /// verdict — a CHANGED row can still be reverted, and it stays CHANGED.</summary>
    public bool IsChanged => Record.Outcome == "applied";
    public string Badge => IsChanged ? "CHANGED" : "KEPT";
    public string OriginalLabel => $"Original \"{Record.OriginalWord}\"";

    public string ContextBefore { get; init; } = "";
    public string ContextWord { get; init; } = "";
    public string ContextAfter { get; init; } = "";

    /// <summary>Null while unresolved; "term" / "original" once the owner has picked.</summary>
    public string? Verdict { get; init; }
    public bool IsResolved => Verdict is not null;
    public bool IsUnresolved => Verdict is null;

    public bool OriginalInText => CorrectionKey.Normalize(InTextWord) == CorrectionKey.Normalize(OriginalWord);
    public bool TermInText => !OriginalInText;

    /// <summary>CorrectionCopy.resolvedParts, verbatim from the Mac.</summary>
    public string ResolvedText => Verdict switch
    {
        "term" when IsChanged => $"{Term} confirmed.",
        "term" => $"{Term} applied here.",
        "original" when IsChanged => $"{OriginalWord} restored.",
        "original" => $"{OriginalWord} kept.",
        _ => "",
    };

    public string OriginalChipName => $"Use {OriginalWord} — {Badge} occurrence {Occurrence}";
    public string TermChipName => $"Use {Term} — {Badge} occurrence {Occurrence}";
}

/// <summary>
/// The review + learn block under the transcript (ux §5) — "Jot guessed on N words" → one row per
/// occurrence → pick the word you meant → the transcript edits and the learned net moves.
///
/// Split out of <see cref="RecordingDetailViewModel"/> deliberately: every rule here (strict
/// resolution, the stale degrade, what a pick does to the ledger) is testable with no MediaPlayer,
/// no navigator and no window.
///
/// THE ANCHOR STORY. Offsets are exact at save time and stale the moment anything edits the
/// transcript, and FOUR writers do (SaveEdit, ReplaceNext, ReplaceAll, and this feature's own
/// right-click add). "Hide the block when IsEdited" is not the answer: all three of the latter set
/// that one-way latch, so the rule would delete the review surface forever on any Find &amp; Replace.
/// Instead every read goes through <see cref="CorrectionProvenance.ReconciledPayload"/>, which
/// fingerprints the text the anchors belong to and re-maps them through a real diff when it differs
/// — and our OWN edits report their exact span through
/// <see cref="CorrectionProvenance.NoteSelfEdit"/>, because a diff is genuinely ambiguous when the
/// replacement shares a suffix with the replaced word. Anything that still fails STRICT resolution
/// is hidden, never nearest-matched; if nothing resolves at all the block degrades to one honest
/// line instead of silently vanishing.
/// </summary>
public sealed partial class VocabularyReviewViewModel : ObservableObject
{
    private readonly RecordingItem _item;
    private readonly CorrectionProvenance _provenance;
    private readonly CorrectionStore _corrections;
    private readonly VocabularyStore _terms;
    private readonly ISettingsStore _settings;

    private int _recordCount;

    public ObservableCollection<VocabularyReviewRow> Rows { get; } = [];

    /// <summary>Raised with a UTF-16 span so the page can select it in the transcript box. The
    /// "flash" is the WPF selection highlight: a read-only TextBox cannot do styled runs at all, and
    /// selection scrolls the span into view for free.</summary>
    public event Action<int, int>? FlashRequested;

    public VocabularyReviewViewModel(
        RecordingItem item,
        CorrectionProvenance provenance,
        CorrectionStore corrections,
        VocabularyStore terms,
        ISettingsStore settings)
    {
        _item = item;
        _provenance = provenance;
        _corrections = corrections;
        _terms = terms;
        _settings = settings;
        Reload();
    }

    // MARK: - Visibility

    /// <summary>The review card renders only for a real dictation that this feature actually
    /// adjudicated. Demo/sample rows never went through the gate, so they never have provenance and
    /// can never render one — the guard is structural, not a flag check.</summary>
    public bool IsVisible => _item.Kind == RecordingKind.Dictation && _recordCount > 0;

    /// <summary>Answers "why didn't it fix my name?" where the question is actually asked. Shown
    /// instead of a review section when the user has terms but the language rules vocabulary out.</summary>
    public bool ShowLanguageNote =>
        _item.Kind == RecordingKind.Dictation
        && _recordCount == 0
        && _terms.Terms.Count > 0
        && !VocabularyRunner.LanguageSupported(_settings.Current.Language);

    public string LanguageNote =>
        NemotronLocales_IsAuto()
            ? "Custom vocabulary needs your language set to English — it's on Auto detect."
            : "Custom vocabulary works in English only right now, so Jot didn't apply your terms here.";

    private bool NemotronLocales_IsAuto() =>
        Transcription.Nemotron.NemotronLocales.Normalize(_settings.Current.Language)
            .Equals(Transcription.Nemotron.NemotronLocales.AutoCode, StringComparison.OrdinalIgnoreCase);

    // MARK: - Header

    public int UnresolvedCount => Rows.Count(r => r.IsUnresolved);
    public bool AllReviewed => !AnchorsStale && UnresolvedCount == 0;

    public string HeaderText => AllReviewed
        ? "All reviewed"
        : $"Jot guessed on {UnresolvedCount} {(UnresolvedCount == 1 ? "word" : "words")}";

    public string HeaderSubtitle => AllReviewed ? "" : "Pick the word you meant.";
    public bool HasHeaderSubtitle => HeaderSubtitle.Length > 0;

    /// <summary>
    /// True when the ledger holds records but NOT ONE of them still resolves against the live
    /// transcript — a free-form edit that rewrote it, or a change too large for the diff to map.
    /// The block then degrades honestly instead of quietly emptying itself.
    ///
    /// Resolved rows and the learned nets are unaffected: learning is keyed on the PAIR, not the
    /// offset, so nothing the owner already taught is lost.
    /// </summary>
    [ObservableProperty] private bool _anchorsStale;

    /// <summary>Bound, not duplicated in XAML: two copies of this sentence is one copy that drifts.</summary>
    public string StaleNote => "You edited this transcript, so Jot can't line up its guesses any more.";

    // MARK: - Load

    public void Reload()
    {
        Rows.Clear();
        _recordCount = 0;

        if (_item.Kind != RecordingKind.Dictation)
        {
            RaiseAll();
            return;
        }

        CorrectionProvenance.Payload payload = _provenance.ReconciledPayload(_item.Id, _item.Transcript);
        _recordCount = payload.Records.Count;

        var perPair = new Dictionary<string, int>(StringComparer.Ordinal);
        int resolved = 0;
        foreach (CorrectionRecord record in payload.Records)
        {
            string? verdict = payload.Verdicts.GetValueOrDefault(record.Key);

            // What SHOULD be sitting at the anchor: the verdict's word if the owner has picked one,
            // otherwise whatever the gate left behind.
            string expected = verdict switch
            {
                "term" => record.Term,
                "original" => record.OriginalWord,
                _ => record.Outcome == "applied" ? record.Term : record.OriginalWord,
            };

            if (!VocabularySplice.TryResolve(_item.Transcript, record.PublishedStart, expected,
                    out VocabularySplice.Span span))
            {
                // A row that can't be located is HIDDEN. Never nearest-matched: iOS deleted their
                // fallback after it "routinely resolved — sometimes onto the wrong occurrence", and
                // a review row that edits the wrong word is data loss.
                continue;
            }

            resolved++;
            perPair[record.MappingKey] = perPair.GetValueOrDefault(record.MappingKey) + 1;
            VocabularySplice.Context ctx = VocabularySplice.ContextAround(_item.Transcript, span);
            Rows.Add(new VocabularyReviewRow
            {
                Record = record,
                Span = span,
                InTextWord = _item.Transcript.Substring(span.Start, span.Length),
                Occurrence = perPair[record.MappingKey],
                Verdict = verdict,
                ContextBefore = ctx.Before,
                ContextWord = ctx.Word,
                ContextAfter = ctx.After,
            });
        }

        AnchorsStale = _recordCount > 0 && resolved == 0;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(ShowLanguageNote));
        OnPropertyChanged(nameof(LanguageNote));
        OnPropertyChanged(nameof(UnresolvedCount));
        OnPropertyChanged(nameof(AllReviewed));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(HasHeaderSubtitle));
    }

    // MARK: - Verdicts

    /// <summary>Pick the term for this occurrence. On a KEPT row that is the canonical "teach the
    /// missed term" — text change plus +1, which arms the override immediately for a rare/OOV
    /// original. (For a COMMON-word original it arms nothing, permanently — <c>Decide</c> step (0)
    /// requires <c>!isCommon</c> — so the UI promises no automation state at all.)</summary>
    [RelayCommand]
    private void PickTerm(VocabularyReviewRow? row) => Pick(row, useTerm: true);

    /// <summary>Pick the original. On a CHANGED row this is the overcorrection fix: it reverts the
    /// text AND records −1. Two different thresholds, and both matter — at net ≤ −1 the pair is
    /// actively blocked; at net 0 it is merely no longer armed.</summary>
    [RelayCommand]
    private void PickOriginal(VocabularyReviewRow? row) => Pick(row, useTerm: false);

    private void Pick(VocabularyReviewRow? row, bool useTerm)
    {
        if (row is null || AnchorsStale) return;
        string desired = useTerm ? row.Term : row.OriginalWord;
        ApplyVerdict(row, useTerm ? "term" : "original", desired);
    }

    /// <summary>Undo reverses symmetrically: the text goes back to what the GATE left, and the
    /// verdict (and this transcript's contribution to the net) is cleared.</summary>
    [RelayCommand]
    private void UndoPick(VocabularyReviewRow? row)
    {
        if (row is null || AnchorsStale) return;
        string gateWord = row.IsChanged ? row.Term : row.OriginalWord;
        ApplyVerdict(row, verdict: null, gateWord);
    }

    private void ApplyVerdict(VocabularyReviewRow row, string? verdict, string desiredWord)
    {
        // Re-resolve against the LIVE text rather than trusting the cached span: an edit made since
        // this row was built would otherwise splice at a stale offset.
        if (!VocabularySplice.TryResolve(_item.Transcript, row.Record.PublishedStart, row.InTextWord,
                out VocabularySplice.Span span))
        {
            Reload();
            return;
        }

        if (CorrectionKey.Normalize(row.InTextWord) != CorrectionKey.Normalize(desiredWord))
        {
            string oldText = _item.Transcript;
            string newText = VocabularySplice.Replace(oldText, span, desiredWord);
            _item.Transcript = newText;
            _item.IsEdited = true;

            // OUR OWN edit, so report the exact span instead of leaving it to the blind diff: when
            // the replacement shares a suffix with the replaced word ("nathan" → "Ramanathan") the
            // diff is ambiguous and can shift this record's anchor off its own word, breaking Undo.
            _provenance.NoteSelfEdit(
                _item.Id,
                row.Record.Key,
                VocabularySplice.GraphemeIndex(newText, span.Start),
                VocabularySplice.GraphemeLength(row.InTextWord),
                VocabularySplice.GraphemeLength(desiredWord),
                newText);

            FlashRequested?.Invoke(span.Start, desiredWord.Length);
        }

        IReadOnlyList<CorrectionProvenance.MappingDelta> deltas = verdict is null
            ? _provenance.ClearVerdict(_item.Id, row.Record)
            : _provenance.SetVerdict(_item.Id, row.Record, verdict);
        foreach (CorrectionProvenance.MappingDelta d in deltas)
            _corrections.Adjust(d.OriginalWord, d.Term, d.Delta);

        Reload();
    }
}
