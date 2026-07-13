using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Jot.Controls;

/// <summary>
/// A lightweight live input-level meter used by the setup wizard's Microphone step to prove the mic
/// is hot before continuing. Independent of the dictation <c>AudioRecorder</c> — it opens its own
/// short-lived WASAPI capture and raises a 0..1 level ~30×/s. Start it on entering the step, Stop on
/// leaving/closing so the mic isn't held open.
/// </summary>
public sealed class MicMeter : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFormat? _format;
    private int _lastTick;

    public event Action<float>? Level;

    public void Start()
    {
        Stop();
        try
        {
            _capture = new WasapiCapture();
            _format = _capture.WaveFormat;
            _capture.DataAvailable += (_, e) => Emit(e.Buffer, e.BytesRecorded);
            _capture.StartRecording();
        }
        catch
        {
            Stop(); // no device / access denied — meter stays flat
        }
    }

    private void Emit(byte[] buffer, int bytes)
    {
        if (_format is null) return;
        int now = Environment.TickCount;
        if (now - _lastTick < 33) return;
        _lastTick = now;

        double sumSq = 0;
        int count = 0;
        if (_format.Encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytes; i += 4) { float s = BitConverter.ToSingle(buffer, i); sumSq += s * s; count++; }
        }
        else if (_format.Encoding == WaveFormatEncoding.Pcm && _format.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytes; i += 2) { float s = BitConverter.ToInt16(buffer, i) / 32768f; sumSq += s * s; count++; }
        }
        else return;

        if (count == 0) return;
        Level?.Invoke((float)Math.Min(1.0, Math.Sqrt(sumSq / count) * 12.0));
    }

    public void Stop()
    {
        if (_capture is null) return;
        try { _capture.StopRecording(); _capture.Dispose(); } catch { }
        _capture = null;
        _format = null;
    }

    public void Dispose() => Stop();
}
