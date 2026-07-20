namespace Jot.Transcription;

/// <summary>Converts 16 kHz mono Float32 audio to text.</summary>
public interface ITranscriber
{
    Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default);

    /// <summary>Whether the model assets are present on disk.</summary>
    bool IsModelInstalled => true;

    /// <summary>Primes the model off the UI thread so the first dictation isn't a cold start.</summary>
    void WarmUp() { }
}

/// <summary>
/// A transcriber whose model streams natively: open a live session, feed audio chunks as they arrive,
/// read the transcript as it grows. On stop the transcript is essentially already done — no re-decoding
/// of the whole clip.
/// </summary>
public interface IStreamingTranscriber
{
    IStreamingSession OpenStream();
}

/// <summary>One in-flight streaming utterance.</summary>
public interface IStreamingSession
{
    /// <summary>Feeds newly-arrived 16 kHz mono samples; returns the transcript so far.</summary>
    string Accept(float[] newSamples);

    /// <summary>Processes the tail and returns the final transcript.</summary>
    string Finish();
}

/// <summary>Milestone-1 placeholder: exercises the record→transcribe→paste loop without a real model.</summary>
public sealed class StubTranscriber : ITranscriber
{
    public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default)
    {
        double seconds = samples.Length / (double)sampleRate;
        double rms = samples.Length == 0 ? 0 : Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);
        string text = $"[Jot stub: captured {seconds:0.0}s of audio, RMS level {rms:0.000}] ";
        return Task.FromResult(text);
    }
}
