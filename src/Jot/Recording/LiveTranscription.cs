using Jot.Transcription;

namespace Jot.Recording;

/// <summary>
/// Drives live transcription via the transcriber's native streaming: one session stays open and each
/// newly-captured slice is fed exactly once (the model carries context in its cache), so partials grow
/// as you speak and the final transcript is ready the instant you stop.
/// </summary>
public sealed class LiveTranscription
{
    private readonly AudioRecorder _recorder;
    private readonly IStreamingTranscriber _transcriber;
    private readonly int _pollMs;
    private readonly RealtimeGuard _guard;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IStreamingSession? _session;
    private int _consumed;             // samples already fed to the session
    private double _processingSeconds; // wall time spent inside Accept this session
    private bool _degraded;            // streaming proved too slow — batch fallback owns the transcript

    /// <summary>Raised on a background thread with the transcript so far.</summary>
    public event Action<string>? PartialReady;

    public LiveTranscription(AudioRecorder recorder, IStreamingTranscriber transcriber, int pollMs = 300)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _pollMs = pollMs;
        // JOT_DEGRADE_RTF (debug/testing only): lowers ALL the guard's gates so the degrade WIRING can
        // be exercised on a fast machine (a fast machine can never form a real backlog, so only the
        // production thresholds — unit-tested — would otherwise ever fire). e.g. 0.01.
        _guard = double.TryParse(Environment.GetEnvironmentVariable("JOT_DEGRADE_RTF"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double v) && v > 0
            ? new RealtimeGuard(minAudioSeconds: 1.0, rtfThreshold: v, minBacklogSeconds: 0.05)
            : new RealtimeGuard();
    }

    public void Start()
    {
        _session = _transcriber.OpenStream();
        _consumed = 0;
        _processingSeconds = 0;
        _degraded = false;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await Delay(_pollMs, ct)) break;
            try { FeedNew(); } catch { /* a mid-stream hiccup shouldn't kill the loop */ }
        }
    }

    private void FeedNew()
    {
        if (_degraded) return; // batch fallback owns this utterance now
        float[]? all = _recorder.SnapshotSamples();
        if (all is null || all.Length <= _consumed) return;
        float[] delta = all[_consumed..];

        // On a machine that can't stream in realtime, each Accept takes longer than the audio it feeds,
        // so the un-fed delta grows every poll — that growth IS the backlog the guard watches.
        double backlogSeconds = delta.Length / (double)AudioRecorder.TargetSampleRate;
        double fedSeconds = _consumed / (double)AudioRecorder.TargetSampleRate;
        if (_guard.ShouldDegrade(fedSeconds, _processingSeconds, backlogSeconds))
        {
            _degraded = true;
            _session = null; // FinishAsync now returns "" → RecorderController's batch fallback delivers
            Services.JotLog.Info(
                $"live streaming can't keep up (fed {fedSeconds:0.0}s, spent {_processingSeconds:0.0}s, " +
                $"backlog {backlogSeconds:0.0}s) — degrading to batch transcription");
            _cts?.Cancel();
            return;
        }

        _consumed = all.Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string partial = _session!.Accept(delta);
        sw.Stop();
        _processingSeconds += sw.Elapsed.TotalSeconds;
        if (partial.Length > 0) PartialReady?.Invoke(partial);
    }

    /// <summary>Returns the final transcript. Call while the recorder is STILL capturing (before Stop)
    /// so the last audio is included.</summary>
    public async Task<string> FinishAsync()
    {
        if (_session is null) return string.Empty;
        _cts?.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { }
        try { FeedNew(); } catch { }
        string final;
        try { final = _session.Finish(); } catch { final = string.Empty; }
        _session = null;
        _cts?.Dispose(); _cts = null; _loop = null;
        return final;
    }

    /// <summary>Cancels without producing a transcript (dispose / error paths).</summary>
    public async Task CancelAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch { }
        _session = null; _cts.Dispose(); _cts = null; _loop = null;
    }

    private static async Task<bool> Delay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
