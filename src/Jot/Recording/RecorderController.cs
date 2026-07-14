using System.IO;
using Jot.Delivery;
using Jot.Services.Abstractions;
using Jot.Services.Ai;
using Jot.Transcription;

namespace Jot.Recording;

/// <summary>Pipeline state, surfaced to the tray and the status pill.</summary>
public enum RecorderState { Idle, Recording, Transcribing }

/// <summary>
/// Owns the record → transcribe → paste state machine. The UI (tray + pill) subscribes to state
/// rather than orchestrating. Transcription runs off the UI thread; the paste hops back onto the STA
/// dispatcher (clipboard requires it). While recording, a global cancel hotkey (Esc by default) is
/// armed so the user can abandon a dictation; it's released the moment recording ends.
/// </summary>
public sealed class RecorderController : IDisposable
{
    private static readonly string RecordingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot", "recordings");

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
    private GlobalHotkey? _cancelHotkey;          // armed only while recording

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
    public event Action? Cancelled;                   // recording abandoned via the cancel hotkey

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
            _recorder.Start();
            SetState(RecorderState.Recording);
            _sound.PlayStart();
            _liveActive = _settings.Current.LiveCaptions && _live is not null;
            if (_liveActive) _live!.Start();
            ArmCancelHotkey();
            Log($"--- start (live={_liveActive}, device={_settings.Current.TranscriptionDevice}) ---");
        }
        catch (Exception ex)
        {
            _sound.PlayError();
            Failed?.Invoke("Couldn't start recording", ex.Message);
        }
    }

    /// <summary>Abandons the in-flight recording without transcribing or pasting (the cancel hotkey).</summary>
    public async void Cancel()
    {
        if (State != RecorderState.Recording) return;
        DisarmCancelHotkey();
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
        DisarmCancelHotkey();
        SetState(RecorderState.Transcribing);
        _sound.PlayStop();
        try
        {
            string wav = Path.Combine(RecordingsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.wav");

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

    // ---- cancel hotkey (Esc by default), live only while recording ------------------------------

    private void ArmCancelHotkey()
    {
        DisarmCancelHotkey();
        if (!HotkeyChord.TryParse(_settings.Current.CancelRecordingHotkey, out HotkeyChord chord)) return;
        try
        {
            _cancelHotkey = new GlobalHotkey(chord.Modifiers, chord.VirtualKey, id: 9);
            _cancelHotkey.Pressed += Cancel;
        }
        catch (Exception ex)
        {
            // The cancel key being unavailable shouldn't stop recording — just skip Esc-to-cancel.
            _cancelHotkey = null;
            LogSuppressed(ex);
        }
    }

    private void DisarmCancelHotkey()
    {
        _cancelHotkey?.Dispose();
        _cancelHotkey = null;
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

    private static void LogSuppressed(Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}  (suppressed) {ex}\n\n");
        }
        catch { /* best effort */ }
    }

    // A plain-text dictation trace so a failed "save" is never a black box: each stage of every
    // recording is appended to %LOCALAPPDATA%\Jot\dictation.log.
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "dictation.log"), $"{DateTime.Now:HH:mm:ss}  {message}\n");
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        DisarmCancelHotkey();
        _live?.CancelAsync().GetAwaiter().GetResult();
        _recorder.Dispose();
    }
}
