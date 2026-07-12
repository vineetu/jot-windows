namespace Jot.Transcription;

/// <summary>
/// Converts 16 kHz mono Float32 audio to text. The Mac app fulfils this with
/// FluidAudio/Parakeet on the Neural Engine; on Windows the production
/// implementation will be Parakeet TDT via ONNX Runtime + DirectML.
/// </summary>
public interface ITranscriber
{
    Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken ct = default);
}

/// <summary>
/// Milestone-1 placeholder: proves the record → transcribe → paste loop end to end
/// without a real model. Emits a marker describing the captured audio.
/// </summary>
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
