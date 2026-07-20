using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jot.Recording;

/// <summary>Result of one recording: 16 kHz mono float PCM plus the on-disk WAV path.</summary>
public sealed record RecordingResult(float[] Samples, int SampleRate, string WavPath, TimeSpan Duration);

/// <summary>
/// Captures the default mic via WASAPI and produces 16 kHz mono Float32 (the format Parakeet expects).
/// Capture runs at the device's native format; downmix + resample happen on Stop.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int TargetSampleRate = 16000;

    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private WaveFormat? _sourceFormat;
    private int _lastLevelTick;
    private readonly object _bufferLock = new(); // guards _buffer between the capture thread and snapshots

    public bool IsRecording => _capture is not null;

    /// <summary>
    /// Fired on the capture thread ~30×/s while recording with a display-scaled RMS level (0 = silence,
    /// ~1 = loud); the pill marshals each onto the Dispatcher. Silent when not recording.
    /// </summary>
    public event Action<float>? LevelChanged;

    /// <summary>Starts capture on <paramref name="deviceId"/> if it's a valid WASAPI endpoint,
    /// else the system default device.</summary>
    public void Start(string? deviceId = null)
    {
        if (IsRecording) return;

        _capture = CreateCapture(deviceId);
        _sourceFormat = _capture.WaveFormat;
        _buffer = new MemoryStream();

        _capture.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded);
        _capture.StartRecording();
    }

    private static WasapiCapture CreateCapture(string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var mm = new MMDeviceEnumerator();
                MMDevice device = mm.GetDevice(deviceId);
                if (device is not null && device.State == DeviceState.Active)
                    return new WasapiCapture(device);
            }
            catch { /* device gone / access denied — fall back to default below */ }
        }
        return new WasapiCapture(); // system default capture device, shared mode
    }

    private void OnData(byte[] buffer, int bytes)
    {
        lock (_bufferLock) { _buffer!.Write(buffer, 0, bytes); }
        RaiseLevel(buffer, bytes);
    }

    /// <summary>
    /// Everything captured so far as 16 kHz mono Float32, without stopping capture — fed to live
    /// captioning while the user is still speaking. Null when not recording; empty when nothing captured.
    /// </summary>
    public float[]? SnapshotSamples()
    {
        byte[] raw;
        WaveFormat fmt;
        lock (_bufferLock)
        {
            if (_buffer is null || _sourceFormat is null) return null;
            raw = _buffer.ToArray();
            fmt = _sourceFormat;
        }

        if (raw.Length == 0) return [];

        var stream = new RawSourceWaveStream(new MemoryStream(raw, writable: false), fmt);
        ISampleProvider mono = ToMono(stream.ToSampleProvider());
        ISampleProvider resampled = mono.WaveFormat.SampleRate == TargetSampleRate
            ? mono
            : new WdlResamplingSampleProvider(mono, TargetSampleRate);
        return ReadAll(resampled);
    }

    /// <summary>Throttled (~30 Hz) RMS over the just-captured buffer, in the device's native format.</summary>
    private void RaiseLevel(byte[] buffer, int bytes)
    {
        if (LevelChanged is null || _sourceFormat is null) return;

        int now = Environment.TickCount;
        if (now - _lastLevelTick < 33) return;
        _lastLevelTick = now;

        double sumSq = 0;
        int count = 0;

        if (_sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && _sourceFormat.BitsPerSample == 32)
        {
            for (int i = 0; i + 4 <= bytes; i += 4)
            {
                float s = BitConverter.ToSingle(buffer, i);
                sumSq += s * s;
                count++;
            }
        }
        else if (_sourceFormat.Encoding == WaveFormatEncoding.Pcm && _sourceFormat.BitsPerSample == 16)
        {
            for (int i = 0; i + 2 <= bytes; i += 2)
            {
                float s = BitConverter.ToInt16(buffer, i) / 32768f;
                sumSq += s * s;
                count++;
            }
        }
        else
        {
            return; // unknown format — skip the level rather than guess
        }

        if (count == 0) return;
        double rms = Math.Sqrt(sumSq / count);
        LevelChanged?.Invoke((float)Math.Min(1.0, rms * 12.0)); // display gain so speech fills the pill
    }

    /// <summary>Stops capture and returns the resampled mono audio. Never null once started.</summary>
    public RecordingResult Stop(string wavPath)
    {
        if (_capture is null || _buffer is null || _sourceFormat is null)
            throw new InvalidOperationException("Stop called without an active recording.");

        var capture = _capture;
        var stopped = new TaskCompletionSource();
        capture.RecordingStopped += (_, _) => stopped.TrySetResult();
        capture.StopRecording();
        stopped.Task.Wait(TimeSpan.FromSeconds(2)); // let the final buffers flush

        _buffer.Position = 0;
        var raw = new RawSourceWaveStream(_buffer, _sourceFormat);

        ISampleProvider mono = ToMono(raw.ToSampleProvider());
        ISampleProvider resampled = mono.WaveFormat.SampleRate == TargetSampleRate
            ? mono
            : new WdlResamplingSampleProvider(mono, TargetSampleRate);

        var samples = ReadAll(resampled);
        WriteWav16(wavPath, samples);

        var duration = TimeSpan.FromSeconds(samples.Length / (double)TargetSampleRate);

        capture.Dispose();
        _buffer.Dispose();
        _capture = null;
        _buffer = null;
        _sourceFormat = null;

        return new RecordingResult(samples, TargetSampleRate, wavPath, duration);
    }

    /// <summary>Stops capture and throws the audio away (cancelled recording). Safe if not recording.</summary>
    public void Discard()
    {
        if (_capture is null) return;
        var capture = _capture;
        var stopped = new TaskCompletionSource();
        capture.RecordingStopped += (_, _) => stopped.TrySetResult();
        capture.StopRecording();
        stopped.Task.Wait(TimeSpan.FromSeconds(2));

        capture.Dispose();
        _buffer?.Dispose();
        _capture = null;
        _buffer = null;
        _sourceFormat = null;
    }

    private static ISampleProvider ToMono(ISampleProvider src)
    {
        if (src.WaveFormat.Channels == 1) return src;
        if (src.WaveFormat.Channels == 2) return new StereoToMonoSampleProvider(src) { LeftVolume = 0.5f, RightVolume = 0.5f };
        return new MultiChannelToMonoSampleProvider(src);
    }

    private static float[] ReadAll(ISampleProvider provider)
    {
        var all = new List<float>(TargetSampleRate * 8);
        var buf = new float[TargetSampleRate]; // ~1s chunks
        int read;
        while ((read = provider.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(buf.AsSpan(0, read).ToArray());
        return all.ToArray();
    }

    private static void WriteWav16(string path, float[] samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var fmt = new WaveFormat(TargetSampleRate, 16, 1);
        using var writer = new WaveFileWriter(path, fmt);
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp((int)(samples[i] * short.MaxValue), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        writer.Write(pcm, 0, pcm.Length);
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _buffer?.Dispose();
    }
}

/// <summary>Averages all channels down to one. NAudio only ships a stereo-specific downmixer.</summary>
internal sealed class MultiChannelToMonoSampleProvider(ISampleProvider source) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private float[] _src = [];

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        int need = count * _channels;
        if (_src.Length < need) _src = new float[need];
        int read = source.Read(_src, 0, need);
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += _src[f * _channels + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
