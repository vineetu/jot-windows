using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Jot.Transcription;

/// <summary>
/// Reads an audio file into the mono 16 kHz Float32 form the transcriber expects. Jot's own
/// recordings are already 16 kHz/mono, so this is normally a straight read; it also downmixes and
/// resamples defensively for the re-transcribe path where a WAV's format isn't guaranteed.
/// </summary>
public static class WavAudio
{
    public const int SampleRate = 16_000;

    public static float[] ReadMono16k(string path)
    {
        using var reader = new AudioFileReader(path); // exposes Float32 samples at the file's rate
        ISampleProvider provider = reader;

        if (provider.WaveFormat.Channels == 2)
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (provider.WaveFormat.Channels > 2)
            provider = new MultiChannelToMonoSampleProvider(provider);

        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);

        var samples = new List<float>((int)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate);
        var buffer = new float[SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read).ToArray());

        return samples.ToArray();
    }

    /// <summary>Averages all channels down to mono for sources with more than two channels.</summary>
    private sealed class MultiChannelToMonoSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private readonly int _channels = source.WaveFormat.Channels;
        private float[] _scratch = [];

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            int needed = count * _channels;
            if (_scratch.Length < needed) _scratch = new float[needed];

            int read = source.Read(_scratch, 0, needed);
            int frames = read / _channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0f;
                for (int ch = 0; ch < _channels; ch++)
                    sum += _scratch[frame * _channels + ch];
                buffer[offset + frame] = sum / _channels;
            }
            return frames;
        }
    }
}
