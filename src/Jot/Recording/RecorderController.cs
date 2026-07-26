using System.IO;
using Jot.Delivery;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Services.Ai;
using Jot.Transcription;

namespace Jot.Recording;

/// <summary>Pipeline state, surfaced to the tray and the status pill.</summary>
public enum RecorderState { Idle, Recording, Transcribing }

/// <summary>
/// Owns the record → transcribe → paste state machine. The UI (tray + pill) subscribes to state
/// rather than orchestrating. Transcription runs off the UI thread; the paste hops back onto the STA
/// dispatcher (clipboard requires it). While recording, a global Esc hotkey is armed that STOPS AND
/// SAVES the dictation (never discards — a stray Esc, e.g. dismissing a dialog mid-recording, must not
/// lose the recording; see worklist D8); it's released the moment recording ends.
/// </summary>
public sealed class RecorderController : IDisposable
{
    private readonly AudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly ISettingsStore _settings;
    private readonly IRecordingStore _store;
    private readonly ISoundService _sound;
    private readonly UsageStats _stats;
    private readonly LiveTranscription? _live;   // null if the engine can't stream
    private readonly Vocabulary.VocabularyRunner? _vocabulary;
    private bool _liveActive;                     // is this recording being live-streamed?
    private IntPtr _originWindow;                 // the app that was focused when this recording began
    private GlobalHotkey? _stopHotkey;            // Esc, armed only while recording — stops AND saves

    public RecorderController(AudioRecorder recorder, ITranscriber transcriber,
        ISettingsStore settings, IRecordingStore store, ISoundService sound, UsageStats stats,
        Vocabulary.VocabularyRunner? vocabulary = null)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _settings = settings;
        _store = store;
        _sound = sound;
        _stats = stats;
        _vocabulary = vocabulary;
        if (transcriber is IStreamingTranscriber streaming)
        {
            _live = new LiveTranscription(recorder, streaming);
            _live.PartialReady += text => PartialTranscript?.Invoke(text);
        }
    }

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public AudioRecorder Recorder => _recorder;

    public event Action<RecorderState>? StateChanged;
    public event Action<string>? TranscriptReady;

    /// <summary>
    /// The applied vocabulary corrections for the dictation whose <see cref="TranscriptReady"/> fires
    /// NEXT. A SEPARATE event rather than a widened <see cref="TranscriptReady"/> on purpose: the
    /// rewrite pipeline routes its own result into the same pill handler
    /// (<c>PillController.Attach</c>), and rewrite output never passes the gate. A consume-once slot
    /// fed only from here is what stops a rewrite inheriting the previous dictation's chip.
    /// Fired on every delivered dictation, EMPTY list included, so the slot is always fresh.
    /// </summary>
    public event Action<IReadOnlyList<Vocabulary.VocabularyCorrection>>? CorrectionsReady;
    public event Action<string>? PartialTranscript;   // live-caption partial (background thread)
    public event Action<string, string>? Failed;      // (title, message)
    public event Action<string, string>? Notice;      // (title, message) — info, e.g. "copied, press Ctrl+V"
    public event Action? NothingTranscribed;
    public event Action? Cancelled;                   // discard path (Cancel()); not wired to any key today

    /// <summary>Hotkey / tray entry point: start if idle, stop+deliver if recording, ignore while busy.</summary>
    public async void Toggle()
    {
        switch (State)
        {
            case RecorderState.Idle: Start(); break;
            case RecorderState.Recording: await StopAndDeliverAsync(); break;
            case RecorderState.Transcribing: break; // busy
        }
    }

    private void Start()
    {
        try
        {
            // Toggle / PressToStart are global and reachable while an ask card is up, so force-resolve
            // any live deck BEFORE anything else — a deck left hanging holds a paste that never lands.
            Controls.AskCardWindow.ForceResolveLive();
            // Remember where the user is typing so we can deliver the transcript back there.
            _originWindow = Delivery.TextInjector.CaptureForegroundWindow();
            _recorder.Start(_settings.Current.InputDeviceId);
            SetState(RecorderState.Recording);
            _sound.PlayStart();
            _liveActive = _settings.Current.LiveCaptions && _live is not null;
            if (_liveActive) _live!.Start();
            ArmStopHotkey();
            Log($"--- start (live={_liveActive}, device={_settings.Current.TranscriptionDevice}) ---");
        }
        catch (Exception ex)
        {
            _sound.PlayError();
            Failed?.Invoke("Couldn't start recording", ex.Message);
        }
    }

    /// <summary>
    /// Esc entry point while recording: STOP AND SAVE (mirrors the Mac app). Routed to the normal
    /// stop→transcribe→save path so a stray Esc can never lose the recording — the whole point of D8.
    /// </summary>
    private void StopAndSaveFromHotkey()
    {
        if (State != RecorderState.Recording) return;
        DeferStop("stop-and-save fired (Esc)");
    }

    // Defer off the hotkey's WndProc / hook callback: StopAndDeliverAsync disarms (disposes) the Esc
    // hotkey window synchronously, and destroying an HwndSource from inside a message dispatch is a
    // re-entrancy hazard. Posting to the dispatcher runs it after the current dispatch returns.
    private void DeferStop(string why)
    {
        Log(why);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null) dispatcher.BeginInvoke(new Action(() => _ = StopAndDeliverAsync()));
        else _ = StopAndDeliverAsync();
    }

    // push-to-talk hold

    /// <summary>True while the current recording was started by holding the push-to-talk key — the pill
    /// swaps its stop hint to "release to finish". Cleared by <see cref="SetState"/> the moment the
    /// state leaves Recording (Esc, auto-stop, delivery), so a later key-release can never double-stop.</summary>
    public bool HoldActive { get; private set; }

    private DateTime _holdPressedAt;
    private const int TapToToggleMs = 250; // a quick tap converts the press into a normal toggle-on

    private const int SlowStopMs = 5_000;  // stop→delivery beyond this reads as "hung" to a user
    private bool _slowStopNoticeShown;     // the slow-stop feedback nudge fires at most once per launch

    /// <summary>Push-to-talk key DOWN. Idle → start recording (hold begins). Already recording (toggle
    /// or a previous tap) → stop + deliver, exactly like the toggle chord. Transcribing → ignored.</summary>
    public void PressToStart()
    {
        switch (State)
        {
            case RecorderState.Idle:
                // Arm BEFORE Start: SetState(Recording) fires the pill's hint refresh, which must
                // already see HoldActive to show "Release … to stop" instead of the toggle chord.
                HoldActive = true;
                _holdPressedAt = DateTime.UtcNow;
                Start();
                if (State != RecorderState.Recording) HoldActive = false; // Start failed (mic missing)
                break;
            case RecorderState.Recording:
                DeferStop("push-to-talk pressed while recording — stop+deliver");
                break;
            case RecorderState.Transcribing:
                break; // busy
        }
    }

    /// <summary>Push-to-talk key UP. Held long enough → stop + deliver. A quick tap (&lt; 250 ms) keeps
    /// recording as a toggle (next press stops). No-ops whenever the hold isn't live anymore — Esc won,
    /// delivery already ran, or the press never started a recording.</summary>
    public void ReleaseToStop()
    {
        if (!HoldActive || State != RecorderState.Recording) return;
        if ((DateTime.UtcNow - _holdPressedAt).TotalMilliseconds < TapToToggleMs)
        {
            HoldActive = false; // tap → behaves like the toggle chord from here on
            Log("push-to-talk tap — staying in recording (toggle semantics)");
            return;
        }
        DeferStop("push-to-talk released — stop+deliver");
    }

    /// <summary>Discards the in-flight recording without transcribing or pasting. Kept as an API for a
    /// future explicit "discard" affordance; deliberately NOT bound to Esc anymore (Esc now saves).</summary>
    public async void Cancel()
    {
        if (State != RecorderState.Recording) return;
        Log("cancel fired — discarding recording");
        DisarmStopHotkey();
        try
        {
            if (_liveActive && _live is not null) await _live.CancelAsync();
            await Task.Run(_recorder.Discard);
        }
        catch (Exception ex)
        {
            LogSuppressed(ex);
        }
        finally
        {
            _liveActive = false;
            SetState(RecorderState.Idle);
            _sound.PlayCancel();
            Cancelled?.Invoke();
        }
    }

    internal async Task StopAndDeliverAsync()
    {
        DisarmStopHotkey();
        SetState(RecorderState.Transcribing);
        _sound.PlayStop();
        var stopSw = System.Diagnostics.Stopwatch.StartNew(); // hotkey-release → text delivered
        try
        {
            // Drop any proposals a previous run stashed but never saved, BEFORE this dictation can
            // add its own — otherwise an aborted run's list commits under this transcript's id.
            _vocabulary?.ClearPending();

            (RecordingResult result, string text) = CaptureOverride is not null
                ? await CaptureOverride()
                : await CaptureAndTranscribeAsync();

            // Deterministic on-device tidy (filler/casing/numbers) before the empty gate, so an all-filler
            // dictation still routes to NothingTranscribed. isNemotron:true — the wired engine is Nemotron.
            if (_settings.Current.OfflineCleanupEnabled)
                text = Jot.Text.TextPipeline.Clean(text, _settings.Current.Language, isNemotron: true);

            // VOCABULARY. Deliberately the LAST thing that may touch `text`: nothing mutates it between
            // here and _store.Add / PasteAtCursor, so the gate's PublishedStart/PublishedLength anchors
            // are exact against the string that is both saved and pasted. Do not insert a transform below.
            // The ONE sanctioned mutation inside this call is the ask card's revert splice, which the
            // provenance reconcile absorbs exactly (its anchor baseline is the pre-splice gate output).
            VocabularyDelivery vocab = await RunVocabularyAsync(text, result);
            text = vocab.Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                Log("NOTHING transcribed (both live and batch empty)");
                NothingTranscribed?.Invoke();
            }
            else
            {
                Models.RecordingItem item = BuildRecording(result, text);
                _store.Add(item);
                _vocabulary?.Commit(item.Id);  // the id only exists once the row is built
                // Answers are banked AFTER the commit — the verdict ledger is keyed by the recording
                // id, which does not exist before it.
                if (vocab.Answers.Count > 0)
                    _vocabulary?.ApplyAskAnswers(item.Id, vocab.Answers, vocab.Splices);
                _stats.RecordDictation(text, result.Duration.TotalSeconds); // on-device usage counters (D2)
                Log($"SAVED: \"{TitleFrom(text)}\" ({text.Length} chars); library items={_store.Items.Count}");
                if (_settings.Current.AutoPaste)
                {
                    JotSettings s = _settings.Current;
                    // "Return to the app I started in": when off, paste into whatever's focused now
                    // (usually still that app — the pill never steals focus). When on, force it back.
                    //
                    // EXCEPT once the ask block has been entered. The card is ACTIVATABLE, so Jot's own
                    // window is foreground at paste time and the IntPtr.Zero path would resolve the
                    // target with GetForegroundWindow() — pasting the transcript into the card, or
                    // nowhere. The flag is set on ENTRY and never cleared for this dictation, so the
                    // error path (a throw while the card is still up) is covered too, not just the
                    // path where a deck ran to completion.
                    IntPtr target = s.ReturnToOrigin || vocab.AskEntered ? _originWindow : IntPtr.Zero;
                    var pr = Paste(text, target, s);
                    // This PC blocks synthetic input (corporate EDR) and the target wasn't a standard editor →
                    // the transcript is on the clipboard; tell the user to paste it manually (a real Ctrl+V works).
                    if (pr == TextInjector.PasteResult.CopiedToClipboard)
                        Notice?.Invoke("Transcript copied — press Ctrl+V to paste",
                            "This PC blocks apps from pasting for you, so Jot put the transcript on your clipboard.");
                }
                _sound.PlaySuccess();
                // Corrections FIRST: the pill's slot is consume-once and TranscriptReady is what
                // spends it.
                CorrectionsReady?.Invoke(vocab.Corrections);
                TranscriptReady?.Invoke(text);
            }

            // Stop-to-delivered wall time in the log every run; the feedback nudge only when it's
            // painful, and only once per launch (a struggling machine must not get nagged per stop).
            stopSw.Stop();
            Log($"stop-to-delivery {stopSw.ElapsedMilliseconds} ms");
            if (stopSw.ElapsedMilliseconds > SlowStopMs && !_slowStopNoticeShown)
            {
                _slowStopNoticeShown = true;
                Notice?.Invoke("That took longer than usual",
                    "If transcription keeps feeling slow, use Send feedback (Help page) so we can see why.");
            }
        }
        catch (Exception ex)
        {
            _sound.PlayError();
            Failed?.Invoke("Transcription failed", ex.Message);
        }
        finally
        {
            _liveActive = false;
            SetState(RecorderState.Idle);
        }
    }

    // Capture + transcribe, split out so the delivery half below can be driven in tests without a live
    // WASAPI device (see CaptureOverride). Behaviour is unchanged from when this was inline.
    private async Task<(RecordingResult Result, string Text)> CaptureAndTranscribeAsync()
    {
        string recordingsDir = JotPaths.RecordingsDir(_settings.Current);
        Directory.CreateDirectory(recordingsDir);
        string wav = Path.Combine(recordingsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.wav");

        // Native streaming: the transcript is already built as the user spoke, so finishing (while
        // the recorder is still capturing, to catch the last words) is near-instant.
        string liveText = "";
        if (_liveActive && _live is not null)
        {
            try { liveText = (await _live.FinishAsync()).Trim(); }
            catch (Exception ex) { Log("live finish failed: " + ex.Message); }
        }
        Log($"stop: liveActive={_liveActive} liveLen={liveText.Length}");

        RecordingResult result = await Task.Run(() => _recorder.Stop(wav));
        Log($"recorded {result.Samples.Length} samples ({result.Duration.TotalSeconds:0.0}s)");

        // CRITICAL: fall back to a full batch decode whenever live captions produced nothing usable
        // (empty string, not just null) — otherwise an empty live result would be delivered as "nothing".
        string text = liveText;
        if (string.IsNullOrWhiteSpace(text))
        {
            Log("live text empty → batch transcribe");
            text = (await _transcriber.TranscribeAsync(result.Samples, result.SampleRate)).Trim();
        }
        Log($"final text len={text.Length}");
        return (result, text);
    }

    /// <summary>Wall-clock budget for the whole vocabulary block. Under <see cref="SlowStopMs"/> on
    /// purpose: a stop that trips the "took longer than usual" nudge has already failed the user.
    /// A field rather than a const so the D6 timeout test can prove the path without adding four
    /// seconds to every run.</summary>
    internal int VocabularyDeadlineMs { get; set; } = 4_000;

    /// <summary>
    /// D6, THE HARD INVARIANT: vocabulary may never break a dictation.
    ///
    /// A throw is the easy half — <c>StopAndDeliverAsync</c>'s own catch skips BOTH <c>_store.Add</c>
    /// and the paste, so an unguarded throw here loses the user's text outright. The half that
    /// actually kills the app is a HANG: it throws nothing, so a bare try/catch would leave
    /// <c>State == Transcribing</c>, which <c>PressToStart</c>/<c>Toggle</c> both ignore — dictation
    /// dead until restart, silently. Hence the deadline as well as the catch.
    ///
    /// Every exit path returns the UN-GATED transcript, so delivery continues either way.
    /// </summary>
    private async Task<VocabularyDelivery> RunVocabularyAsync(string text, RecordingResult result)
    {
        if (_vocabulary is null) return VocabularyDelivery.Ungated(text);
        bool askEntered = false;
        try
        {
            if (!_vocabulary.ShouldRun) return VocabularyDelivery.Ungated(text);

            using var cts = new CancellationTokenSource();
            Task<Vocabulary.VocabularyRunner.Outcome> run = Task.Run(
                () => _vocabulary.Run(text, result.Samples, result.SampleRate, result.Duration, cts.Token));

            if (await Task.WhenAny(run, Task.Delay(VocabularyDeadlineMs)) != run)
            {
                cts.Cancel();
                // Observe the abandoned task so a late throw can't resurface as an unobserved
                // exception on the finalizer thread.
                _ = run.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                Log($"vocabulary exceeded {VocabularyDeadlineMs} ms — delivering ungated text");
                return VocabularyDelivery.Ungated(text);
            }

            Vocabulary.VocabularyRunner.Outcome outcome = await run;
            if (outcome.Deck.Count == 0)
                return new VocabularyDelivery(outcome.Text, outcome.Corrections, [], AskEntered: false);

            askEntered = true;   // set BEFORE the card can exist — see the paste-target comment above
            IReadOnlyList<Vocabulary.AskAnswer> answers = await RunAskAsync(outcome.Deck);

            // The ONE sanctioned text mutation between the gate and _store.Add. Only a "keep
            // original" answer changes anything, resolved strictly at the recorded anchor and
            // skipped outright if it does not resolve — corrupting the pasted text is worse than
            // leaving the default.
            Vocabulary.AskDeck.SpliceResult spliced = Vocabulary.AskDeck.ApplyAnswers(outcome.Text, answers);
            return new VocabularyDelivery(spliced.Text, outcome.Corrections, answers, AskEntered: true)
            {
                Splices = spliced.Splices,
            };
        }
        catch (Exception ex)
        {
            JotLog.Error("vocabulary failed — delivering ungated text", ex);
            return VocabularyDelivery.Ungated(text) with { AskEntered = askEntered };
        }
        finally
        {
            // A throw (or a hang the deadline abandoned) can leave a Topmost, ACTIVATABLE window in
            // front of the paste. Close it here, on every path, before delivery.
            Controls.AskCardWindow.ForceResolveLive();
        }
    }

    /// <summary>What one dictation's vocabulary block produced, including whether the ask block was
    /// entered — which is what forces the paste target (§4.3 requirement 1).</summary>
    private readonly record struct VocabularyDelivery(
        string Text,
        IReadOnlyList<Vocabulary.VocabularyCorrection> Corrections,
        IReadOnlyList<Vocabulary.AskAnswer> Answers,
        bool AskEntered)
    {
        /// <summary>What the ask splice changed, reported to the ledger so the review rows survive
        /// a revert.</summary>
        public IReadOnlyList<Vocabulary.AskDeck.AskSplice> Splices { get; init; } = [];

        public static VocabularyDelivery Ungated(string text) => new(text, [], [], false);
    }

    /// <summary>
    /// Run the ask deck on the UI thread and wait for it. The card owns its own resolution and
    /// ALWAYS completes (answer, Esc, click-away, timeout, a new recording) — but a hang throws
    /// nothing, so it is backstopped by a wall clock derived from the deck's OWN countdown rather
    /// than a second, competing timeout with a different number.
    ///
    /// With no <c>Application.Current</c> there is no UI host at all (tests, --datapaths), so there
    /// is no card and no ask — the gated text is delivered unchanged.
    /// </summary>
    private async Task<IReadOnlyList<Vocabulary.AskAnswer>> RunAskAsync(
        IReadOnlyList<Vocabulary.AskPolicy.Selection> deck)
    {
        Task<IReadOnlyList<Vocabulary.AskAnswer>> ask;
        if (AskOverride is not null)
        {
            ask = AskOverride(deck);
        }
        else
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return [];
            ask = await dispatcher.InvokeAsync(() => Controls.AskCardWindow.RunAsync(deck, _originWindow));
        }

        // The card's OWN countdown, expressed as a wall clock. Not a second competing timeout with a
        // different number — the same budget, enforced from outside, because a hang throws nothing
        // and the D6 catch would never see it.
        int ceiling = (deck.Count * AskCardMs) + 2_000;
        if (await Task.WhenAny(ask, Task.Delay(ceiling)) != ask)
        {
            Log($"ask deck exceeded {ceiling} ms — delivering the gated text unchanged");
            _ = ask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            return [];
        }
        return await ask;
    }

    /// <summary>Per-card countdown. A field rather than a const so the hang test can prove the
    /// ceiling without adding ten seconds to every run.</summary>
    internal int AskCardMs { get; set; } = Controls.AskCardWindow.DefaultCardMs;

    /// <summary>Test seam for the ask block, never assigned in the app: the card is a real WPF window
    /// and the deck's rules are what the tests need to drive.</summary>
    internal Func<IReadOnlyList<Vocabulary.AskPolicy.Selection>,
                  Task<IReadOnlyList<Vocabulary.AskAnswer>>>? AskOverride;

    // Test seams, never assigned in the app. The capture half needs a live audio device and the paste
    // half synthesises real keystrokes, so the D6 tests swap these two and drive the REAL delivery
    // path — state machine, deadline, store, paste and all — rather than a copy of it.
    internal Func<Task<(RecordingResult Result, string Text)>>? CaptureOverride;
    internal Func<string, IntPtr, JotSettings, TextInjector.PasteResult>? PasteOverride;

    /// <summary>Test seam: the delivery half is driven without <c>Start()</c>, which is what normally
    /// captures the origin window — so the forced-paste-target assertion would otherwise compare two
    /// zeros and prove nothing.</summary>
    internal IntPtr OriginWindowForTests { set => _originWindow = value; }

    private TextInjector.PasteResult Paste(string text, IntPtr target, JotSettings s) =>
        PasteOverride is not null
            ? PasteOverride(text, target, s)
            : TextInjector.PasteAtCursor(text, target, s.KeepInClipboard, s.AutoEnter,
                TextInjector.ParsePasteMethod(s.PasteMethod));

    /// <summary>The stop-&amp;-save shortcut is deliberately fixed to Esc and not user-editable: a
    /// rebind field can never capture Esc (it cancels the capture), and Esc is the natural "stop" key.
    /// Kept as a constant so the Shortcuts page, the pill hint, and this arming logic can't drift.</summary>
    public const string StopRecordingChord = "Escape";

    private void ArmStopHotkey()
    {
        DisarmStopHotkey();
        if (!HotkeyChord.TryParse(StopRecordingChord, out HotkeyChord chord))
        {
            Log($"stop-hotkey: could not parse '{StopRecordingChord}'");
            return;
        }
        try
        {
            _stopHotkey = new GlobalHotkey(chord.Modifiers, chord.VirtualKey, id: 9);
            _stopHotkey.Pressed += StopAndSaveFromHotkey;
            Log($"stop-hotkey armed: {chord} registered={_stopHotkey.IsRegistered}");
        }
        catch (Exception ex)
        {
            // The stop key being unavailable shouldn't stop recording — just skip Esc-to-stop.
            _stopHotkey = null;
            Log($"stop-hotkey FAILED to register ({chord}): {ex.Message}");
        }
    }

    private void DisarmStopHotkey()
    {
        try { _stopHotkey?.Dispose(); } catch { /* best effort — never surface a disposal fault */ }
        _stopHotkey = null;
    }

    private Models.RecordingItem BuildRecording(RecordingResult result, string transcript) => new()
    {
        Kind = Models.RecordingKind.Dictation,
        CreatedAt = DateTime.Now,
        DurationSeconds = result.Duration.TotalSeconds,
        WavPath = result.WavPath,
        ModelLabel = "Nemotron",
        Title = TitleFrom(transcript),
        Transcript = transcript,
        Status = Models.RecordingStatus.Complete,
    };

    private static string TitleFrom(string transcript)
    {
        // Split on ALL whitespace incl. newlines: Nemotron emits paragraph breaks, and a newline inside
        // the title would make the SAVED log line span multiple physical lines — which broke the
        // feedback report's per-line transcript redaction (it failed open). Keep titles single-line.
        string[] words = transcript.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Dictation";
        string title = string.Join(' ', words.Take(6));
        return words.Length > 6 ? title + "…" : title;
    }

    private void SetState(RecorderState state)
    {
        if (state != RecorderState.Recording) HoldActive = false; // single choke point — every exit from
                                                                  // Recording ends the hold, whoever caused it
        State = state;
        StateChanged?.Invoke(state);
    }

    private static void LogSuppressed(Exception ex) => JotLog.Error("suppressed during recording", ex);

    // Dictation trace so a failed save is never a black box: each stage goes to the shared JotLog.
    private static void Log(string message) => JotLog.Info(message);

    public void Dispose()
    {
        DisarmStopHotkey();
        _live?.CancelAsync().GetAwaiter().GetResult();
        _recorder.Dispose();
    }
}
