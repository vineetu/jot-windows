using Jot.Transcription;

namespace Jot.Recording;

/// <summary>
/// Drives live transcription while the user dictates, using the transcriber's NATIVE streaming: one
/// session stays open and each newly-captured slice of audio is fed to it exactly once (the model
/// carries its own context via its cache), so partials grow as you speak and — crucially — the final
/// transcript is essentially ready the instant you stop. Replaces the old re-decode-a-trailing-window
/// hack, which existed only because the previous model (Parakeet v2) couldn't stream.
/// </summary>
public sealed class LiveTranscription
{
    private readonly AudioRecorder _recorder;
    private readonly IStreamingTranscriber _transcriber;
    private readonly int _pollMs;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IStreamingSession? _session;
    private int _consumed; // samples already fed to the session

    /// <summary>Raised on a background thread with the transcript so far.</summary>
    public event Action<string>? PartialReady;

    public LiveTranscription(AudioRecorder recorder, IStreamingTranscriber transcriber, int pollMs = 300)
    {
        _recorder = recorder;
        _transcriber = transcriber;
        _pollMs = pollMs;
    }

    public void Start()
    {
        _session = _transcriber.OpenStream();
        _consumed = 0;
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

    // Feed whatever audio has arrived since the last feed, and surface the growing transcript.
    private void FeedNew()
    {
        float[]? all = _recorder.SnapshotSamples();
        if (all is null || all.Length <= _consumed) return;
        float[] delta = all[_consumed..];
        _consumed = all.Length;
        string partial = _session!.Accept(delta);
        if (partial.Length > 0) PartialReady?.Invoke(partial);
    }

    /// <summary>Stops the live loop, feeds the final slice, and returns the authoritative transcript.
    /// Call while the recorder is STILL capturing (before Stop) so the last audio is included.</summary>
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
