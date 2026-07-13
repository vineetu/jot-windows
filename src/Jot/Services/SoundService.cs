using System.IO;
using System.Media;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Synthesizes short, distinct feedback tones in-memory (no bundled audio assets) and plays them
/// asynchronously via <see cref="SoundPlayer"/>. Each event has its own little motif so they're
/// distinguishable by ear. Gated by the per-event toggles in <see cref="JotSettings"/>.
/// </summary>
public sealed class SoundService : ISoundService
{
    private const int SampleRate = 44_100;

    private readonly ISettingsStore _settings;

    public SoundService(ISettingsStore settings) => _settings = settings;

    private JotSettings S => _settings.Current;

    // A note is (frequency Hz, duration ms). A rest is frequency 0.
    private static readonly (double f, int ms)[] StartMotif   = [(587, 70), (880, 110)];      // rising D5→A5
    private static readonly (double f, int ms)[] StopMotif    = [(880, 70), (587, 110)];      // falling A5→D5
    private static readonly (double f, int ms)[] CancelMotif  = [(300, 90), (0, 30), (300, 90)]; // low double
    private static readonly (double f, int ms)[] SuccessMotif = [(784, 70), (1046, 130)];     // G5→C6, pleasant
    private static readonly (double f, int ms)[] ErrorMotif   = [(233, 200)];                 // low buzz Bb3

    public void PlayStart()   { if (S.SoundStart)   Play(StartMotif); }
    public void PlayStop()    { if (S.SoundStop)    Play(StopMotif); }
    public void PlayCancel()  { if (S.SoundCancel)  Play(CancelMotif); }
    public void PlaySuccess() { if (S.SoundSuccess) Play(SuccessMotif); }
    public void PlayError()   { if (S.SoundError)   Play(ErrorMotif); }
    public void Preview()     => Play(SuccessMotif);

    private static void Play((double f, int ms)[] motif)
    {
        try
        {
            byte[] wav = BuildWav(motif);
            // SoundPlayer.Play() copies what it needs and plays on a background thread, so a local
            // stream is fine to let go of. Best-effort: audio glitches must never disrupt dictation.
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch { /* sound is non-essential */ }
    }

    private static byte[] BuildWav((double f, int ms)[] motif)
    {
        int totalSamples = 0;
        foreach (var (_, ms) in motif) totalSamples += ms * SampleRate / 1000;

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);

        int dataBytes = totalSamples * 2; // 16-bit mono
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);                        // PCM chunk size
        w.Write((short)1);                  // PCM
        w.Write((short)1);                  // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);            // byte rate
        w.Write((short)2);                  // block align
        w.Write((short)16);                 // bits per sample
        w.Write("data".ToCharArray());
        w.Write(dataBytes);

        foreach (var (freq, msDur) in motif)
        {
            int n = msDur * SampleRate / 1000;
            for (int i = 0; i < n; i++)
            {
                double sample = 0;
                if (freq > 0)
                {
                    // Short attack/decay envelope so notes don't click.
                    double env = Math.Min(1.0, Math.Min(i, n - i) / (SampleRate * 0.008));
                    sample = Math.Sin(2 * Math.PI * freq * i / SampleRate) * env * 0.28;
                }
                w.Write((short)(sample * short.MaxValue));
            }
        }

        w.Flush();
        return ms2.ToArray();
    }
}
