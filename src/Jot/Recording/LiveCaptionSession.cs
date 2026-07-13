using Jot.Transcription;

namespace Jot.Recording;

/// <summary>
/// Produces live-caption partials while the user is dictating. Parakeet here is an offline
/// (full-utterance) model, so streaming is done by re-decoding a trailing window of the audio every
/// so often: snapshot the mic buffer, transcribe the last <see cref="_windowSeconds"/> seconds, and
/// raise <see cref="PartialReady"/>. For a typical (sub-window) dictation the window is the whole
/// utterance, so partials simply grow and stabilise; the authoritative full transcript is still the
/// one produced on stop. Self-paced: each pass waits for the previous decode plus a small gap, so it
/// never queues up or falls behind, and it backs off the CPU between updates.
/// </summary>
public sealed class LiveCaptionSession
{
    private const int SampleRate = 16_000;

    private readonly AudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly int _windowSeconds;
    private readonly int _gapMs;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Raised on a background thread with the latest partial transcript.</summary>
    public event Action<string>? PartialReady;

    public LiveCaptionSession(AudioRecorder recorder, ITranscriber transcriber,
        int windowSeconds = 30, int gapMs = 250)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _windowSeconds = windowSeconds;
        _gapMs = gapMs;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Cancels the loop and waits for any in-flight decode to unwind before returning.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch { /* the loop swallows its own errors; nothing to surface here */ }
        finally { _cts.Dispose(); _cts = null; _loop = null; }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        int lastLen = 0;
        while (!ct.IsCancellationRequested)
        {
            float[]? samples = _recorder.SnapshotSamples();

            // Wait for at least ~0.4s of audio, and for meaningful new audio since the last pass.
            if (samples is null || samples.Length < SampleRate * 2 / 5 ||
                (lastLen > 0 && samples.Length - lastLen < SampleRate / 4))
            {
                if (!await Delay(_gapMs, ct)) break;
                continue;
            }
            lastLen = samples.Length;

            int window = SampleRate * _windowSeconds;
            float[] slice = samples.Length <= window ? samples : samples[^window..];

            string text;
            try
            {
                text = await _transcriber.TranscribeAsync(slice, SampleRate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { if (!await Delay(_gapMs, ct)) break; continue; }

            if (ct.IsCancellationRequested) break;
            if (text.Length > 0) PartialReady?.Invoke(text);

            if (!await Delay(_gapMs, ct)) break;
        }
    }

    /// <summary>Cancellation-safe delay; returns false if cancelled (so the caller stops the loop).</summary>
    private static async Task<bool> Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
