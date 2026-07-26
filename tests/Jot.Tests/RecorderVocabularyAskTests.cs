using System.Collections.ObjectModel;
using System.IO;
using Jot.Delivery;
using Jot.Models;
using Jot.Recording;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Jot.Vocabulary;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The ask block on the delivery path (ux §4): the PASTE TARGET, the answers reaching the ledger,
/// and the chip payload.
///
/// The paste target is the one that would ship broken and look fine. <c>ReturnToOrigin</c> defaults
/// to false, so <c>PasteAtCursor</c> normally gets <c>IntPtr.Zero</c> and resolves the target with
/// <c>GetForegroundWindow()</c> AT PASTE TIME — which is safe today only because the pill never
/// activates. The ask card DOES activate, so the moment it can exist, that path would paste the
/// transcript into Jot's own window. The fix is forcing the origin whenever the ask block was
/// ENTERED, error path included; these tests assert exactly that, on both paths.
///
/// D6 is untouched and still owns "vocabulary may never break a dictation"
/// (<see cref="RecorderVocabularyD6Tests"/>); this file only covers what the ask adds.
/// </summary>
public class RecorderVocabularyAskTests : IDisposable
{
    private const string Transcript = "met with neumotron about the launch";
    private const string Gated = "met with Nemotron about the launch";
    private static readonly IntPtr Origin = 4242;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jot-ask-" + Guid.NewGuid().ToString("N"));

    public RecorderVocabularyAskTests() => JotLog.Initialize(() => _root);

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

    private sealed class SilentSounds : ISoundService
    {
        public void PlayStart() { }
        public void PlayStop() { }
        public void PlayCancel() { }
        public void PlaySuccess() { }
        public void PlayError() { }
        public void Preview() { }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
            => Task.FromResult(Transcript);
    }

    /// <summary>Spots the term, so the gate really applies it and the deck really has a card.</summary>
    private sealed class GatingSpotter : IVocabularySpotter
    {
        public bool IsReady => true;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
            => [new VocabularyGate.Detection("Nemotron", [], -3f, 0.4, 0.7)];
        public TermSpottability CheckTerm(string? term) => TermSpottability.Ok;
    }

    private sealed class NoSpotter : IVocabularySpotter
    {
        public bool IsReady => true;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct) => [];
        public TermSpottability CheckTerm(string? term) => TermSpottability.Ok;
    }

    // MARK: - Harness

    private sealed record Harness(
        RecorderController Controller,
        FakeSettingsStore Settings,
        FakeRecordingStore Store,
        CorrectionStore Corrections,
        CorrectionProvenance Provenance,
        List<(string Text, IntPtr Target)> Pastes,
        List<IReadOnlyList<VocabularyCorrection>> Chips,
        List<(string Title, string Message)> Failures);

    private Harness Build(IVocabularySpotter spotter, bool autoPaste = true, bool returnToOrigin = false)
    {
        var settings = new FakeSettingsStore();
        settings.Current.DataDirectory = _root;
        settings.Current.OfflineCleanupEnabled = false;
        settings.Current.AutoPaste = autoPaste;
        settings.Current.ReturnToOrigin = returnToOrigin;
        settings.Current.VocabularyEnabled = true;
        settings.Current.Language = "en-US";

        var terms = new VocabularyStore(null);
        terms.Add("Nemotron", ["neumotron"]);

        var corrections = new CorrectionStore(_root);
        var provenance = new CorrectionProvenance(_root);
        var runner = new VocabularyRunner(settings, terms, corrections, provenance,
            spotter, EmbeddedCommonWordsProvider.Shared, NoopDiagnosticsSink.Instance);

        var store = new FakeRecordingStore();
        var controller = new RecorderController(
            new AudioRecorder(), new FakeTranscriber(), settings, store, new SilentSounds(),
            new UsageStats(settings), runner)
        {
            VocabularyDeadlineMs = 2_000,
            AskCardMs = 300,              // the ceiling path, without adding ten seconds to the run
            OriginWindowForTests = Origin,
        };

        var pastes = new List<(string, IntPtr)>();
        var chips = new List<IReadOnlyList<VocabularyCorrection>>();
        var failures = new List<(string, string)>();
        controller.PasteOverride = (text, target, _) =>
        {
            pastes.Add((text, target));
            return TextInjector.PasteResult.Pasted;
        };
        controller.CorrectionsReady += c => chips.Add(c);
        controller.Failed += (title, message) => failures.Add((title, message));
        controller.CaptureOverride = () => Task.FromResult((
            new RecordingResult(new float[16000], 16000, Path.Combine(_root, "x.wav"), TimeSpan.FromSeconds(1)),
            Transcript));

        return new Harness(controller, settings, store, corrections, provenance, pastes, chips, failures);
    }

    // MARK: - The paste target

    /// <summary>No deck ⇒ nothing activates ⇒ the delivery path is byte-identical to today's, which
    /// is what keeps the overwhelming majority of dictations unchanged.</summary>
    [Fact]
    public async Task NoDeck_LeavesThePasteTargetExactlyAsItWas()
    {
        Harness h = Build(new NoSpotter());
        await h.Controller.StopAndDeliverAsync();

        Assert.Equal(IntPtr.Zero, Assert.Single(h.Pastes).Target);
    }

    [Fact]
    public async Task AskEntered_ForcesTheOriginWindowAsThePasteTarget()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = _ => Task.FromResult<IReadOnlyList<AskAnswer>>([]);

        await h.Controller.StopAndDeliverAsync();

        (string text, IntPtr target) = Assert.Single(h.Pastes);
        Assert.Equal(Gated, text);
        Assert.Equal(Origin, target);
    }

    /// <summary>THE error path. An exception thrown while the card is still up must not re-open the
    /// hazard: the flag is set on ENTRY, not on completion, so the paste still goes to the origin.</summary>
    [Fact]
    public async Task AskThrows_StillPastesToTheOriginAndStillDelivers()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = _ => throw new InvalidOperationException("card blew up");

        await h.Controller.StopAndDeliverAsync();

        // D6 still holds: the UN-gated transcript is saved and delivered rather than lost.
        Assert.Equal(Transcript, Assert.Single(h.Store.Items).Transcript);
        (string text, IntPtr target) = Assert.Single(h.Pastes);
        Assert.Equal(Transcript, text);
        Assert.Equal(Origin, target);
        Assert.Empty(h.Failures);
        Assert.Equal(RecorderState.Idle, h.Controller.State);
    }

    /// <summary>A deck that never completes is a hang, and a hang throws nothing — the ceiling is
    /// what stops dictation dying silently with State stuck at Transcribing.</summary>
    [Fact]
    public async Task AskNeverCompletes_StillDeliversAndComesHome()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = _ => new TaskCompletionSource<IReadOnlyList<AskAnswer>>().Task;

        Task delivery = h.Controller.StopAndDeliverAsync();
        Task finished = await Task.WhenAny(delivery, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(delivery, finished);
        Assert.Single(h.Store.Items);
        Assert.Single(h.Pastes);
        Assert.Equal(RecorderState.Idle, h.Controller.State);
    }

    [Fact]
    public async Task AutoPasteOff_MeansNoPasteAtAll_EvenWithADeck()
    {
        Harness h = Build(new GatingSpotter(), autoPaste: false);
        h.Controller.AskOverride = _ => Task.FromResult<IReadOnlyList<AskAnswer>>([]);

        await h.Controller.StopAndDeliverAsync();

        Assert.Single(h.Store.Items);
        Assert.Empty(h.Pastes);
    }

    // MARK: - Answers

    [Fact]
    public async Task KeepOriginal_RevertsTheSavedAndPastedTextAndDemotesThePair()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = deck => Task.FromResult<IReadOnlyList<AskAnswer>>(
            [.. deck.Select(s => new AskAnswer(s.Record, KeepOriginal: true))]);

        await h.Controller.StopAndDeliverAsync();

        // What is SAVED is what is PASTED — the whole point of splicing before _store.Add.
        Assert.Equal(Transcript, Assert.Single(h.Store.Items).Transcript);
        Assert.Equal(Transcript, Assert.Single(h.Pastes).Text);
        Assert.Equal(-1, NetOf(h.Corrections, "neumotron", "Nemotron"));
    }

    [Fact]
    public async Task UseTheTerm_KeepsTheGatedTextAndTeachesThePair()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = deck => Task.FromResult<IReadOnlyList<AskAnswer>>(
            [.. deck.Select(s => new AskAnswer(s.Record, KeepOriginal: false))]);

        await h.Controller.StopAndDeliverAsync();

        Assert.Equal(Gated, Assert.Single(h.Store.Items).Transcript);
        Assert.Equal(1, NetOf(h.Corrections, "neumotron", "Nemotron"));
    }

    /// <summary>"Once the user answers, don't ask again." Nothing else on this path writes the set
    /// AskPolicy reads, so without the explicit suppression an answered APPLIED pair would come back
    /// on every later dictation forever.</summary>
    [Fact]
    public async Task AnsweringAPair_StopsItBeingAskedEverAgain()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = deck => Task.FromResult<IReadOnlyList<AskAnswer>>(
            [.. deck.Select(s => new AskAnswer(s.Record, KeepOriginal: false))]);

        await h.Controller.StopAndDeliverAsync();

        Assert.Contains(CorrectionKey.PairKey("neumotron", "Nemotron"), h.Corrections.KeyboardSuppressedPairs());
    }

    /// <summary>The live answer must stamp the per-occurrence verdict, or the review surface would
    /// ask the same question a second time.</summary>
    [Fact]
    public async Task AnAnsweredCard_ShowsAsAlreadyResolvedOnTheReviewSurface()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = deck => Task.FromResult<IReadOnlyList<AskAnswer>>(
            [.. deck.Select(s => new AskAnswer(s.Record, KeepOriginal: false))]);

        await h.Controller.StopAndDeliverAsync();

        RecordingItem item = Assert.Single(h.Store.Items);
        CorrectionProvenance.Payload payload = h.Provenance.PayloadFor(item.Id);
        Assert.Equal("term", Assert.Single(payload.Verdicts).Value);
    }

    /// <summary>A revert splice happens BEFORE the commit, so the ledger's anchors must still line up
    /// with the saved transcript — otherwise the review rows for a reverted card would all be hidden.</summary>
    [Fact]
    public async Task AfterARevertSplice_TheReviewAnchorsStillResolve()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = deck => Task.FromResult<IReadOnlyList<AskAnswer>>(
            [.. deck.Select(s => new AskAnswer(s.Record, KeepOriginal: true))]);

        await h.Controller.StopAndDeliverAsync();

        RecordingItem item = Assert.Single(h.Store.Items);
        CorrectionProvenance.Payload payload = h.Provenance.ReconciledPayload(item.Id, item.Transcript);
        CorrectionRecord record = Assert.Single(payload.Records);
        Assert.True(VocabularySplice.TryResolve(item.Transcript, record.PublishedStart, "neumotron", out _));
    }

    // MARK: - The chip payload

    [Fact]
    public async Task CorrectionsReady_CarriesTheAppliedCorrectionsForTheChip()
    {
        Harness h = Build(new GatingSpotter());
        h.Controller.AskOverride = _ => Task.FromResult<IReadOnlyList<AskAnswer>>([]);

        await h.Controller.StopAndDeliverAsync();

        VocabularyCorrection chip = Assert.Single(Assert.Single(h.Chips));
        Assert.Equal("neumotron", chip.OriginalWord);
        Assert.Equal("Nemotron", chip.Term);
        Assert.Equal("Nemotron", VocabularyCorrection.ChipText([chip]));
        Assert.Equal(" Vocabulary used Nemotron.", VocabularyCorrection.AutomationSuffix([chip]));
    }

    /// <summary>Fired on EVERY delivered dictation, empty list included — the pill's slot is
    /// consume-once, so a dictation that corrected nothing must actively clear it.</summary>
    [Fact]
    public async Task CorrectionsReady_FiresEmptyWhenNothingWasCorrected()
    {
        Harness h = Build(new NoSpotter());
        await h.Controller.StopAndDeliverAsync();

        Assert.Empty(Assert.Single(h.Chips));
    }

    [Fact]
    public void ChipText_ShowsTheCountForTwoOrMoreAndTruncatesALongTerm()
    {
        Assert.Equal("2", VocabularyCorrection.ChipText(
            [new VocabularyCorrection("a", "A"), new VocabularyCorrection("b", "B")]));
        Assert.Equal("Supercalifrag…", VocabularyCorrection.ChipText(
            [new VocabularyCorrection("x", "Supercalifragilistic")]));
        Assert.Equal("", VocabularyCorrection.ChipText([]));
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
}
