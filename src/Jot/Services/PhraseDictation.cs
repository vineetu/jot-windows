using System.IO;
using Jot.Recording;
using Jot.Services.Abstractions;
using Jot.Transcription;

namespace Jot.Services;

/// <summary>Records a short spoken phrase and returns it as text — the "speak" side of the prompt-augment
/// field. An interface so the picker's auto-mic + type-takeover state machine can be unit-tested with a
/// fake (the real one needs an audio device).</summary>
public interface IPhraseDictation
{
    /// <summary>Live caption of the phrase so far (background thread — marshal before touching UI).</summary>
    event Action<string>? Partial;

    /// <summary>Begin capturing. False if already listening or the recorder is otherwise busy.</summary>
    bool Start();

    /// <summary>Stop capturing and return the transcribed phrase (trimmed; "" if nothing was heard).</summary>
    Task<string> StopAsync();
}

/// <summary>
/// Dictates one short spoken phrase into a text field — the "speak" half of the prompt-augment input
/// (e.g. saying "Japanese" for Translate). Deliberately the same primitives the dictation and voice-rewrite
/// paths use — <see cref="AudioRecorder"/>, the streaming <see cref="LiveTranscription"/> for a live caption,
/// and the engine's batch <see cref="ITranscriber.TranscribeAsync"/> as fallback — just orchestrated to
/// RETURN the text instead of running a rewrite. Nothing here duplicates the engine; it reuses it.
///
/// One capture at a time. Callers only start this while idle (the picker is open, nothing else recording),
/// so it doesn't guard against a concurrent dictation beyond a simple busy check.
/// </summary>
public sealed class PhraseDictation : IPhraseDictation
{
    private readonly AudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly ISettingsStore _settings;
    private readonly LiveTranscription? _live;   // null when the engine can't stream — batch decode still works
    private bool _active;

    /// <summary>Live caption of the phrase so far (background thread — marshal before touching UI).</summary>
    public event Action<string>? Partial;

    public PhraseDictation(AudioRecorder recorder, ITranscriber transcriber, ISettingsStore settings)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _settings = settings;
        if (transcriber is IStreamingTranscriber streaming)
        {
            _live = new LiveTranscription(recorder, streaming);
            _live.PartialReady += text => Partial?.Invoke(text);
        }
    }

    public bool IsListening => _active;

    /// <summary>Begin capturing. False if already listening or the recorder is otherwise busy.</summary>
    public bool Start()
    {
        if (_active || _recorder.IsRecording) return false;
        _recorder.Start(_settings.Current.InputDeviceId);
        // Live caption is best-effort: a streaming hiccup just means we fall back to the batch decode on stop.
        try { if (_settings.Current.LiveCaptions && _live is not null) _live.Start(); }
        catch { /* stream failed to arm — StopAsync still returns the batch transcription */ }
        _active = true;
        return true;
    }

    /// <summary>Stop capturing and return the transcribed phrase (trimmed; "" if nothing was heard).</summary>
    public async Task<string> StopAsync()
    {
        if (!_active) return "";
        _active = false;

        string liveText = "";
        if (_live is not null)
        {
            // Finish the stream while the recorder is still capturing so the last word isn't clipped.
            try { liveText = (await _live.FinishAsync()).Trim(); }
            catch (Exception ex) { JotLog.Info("phrase live finish failed: " + ex.Message); }
        }

        string wav = Path.Combine(Path.GetTempPath(), $"jot-phrase-{Guid.NewGuid():N}.wav");
        RecordingResult res = await Task.Run(() => _recorder.Stop(wav));
        string text = liveText;
        if (string.IsNullOrWhiteSpace(text))
            text = (await _transcriber.TranscribeAsync(res.Samples, res.SampleRate)).Trim();
        try { File.Delete(res.WavPath); } catch { /* the phrase audio isn't kept */ }
        return text;
    }
}
