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
/// D6 — THE HARD INVARIANT: vocabulary may never break a dictation.
///
/// <c>StopAndDeliverAsync</c> wraps everything in one try whose catch plays an error sound and fires
/// <c>Failed</c> — and reaches NEITHER <c>_store.Add</c> NOR the paste. So an unguarded throw in the
/// vocabulary block loses the user's text outright. The failure that actually kills the app, though,
/// is a HANG: it throws nothing, so a bare try/catch leaves <c>State == Transcribing</c>, which both
/// <c>Toggle</c> and <c>PressToStart</c> ignore — dictation dead until restart, silently.
///
/// These tests drive the REAL delivery path (state machine, deadline, store, paste) and assert both
/// exit paths per failure mode, because "the original transcript reaches PasteAtCursor" is vacuous
/// with AutoPaste off.
/// </summary>
public class RecorderVocabularyD6Tests : IDisposable
{
    // A single-word near-miss on purpose: ApplyFromDetections places one detection onto one
    // whitespace-delimited word, so a split "nemo tron" is a different (merge-shaped) case.
    private const string Transcript = "met with neumotron about the launch";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "jot-d6-" + Guid.NewGuid().ToString("N"));

    public RecorderVocabularyD6Tests()
    {
        // These tests throw on purpose and the D6 handler logs the stack trace. Point JotLog at the
        // temp root so deliberate failures don't land in the real user's jot.log — which is the file
        // Send-feedback attaches.
        JotLog.Initialize(() => _root);
    }

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
        public int Errors;
        public void PlayStart() { }
        public void PlayStop() { }
        public void PlayCancel() { }
        public void PlaySuccess() { }
        public void PlayError() => Errors++;
        public void Preview() { }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
            => Task.FromResult(Transcript);
    }

    /// <summary>Spotter that throws — the "easy" half of D6.</summary>
    private sealed class ThrowingSpotter : IVocabularySpotter
    {
        public bool IsReady => true;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
            => throw new InvalidOperationException("onnx session blew up");
    }

    /// <summary>Spotter that never returns — the half a try/catch cannot see.</summary>
    private sealed class HangingSpotter : IVocabularySpotter, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);
        public bool IsReady => true;

        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
        {
            _release.Wait(TimeSpan.FromSeconds(30));   // released by Dispose; bounded so a failure can't wedge the run
            return [];
        }

        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    // MARK: - Harness

    private sealed record Harness(
        RecorderController Controller,
        FakeSettingsStore Settings,
        FakeRecordingStore Store,
        SilentSounds Sounds,
        List<string> Pastes,
        List<string> Ready,
        List<(string Title, string Message)> Failures);

    private Harness Build(IVocabularySpotter spotter, bool autoPaste)
    {
        var settings = new FakeSettingsStore();
        settings.Current.DataDirectory = _root;         // keep stats.json / logs off the real user data
        settings.Current.OfflineCleanupEnabled = false; // isolate the vocabulary block from TextPipeline
        settings.Current.AutoPaste = autoPaste;
        settings.Current.VocabularyEnabled = true;
        settings.Current.Language = "en-US";

        var terms = new VocabularyStore(null);
        terms.Add("Nemotron", ["neumotron"]);

        var runner = new VocabularyRunner(
            settings, terms, new CorrectionStore(null), new CorrectionProvenance(null),
            spotter, EmbeddedCommonWordsProvider.Shared, NoopDiagnosticsSink.Instance);

        var store = new FakeRecordingStore();
        var sounds = new SilentSounds();
        var controller = new RecorderController(
            new AudioRecorder(), new FakeTranscriber(), settings, store, sounds, new UsageStats(settings), runner)
        {
            VocabularyDeadlineMs = 300,   // the timeout path, without adding seconds to every run
        };

        var pastes = new List<string>();
        var ready = new List<string>();
        var failures = new List<(string, string)>();
        controller.PasteOverride = (text, _, _) => { pastes.Add(text); return TextInjector.PasteResult.Pasted; };
        controller.TranscriptReady += t => ready.Add(t);
        controller.Failed += (title, message) => failures.Add((title, message));
        controller.CaptureOverride = () => Task.FromResult((
            new RecordingResult(new float[16000], 16000, Path.Combine(_root, "x.wav"), TimeSpan.FromSeconds(1)),
            Transcript));

        return new Harness(controller, settings, store, sounds, pastes, ready, failures);
    }

    private static void AssertDeliveredUngated(Harness h, bool autoPaste)
    {
        // The transcript is saved, un-gated, exactly once.
        Assert.Equal(Transcript, Assert.Single(h.Store.Items).Transcript);

        // Both delivery paths, stated separately — with AutoPaste off the paste assertion is vacuous.
        if (autoPaste) Assert.Equal(Transcript, Assert.Single(h.Pastes));
        else Assert.Empty(h.Pastes);

        // TranscriptReady fires either way (the pill is a delivery signal, not a paste signal).
        Assert.Equal(Transcript, Assert.Single(h.Ready));

        // The state machine came home, and the failure path was never taken.
        Assert.Equal(RecorderState.Idle, h.Controller.State);
        Assert.Empty(h.Failures);
        Assert.Equal(0, h.Sounds.Errors);
    }

    // MARK: - Failure mode 1 · the spotter throws

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThrowingSpotter_StillDeliversTheUngatedTranscript(bool autoPaste)
    {
        Harness h = Build(new ThrowingSpotter(), autoPaste);
        await h.Controller.StopAndDeliverAsync();
        AssertDeliveredUngated(h, autoPaste);
    }

    // MARK: - Failure mode 2 · the spotter hangs

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HangingSpotter_HitsTheDeadlineAndStillDelivers(bool autoPaste)
    {
        using var spotter = new HangingSpotter();
        Harness h = Build(spotter, autoPaste);
        await h.Controller.StopAndDeliverAsync();
        AssertDeliveredUngated(h, autoPaste);
    }

    // MARK: - The happy path still works

    /// <summary>Control case: with a working spotter the gate DOES rewrite the delivered text, so the
    /// two tests above are proving a fallback rather than a feature that never runs.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkingSpotter_DeliversTheGatedTranscript(bool autoPaste)
    {
        Harness h = Build(new GatingSpotter(), autoPaste);
        await h.Controller.StopAndDeliverAsync();

        const string gated = "met with Nemotron about the launch";
        Assert.Equal(gated, Assert.Single(h.Store.Items).Transcript);
        Assert.Equal(gated, Assert.Single(h.Ready));
        if (autoPaste) Assert.Equal(gated, Assert.Single(h.Pastes));
        else Assert.Empty(h.Pastes);
        Assert.Equal(RecorderState.Idle, h.Controller.State);
    }

    private sealed class GatingSpotter : IVocabularySpotter
    {
        public bool IsReady => true;
        public IReadOnlyList<VocabularyGate.Detection> Spot(
            float[] samples, int sampleRate, IReadOnlyList<VocabularyTerm> terms, CancellationToken ct)
            => [new VocabularyGate.Detection("Nemotron", [], -3f, 0.4, 0.7)];
    }

    /// <summary>Vocabulary off is the shipping default, so it gets its own case: no gate, no
    /// provenance, and the delivery path is byte-identical to a build without the feature.</summary>
    [Fact]
    public async Task VocabularyOff_DeliversExactlyAsBefore()
    {
        Harness h = Build(new ThrowingSpotter(), autoPaste: true);
        h.Settings.Current.VocabularyEnabled = false;

        await h.Controller.StopAndDeliverAsync();
        AssertDeliveredUngated(h, autoPaste: true);
    }
}
