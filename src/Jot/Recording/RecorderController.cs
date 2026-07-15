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
    private readonly IAiClient _ai;
    private readonly AiCredentials _credentials;
    private readonly LiveTranscription? _live;   // null if the engine can't stream
    private bool _liveActive;                     // is this recording being live-streamed?
    private IntPtr _originWindow;                 // the app that was focused when this recording began
    private GlobalHotkey? _stopHotkey;            // Esc, armed only while recording — stops AND saves

    public RecorderController(AudioRecorder recorder, ITranscriber transcriber,
        ISettingsStore settings, IRecordingStore store, ISoundService sound,
        IAiClient ai, AiCredentials credentials)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _settings = settings;
        _store = store;
        _sound = sound;
        _ai = ai;
        _credentials = credentials;
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
    public event Action<string>? PartialTranscript;   // live-caption partial (background thread)
    public event Action<string, string>? Failed;      // (title, message)
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
    private async void StopAndSaveFromHotkey()
    {
        if (State != RecorderState.Recording) return;
        Log("stop-and-save fired (Esc)");
        await StopAndDeliverAsync();
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

    private async Task StopAndDeliverAsync()
    {
        DisarmStopHotkey();
        SetState(RecorderState.Transcribing);
        _sound.PlayStop();
        try
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

            if (string.IsNullOrWhiteSpace(text))
            {
                Log("NOTHING transcribed (both live and batch empty)");
                NothingTranscribed?.Invoke();
            }
            else
            {
                text = await MaybeCleanupAsync(text);
                _store.Add(BuildRecording(result, text));
                Log($"SAVED: \"{TitleFrom(text)}\" ({text.Length} chars); library items={_store.Items.Count}");
                if (_settings.Current.AutoPaste)
                {
                    JotSettings s = _settings.Current;
                    // "Return to the app I started in": when off, paste into whatever's focused now
                    // (usually still that app — the pill never steals focus). When on, force it back.
                    IntPtr target = s.ReturnToOrigin ? _originWindow : IntPtr.Zero;
                    TextInjector.PasteAtCursor(text, target, s.KeepInClipboard, s.AutoEnter);
                }
                _sound.PlaySuccess();
                TranscriptReady?.Invoke(text);
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

    // Optional AI cleanup (filler removal / punctuation) when a provider is configured and enabled.
    // Never blocks delivery for long or loses text: on any failure CleanupAsync returns the original.
    private async Task<string> MaybeCleanupAsync(string text)
    {
        JotSettings s = _settings.Current;
        if (!s.CleanupEnabled || s.AiProvider == "None") return text;
        try
        {
            var config = new AiConfig(s.AiProvider,
                string.IsNullOrWhiteSpace(s.AiBaseUrl) ? null : s.AiBaseUrl,
                string.IsNullOrWhiteSpace(s.AiModel) ? null : s.AiModel,
                _credentials.ApiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string cleaned = await _ai.CleanupAsync(text, config, cts.Token);
            return string.IsNullOrWhiteSpace(cleaned) ? text : cleaned.Trim();
        }
        catch
        {
            return text; // cleanup is best-effort; deliver the raw transcript rather than nothing
        }
    }

    // ---- stop hotkey (Esc by default), live only while recording — stops AND saves ----------------

    private void ArmStopHotkey()
    {
        DisarmStopHotkey();
        if (!HotkeyChord.TryParse(_settings.Current.CancelRecordingHotkey, out HotkeyChord chord))
        {
            Log($"stop-hotkey: could not parse '{_settings.Current.CancelRecordingHotkey}'");
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
        _stopHotkey?.Dispose();
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
        string[] words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "Dictation";
        string title = string.Join(' ', words.Take(6));
        return words.Length > 6 ? title + "…" : title;
    }

    private void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private static void LogSuppressed(Exception ex) => JotLog.Error("suppressed during recording", ex);

    // A plain-text dictation trace so a failed "save" is never a black box: each stage of every
    // recording goes to the shared activity log (JotLog) under the user's data folder.
    private static void Log(string message) => JotLog.Info(message);

    public void Dispose()
    {
        DisarmStopHotkey();
        _live?.CancelAsync().GetAwaiter().GetResult();
        _recorder.Dispose();
    }
}
