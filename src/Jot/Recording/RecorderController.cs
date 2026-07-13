using System.IO;
using Jot.Delivery;
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

    public RecorderController(AudioRecorder recorder, ITranscriber transcriber)
    {
        _recorder = recorder;
        _transcriber = transcriber;
    }

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public AudioRecorder Recorder => _recorder;

    public event Action<RecorderState>? StateChanged;
    public event Action<string>? TranscriptReady;
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
            _recorder.Start();
            SetState(RecorderState.Recording);
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Couldn't start recording", ex.Message);
        }
    }

    private async Task StopAndDeliverAsync()
    {
        SetState(RecorderState.Transcribing);
        try
        {
            string wav = Path.Combine(RecordingsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            RecordingResult result = await Task.Run(() => _recorder.Stop(wav));
            string text = await _transcriber.TranscribeAsync(result.Samples, result.SampleRate);

            if (string.IsNullOrWhiteSpace(text))
                NothingTranscribed?.Invoke();
            else
            {
                TextInjector.PasteAtCursor(text);
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

    private void SetState(RecorderState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose() => _recorder.Dispose();
}
