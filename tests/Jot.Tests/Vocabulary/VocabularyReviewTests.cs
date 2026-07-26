using System.Collections.ObjectModel;
using System.IO;
using Jot.Models;
using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.ViewModels;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests.Vocabulary;

/// <summary>
/// The review + learn block (ux §5) and the right-click "Add to Vocabulary…" gesture (ux §2.7).
///
/// The anchor cases are the point. Four writers mutate the transcript — SaveEdit, ReplaceNext,
/// ReplaceAll and this feature's own right-click add — and all but the first set the one-way
/// <c>IsEdited</c> latch, which is why "hide the block when edited" is NOT the rule. What must hold
/// is: the rows survive the edits whose geometry is recoverable, they are HIDDEN rather than
/// mis-anchored when they are not, and when nothing lines up at all the block degrades to one honest
/// line instead of silently emptying itself.
/// </summary>
public class VocabularyReviewTests : IDisposable
{
    private const string Gated = "met with Nemotron about the launch";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jot-review-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ } }

    // MARK: - Fakes

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public JotSettings Current { get; } = new();
        public void Save() { }
        public void Reset() { }
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class FakeRecordingStore : IRecordingStore
    {
        public ObservableCollection<RecordingItem> Items { get; } = [];
        public void Add(RecordingItem item) => Items.Add(item);
        public void Delete(RecordingItem item) => Items.Remove(item);
        public void Rename(RecordingItem item, string title) => item.Title = title;
        public IReadOnlyList<string> AllTags() => [];
    }

    private sealed class FakeNavigator : INavigator
    {
        public object? Parameter => null;
        public void Navigate(Type pageType, object? parameter = null) { }
        public void GoBack() { }
    }

    private sealed class NoModelSpotter : IVocabularySpotter
    {
        public bool IsReady => false;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct) => [];
        public TermSpottability CheckTerm(string? term) => VocabularyTermRules.CheckLength(term);
    }

    // MARK: - Harness

    private static VocabularyGate.Proposal Proposal(
        string original, string term, string outcome, int start, int publishedStart) =>
        new(original, term, outcome == "applied" ? "APPLY" : "BLOCK", outcome,
            0.85f, 0f, false, true, 0, start, original.Length, publishedStart, term.Length, [], null);

    private sealed record Harness(
        RecordingItem Item,
        VocabularyReviewViewModel Review,
        CorrectionStore Corrections,
        CorrectionProvenance Provenance,
        VocabularyStore Terms,
        FakeSettingsStore Settings);

    private Harness Build(
        string gatedText = Gated,
        RecordingKind kind = RecordingKind.Dictation,
        params VocabularyGate.Proposal[] proposals)
    {
        var settings = new FakeSettingsStore();
        settings.Current.Language = "en-US";
        var terms = new VocabularyStore(null);
        terms.Add("Nemotron", ["neumotron"]);
        var corrections = new CorrectionStore(_root);
        var provenance = new CorrectionProvenance(_root);

        var item = new RecordingItem { Kind = kind, Transcript = gatedText };
        if (proposals.Length > 0)
        {
            provenance.Record(proposals, gatedText);
            provenance.Commit(item.Id);
        }

        var review = new VocabularyReviewViewModel(item, provenance, corrections, terms, settings);
        return new Harness(item, review, corrections, provenance, terms, settings);
    }

    private static int NetOf(CorrectionStore store, string original, string term)
    {
        foreach (OverrideEntry o in store.Snapshot())
        {
            if (o.OriginalWord == CorrectionKey.Normalize(original)
                && CorrectionKey.Lowercased(o.Term) == CorrectionKey.Lowercased(term))
                return o.Net;
        }
        return 0;
    }

    // MARK: - Rendering

    [Fact]
    public void NoProposals_MeansNoBlock()
    {
        Harness h = Build();
        Assert.False(h.Review.IsVisible);
        Assert.Empty(h.Review.Rows);
    }

    /// <summary>A rewrite row never passed the gate, and neither does a demo/sample row: the guard is
    /// structural — no provenance, no block — so it cannot be forgotten.</summary>
    [Fact]
    public void ARewriteRow_NeverRendersTheBlock()
    {
        Harness h = Build("some rewritten text", RecordingKind.Rewrite,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));
        Assert.False(h.Review.IsVisible);
    }

    [Fact]
    public void AnAppliedProposal_RendersAChangedRowWithContextAndTheTermInText()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        Assert.True(h.Review.IsVisible);
        VocabularyReviewRow row = Assert.Single(h.Review.Rows);
        Assert.Equal("CHANGED", row.Badge);
        Assert.Equal("Original \"neumotron\"", row.OriginalLabel);
        Assert.Equal("Nemotron", row.ContextWord);
        Assert.True(row.TermInText);
        Assert.False(row.OriginalInText);
        Assert.True(row.IsUnresolved);
        Assert.Equal("Jot guessed on 1 word", h.Review.HeaderText);
        Assert.Equal("Pick the word you meant.", h.Review.HeaderSubtitle);
    }

    /// <summary>A blocked near-miss is visible ONLY here — no pill chip, no ask card (D8). That makes
    /// this row load-bearing rather than decorative.</summary>
    [Fact]
    public void ABlockedProposal_RendersAKeptRowWithTheOriginalInText()
    {
        Harness h = Build("the lista is short", RecordingKind.Dictation,
            Proposal("lista", "Lisa", "kept", 4, 4));

        VocabularyReviewRow row = Assert.Single(h.Review.Rows);
        Assert.Equal("KEPT", row.Badge);
        Assert.True(row.OriginalInText);
    }

    // MARK: - Picks

    [Fact]
    public void PickingTheOriginalOnAChangedRow_RevertsTheTextAndDemotesThePair()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        h.Review.PickOriginalCommand.Execute(h.Review.Rows[0]);

        Assert.Equal("met with neumotron about the launch", h.Item.Transcript);
        Assert.True(h.Item.IsEdited);
        Assert.Equal(-1, NetOf(h.Corrections, "neumotron", "Nemotron"));

        VocabularyReviewRow row = Assert.Single(h.Review.Rows);
        Assert.True(row.IsResolved);
        Assert.Equal("neumotron restored.", row.ResolvedText);
        Assert.True(h.Review.AllReviewed);
        Assert.Equal("All reviewed", h.Review.HeaderText);
    }

    /// <summary>The canonical "teach the missed term": one gesture applies it here AND arms the
    /// override for a rare/OOV original.</summary>
    [Fact]
    public void PickingTheTermOnAKeptRow_AppliesItAndTeachesThePair()
    {
        Harness h = Build("met with neumotron about the launch", RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "kept", 9, 9));

        h.Review.PickTermCommand.Execute(h.Review.Rows[0]);

        Assert.Equal(Gated, h.Item.Transcript);
        Assert.Equal(1, NetOf(h.Corrections, "neumotron", "Nemotron"));
        Assert.Equal("Nemotron applied here.", Assert.Single(h.Review.Rows).ResolvedText);
    }

    [Fact]
    public void PickingTheTermOnAChangedRow_ConfirmsItWithoutTouchingTheText()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        h.Review.PickTermCommand.Execute(h.Review.Rows[0]);

        Assert.Equal(Gated, h.Item.Transcript);
        Assert.False(h.Item.IsEdited);   // nothing changed, so no edit latch
        Assert.Equal(1, NetOf(h.Corrections, "neumotron", "Nemotron"));
        Assert.Equal("Nemotron confirmed.", Assert.Single(h.Review.Rows).ResolvedText);
    }

    [Fact]
    public void Undo_RestoresTheGatesOwnOutcomeAndClearsWhatWasLearned()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        h.Review.PickOriginalCommand.Execute(h.Review.Rows[0]);
        Assert.Equal(-1, NetOf(h.Corrections, "neumotron", "Nemotron"));

        h.Review.UndoPickCommand.Execute(h.Review.Rows[0]);

        Assert.Equal(Gated, h.Item.Transcript);
        Assert.Equal(0, NetOf(h.Corrections, "neumotron", "Nemotron"));
        Assert.True(Assert.Single(h.Review.Rows).IsUnresolved);
    }

    /// <summary>One transcript contributes at most ±1 per mapping, so adjudicating three occurrences
    /// of the same pair cannot inflate the net to 3.</summary>
    [Fact]
    public void ThreeOccurrencesOfOnePair_ContributeAtMostOneToTheNet()
    {
        const string text = "neumotron and neumotron and neumotron";
        Harness h = Build(text, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "kept", 0, 0),
            Proposal("neumotron", "Nemotron", "kept", 14, 14),
            Proposal("neumotron", "Nemotron", "kept", 28, 28));

        Assert.Equal(3, h.Review.Rows.Count);
        Assert.Equal([1, 2, 3], h.Review.Rows.Select(r => r.Occurrence));

        foreach (string key in h.Review.Rows.Select(r => r.Record.Key).ToList())
        {
            VocabularyReviewRow? row = h.Review.Rows.FirstOrDefault(r => r.Record.Key == key);
            if (row is not null) h.Review.PickTermCommand.Execute(row);
        }

        Assert.Equal("Nemotron and Nemotron and Nemotron", h.Item.Transcript);
        Assert.Equal(1, NetOf(h.Corrections, "neumotron", "Nemotron"));
    }

    // MARK: - The anchor cases

    /// <summary>ReplaceNext's geometry IS recoverable, so the rows must survive it and still point at
    /// the right words — this is the ordinary case the whole reconcile exists for.</summary>
    [Fact]
    public void ReplaceNext_LeavesTheRowsPointingAtTheRightWords()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        // Exactly what ReplaceNext does: one splice, at a known index, before the anchor.
        h.Item.Transcript = h.Item.Transcript.Replace("met with", "spoke to", StringComparison.Ordinal);
        h.Item.IsEdited = true;
        h.Review.Reload();

        Assert.False(h.Review.AnchorsStale);
        Assert.Equal("Nemotron", Assert.Single(h.Review.Rows).ContextWord);
    }

    /// <summary>ReplaceAll is N sites and N deltas and the app never computes the indices — but the
    /// reconcile's diff maps them anyway, and anything it cannot is hidden rather than mis-anchored.
    /// What must NEVER happen is a row that edits the wrong occurrence.</summary>
    [Fact]
    public void ReplaceAll_NeverLeavesARowPointingAtTheWrongOccurrence()
    {
        const string text = "Nemotron then Nemotron again";
        Harness h = Build(text, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 0, 0),
            Proposal("neumotron", "Nemotron", "applied", 14, 14));

        h.Item.Transcript = text.Replace("then", "and then", StringComparison.Ordinal);
        h.Item.IsEdited = true;
        h.Review.Reload();

        foreach (VocabularyReviewRow row in h.Review.Rows)
        {
            Assert.Equal("Nemotron",
                h.Item.Transcript.Substring(row.Span.Start, row.Span.Length));
        }
    }

    /// <summary>A free-form rewrite has no geometry at all. The block must NOT vanish — that is what
    /// "hide when IsEdited" would do, permanently, because the latch is one-way — it must say why it
    /// can't help.</summary>
    [Fact]
    public void SaveEdit_DegradesToTheHonestNoteRatherThanVanishing()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        h.Item.Transcript = "an entirely different sentence about something else";
        h.Item.IsEdited = true;
        h.Review.Reload();

        Assert.True(h.Review.IsVisible);      // still there
        Assert.True(h.Review.AnchorsStale);   // but honest about what it can do
        Assert.Empty(h.Review.Rows);
    }

    /// <summary>Stale anchors must not merely stop OFFERING picks — they must stop TAKING them, or a
    /// stray click would splice at an offset nobody can vouch for.</summary>
    [Fact]
    public void StaleAnchors_RefuseToEditTheTranscript()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));
        VocabularyReviewRow row = h.Review.Rows[0];

        h.Item.Transcript = "an entirely different sentence about something else";
        h.Review.Reload();
        Assert.True(h.Review.AnchorsStale);

        h.Review.PickOriginalCommand.Execute(row);

        Assert.Equal("an entirely different sentence about something else", h.Item.Transcript);
        Assert.Equal(0, NetOf(h.Corrections, "neumotron", "Nemotron"));
    }

    /// <summary>Learning is keyed on the PAIR, not the offset, so a stale transcript loses the review
    /// rows and nothing else.</summary>
    [Fact]
    public void StaleAnchors_DoNotLoseWhatWasAlreadyTaught()
    {
        Harness h = Build(Gated, RecordingKind.Dictation,
            Proposal("neumotron", "Nemotron", "applied", 9, 9));

        h.Review.PickTermCommand.Execute(h.Review.Rows[0]);
        Assert.Equal(1, NetOf(h.Corrections, "neumotron", "Nemotron"));

        h.Item.Transcript = "wholly rewritten";
        h.Review.Reload();

        Assert.True(h.Review.AnchorsStale);
        Assert.Equal(1, NetOf(h.Corrections, "neumotron", "Nemotron"));
    }

    // MARK: - Language honesty

    [Fact]
    public void NonEnglish_AnswersTheQuestionWhereItIsAsked()
    {
        Harness h = Build();
        h.Settings.Current.Language = "es-ES";
        h.Review.Reload();

        Assert.True(h.Review.ShowLanguageNote);
        Assert.Contains("English only", h.Review.LanguageNote, StringComparison.Ordinal);

        h.Settings.Current.Language = "auto";
        h.Review.Reload();
        Assert.Contains("Auto detect", h.Review.LanguageNote, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTerms_MeansNoLanguageNoteEither()
    {
        Harness h = Build();
        h.Terms.Terms.Clear();
        h.Settings.Current.Language = "es-ES";
        h.Review.Reload();

        Assert.False(h.Review.ShowLanguageNote);
    }

    // MARK: - §2.7 · right-click "Add to Vocabulary…"

    private RecordingDetailViewModel BuildDetail(RecordingItem item, out CorrectionStore corrections,
        out VocabularyStore terms, out FakeSettingsStore settings)
    {
        settings = new FakeSettingsStore();
        settings.Current.Language = "en-US";
        settings.Current.VocabularyEnabled = true;
        terms = new VocabularyStore(null);
        corrections = new CorrectionStore(_root);
        var services = new VocabularyServices(
            terms, corrections, new CorrectionProvenance(_root), settings, new NoModelSpotter());
        return new RecordingDetailViewModel(item, new FakeRecordingStore(), new FakeNavigator(), services);
    }

    /// <summary>The flagship gesture's THREE effects, in one action.</summary>
    [Fact]
    public void AddToVocabulary_FixesThisTranscriptCreatesTheTermAndRecordsThePlusOne()
    {
        var item = new RecordingItem { Transcript = "met with neumotron about the launch" };
        RecordingDetailViewModel vm = BuildDetail(item, out CorrectionStore corrections,
            out VocabularyStore terms, out _);

        vm.AddToVocabulary(9, "neumotron".Length, "neumotron", "Nemotron");

        Assert.Equal("met with Nemotron about the launch", item.Transcript);
        Assert.True(item.IsEdited);
        VocabularyTerm term = Assert.Single(terms.Terms);
        Assert.Equal("Nemotron", term.Text);
        Assert.Equal(["neumotron"], term.Aliases);
        Assert.Equal(1, NetOf(corrections, "neumotron", "Nemotron"));
    }

    /// <summary>iOS scar tissue: "that's the 'what is this?' test — ordinary rewording isn't
    /// vocabulary." Fix the text, create nothing, and SAY so.</summary>
    [Fact]
    public void AddToVocabulary_OnAnEverydayPhrase_FixesTheTextButCreatesNoTerm()
    {
        var item = new RecordingItem { Transcript = "the lista is short" };
        RecordingDetailViewModel vm = BuildDetail(item, out CorrectionStore corrections,
            out VocabularyStore terms, out _);

        string status = vm.AddToVocabulary(4, "lista".Length, "lista", "list");

        Assert.Equal("the list is short", item.Transcript);
        Assert.Empty(terms.Terms);
        Assert.Equal(0, NetOf(corrections, "lista", "list"));
        Assert.Equal("Fixed here. \"list\" is an everyday phrase, so it wasn't added to your vocabulary.", status);
    }

    [Theory]
    [InlineData("Nemotron", true)]
    [InlineData("one two three four", true)]
    [InlineData("one two three four five", false)]   // > MaxTermWords
    [InlineData("   ", false)]
    [InlineData("123", false)]                        // no letters
    public void CanAddToVocabulary_GatesOnAPlausibleTerm(string selection, bool expected)
    {
        var item = new RecordingItem { Transcript = "anything" };
        RecordingDetailViewModel vm = BuildDetail(item, out _, out _, out _);
        Assert.Equal(expected, vm.CanAddToVocabulary(selection));
    }

    [Fact]
    public void CanAddToVocabulary_IsFalseForATermAlreadyInTheList()
    {
        var item = new RecordingItem { Transcript = "anything" };
        RecordingDetailViewModel vm = BuildDetail(item, out _, out VocabularyStore terms, out _);
        terms.Add("Nemotron", ["neumotron"]);

        Assert.False(vm.CanAddToVocabulary("Nemotron"));
        Assert.False(vm.CanAddToVocabulary("neumotron"));   // an alias claims it too
        Assert.True(vm.CanAddToVocabulary("Ideaflow"));
    }

    /// <summary>"Saved, but nothing will use it yet" is the case the dialog exists to be honest
    /// about — every one of these is a state where the term IS stored and does nothing.</summary>
    [Theory]
    [InlineData("en-US", false, "Custom vocabulary is off — turn it on in Settings to use this.")]
    [InlineData("es-ES", true, "Saved. Vocabulary only applies when your language is set to English.")]
    [InlineData("auto", true, "Saved. Vocabulary needs your language set to English — it's on Auto detect.")]
    [InlineData("en-US", true, "Saved. Jot downloads the vocabulary model (about 130 MB) the first time you use it.")]
    public void AddToVocabularyStatus_SaysWhatWillActuallyHappen(string language, bool enabled, string expected)
    {
        var item = new RecordingItem { Transcript = "anything" };
        RecordingDetailViewModel vm = BuildDetail(item, out _, out _, out FakeSettingsStore settings);
        settings.Current.Language = language;
        settings.Current.VocabularyEnabled = enabled;

        Assert.Equal(expected, vm.AddToVocabularyStatus());
    }

    // MARK: - §2.7 · the splice must not eat what surrounded the selection

    /// <summary>Drives the gesture the way <c>RecordingDetailPage.OnAddToVocabulary</c> does: the
    /// heard form is the RAW <c>SelectedText</c> for the same offsets the splice gets, so a selection
    /// that carries WPF's trailing space is exercised end to end rather than described.</summary>
    private string AddViaSelection(string transcript, int start, int length, string typedTerm)
    {
        var item = new RecordingItem { Transcript = transcript };
        RecordingDetailViewModel vm = BuildDetail(item, out _, out _, out _);
        vm.AddToVocabulary(start, length, transcript.Substring(start, length), typedTerm);
        return item.Transcript;
    }

    /// <summary>
    /// The owner's exact case. WPF's double-click word selection INCLUDES the trailing whitespace,
    /// so the selection is "Venith " (7) — and the pre-fix splice replaced all 7 with the trimmed
    /// "Vineet", publishing "My name is VineetSriram." The space and the period must both survive.
    /// </summary>
    [Fact]
    public void AddToVocabulary_WithWpfsTrailingSpaceInTheSelection_KeepsTheSpaceAndThePeriod()
    {
        const string transcript = "My name is Venith Sriram.";
        Assert.Equal("Venith ", transcript.Substring(11, 7));   // the selection WPF actually hands us

        Assert.Equal("My name is Vineet Sriram.", AddViaSelection(transcript, 11, 7, "Vineet"));
    }

    /// <summary>Every boundary the trim has to get right. The replacement owns the WORD and nothing
    /// else — leading/trailing spaces, newlines and punctuation outside it are untouched.</summary>
    [Theory]
    // start of transcript, with WPF's trailing space
    [InlineData("Venith is here", 0, 7, "Vineet", "Vineet is here")]
    // end of transcript — there is no trailing space to preserve
    [InlineData("hello Venith", 6, 6, "Vineet", "hello Vineet")]
    // followed by punctuation: no whitespace to trim, and the comma is not ours to touch
    [InlineData("Venith, hello", 0, 6, "Vineet", "Vineet, hello")]
    // followed by a newline — a line break is whitespace too, and eating it would join two lines
    [InlineData("Venith\nhello", 0, 7, "Vineet", "Vineet\nhello")]
    // multi-word selection: internal whitespace IS part of what was picked, the edges are not
    [InlineData("call Sri Ram now", 5, 8, "Sriram", "call Sriram now")]
    // leading whitespace (a drag that started early) is trimmed from the front as well
    [InlineData("call Venith now", 4, 8, "Vineet", "call Vineet now")]
    // a whitespace-only selection replaces nothing at all
    [InlineData("call Venith now", 4, 1, "Vineet", "call Venith now")]
    public void AddToVocabulary_TrimsTheSelectionToTheWordItReplaces(
        string transcript, int start, int length, string typedTerm, string expected)
    {
        Assert.Equal(expected, AddViaSelection(transcript, start, length, typedTerm));
    }
}
