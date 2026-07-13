using System.IO;
using Jot.Delivery;
using Jot.Services.Abstractions;
using Jot.Transcription;

namespace Jot.Recording;

/// <summary>Pipeline state, surfaced to the tray and (Phase 2) the status pill.</summary>
public enum RecorderState { Idle, Recording, Transcribing }

/// <summary>
/// Owns the record → transcribe → paste state machine that used to live inline in App.
/// Extracted so the UI (tray now, pill next) can subscribe to state instead of the app
/// orchestrating everything. Transcription runs off the UI thread; the paste hops back onto
/// the STA dispatcher (clipboard requires it). The recorder itself exposes a live RMS level
/// (Phase 2 waveform) via <see cref="AudioRecorder"/>.
/// </summary>
public sealed class RecorderController : IDisposable
{
    private static readonly string RecordingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot", "recordings");

    private readonly AudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly ISettingsStore _settings;
    private readonly IRecordingStore _store;
    private readonly LiveCaptionSession _live;
    private IntPtr _originWindow; // the app that was focused when this recording began

    public RecorderController(AudioRecorder recorder, ITranscriber transcriber,
        ISettingsStore settings, IRecordingStore store)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _settings = settings;
        _store = store;
        _live = new LiveCaptionSession(recorder, transcriber);
        _live.PartialReady += text => PartialTranscript?.Invoke(text);
    }

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public AudioRecorder Recorder => _recorder;

    public event Action<RecorderState>? StateChanged;
    public event Action<string>? TranscriptReady;
    public event Action<string>? PartialTranscript;   // live-caption partial (background thread)
    public event Action<string, string>? Failed;      // (title, message)
    public event Action? NothingTranscribed;

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
            if (_settings.Current.LiveCaptions)
                _live.Start();
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Couldn't start recording", ex.Message);
        }
    }

    private async Task StopAndDeliverAsync()
    {
        SetState(RecorderState.Transcribing);
        await _live.StopAsync(); // end live captioning before the authoritative final decode
        try
        {
            string wav = Path.Combine(RecordingsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            RecordingResult result = await Task.Run(() => _recorder.Stop(wav));
            string text = (await _transcriber.TranscribeAsync(result.Samples, result.SampleRate)).Trim();

            if (string.IsNullOrWhiteSpace(text))
                NothingTranscribed?.Invoke();
            else
            {
                _store.Add(BuildRecording(result, text));
                if (_settings.Current.AutoPaste)
                    TextInjector.PasteAtCursor(text, _originWindow);
                TranscriptReady?.Invoke(text);
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Transcription failed", ex.Message);
        }
        finally
        {
            SetState(RecorderState.Idle);
        }
    }

    private static Models.RecordingItem BuildRecording(RecordingResult result, string transcript) => new()
    {
        Kind = Models.RecordingKind.Dictation,
        CreatedAt = DateTime.Now,
        DurationSeconds = result.Duration.TotalSeconds,
        WavPath = result.WavPath,
        ModelLabel = "Parakeet",
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

    public void Dispose()
    {
        _live.StopAsync().GetAwaiter().GetResult();
        _recorder.Dispose();
    }
}
