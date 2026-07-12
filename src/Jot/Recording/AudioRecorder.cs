using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jot.Recording;

/// <summary>Result of one recording: 16 kHz mono float PCM plus the on-disk WAV path.</summary>
public sealed record RecordingResult(float[] Samples, int SampleRate, string WavPath, TimeSpan Duration);

/// <summary>
/// Captures the default microphone via WASAPI and produces 16 kHz mono Float32 —
/// the input format Parakeet expects (matches the Mac app's AVAudioConverter target).
/// Capture happens at the device's native format; downmix + resample run on Stop.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private const int TargetSampleRate = 16000;

    private WasapiCapture? _capture;
    private MemoryStream? _buffer;
    private WaveFormat? _sourceFormat;

    public bool IsRecording => _capture is not null;

    public void Start()
    {
        if (IsRecording) return;

        _capture = new WasapiCapture(); // default capture device, shared mode
        _sourceFormat = _capture.WaveFormat;
        _buffer = new MemoryStream();

        _capture.DataAvailable += (_, e) => _buffer!.Write(e.Buffer, 0, e.BytesRecorded);
        _capture.StartRecording();
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

        // device format -> float samples -> mono -> 16 kHz
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
