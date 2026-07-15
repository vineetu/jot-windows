using System.IO;
using System.Text.Json;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// On-device usage counters (worklist D2): total dictations, words, and seconds spoken, plus an
/// estimate of time saved vs typing. Privacy-respecting — persisted only to <c>&lt;DataDir&gt;\stats.json</c>,
/// never sent anywhere. Surfaced on the About page to motivate the "donate to charity" ask (D1).
/// Best-effort: a read/write failure never affects a dictation.
/// </summary>
public sealed class UsageStats
{
    private readonly ISettingsStore _settings;
    private readonly object _gate = new();
    private const double TypingWpm = 40.0; // conservative average typing speed for the "time saved" estimate

    public int TotalDictations { get; private set; }
    public long TotalWords { get; private set; }
    public double TotalDictationSeconds { get; private set; }

    public UsageStats(ISettingsStore settings)
    {
        _settings = settings;
        Load();
    }

    private string FilePath => Path.Combine(JotPaths.DataDir(_settings.Current), "stats.json");

    /// <summary>Record one completed dictation. No-op for empty transcripts.</summary>
    public void RecordDictation(string transcript, double seconds)
    {
        int words = CountWords(transcript);
        if (words == 0) return;
        lock (_gate)
        {
            TotalDictations++;
            TotalWords += words;
            TotalDictationSeconds += Math.Max(0, seconds);
            Save();
        }
    }

    /// <summary>Estimated minutes saved vs typing at ~40 wpm (typing time minus time spent dictating).</summary>
    public double MinutesSaved
    {
        get
        {
            double typeMinutes = TotalWords / TypingWpm;
            double dictateMinutes = TotalDictationSeconds / 60.0;
            return Math.Max(0, typeMinutes - dictateMinutes);
        }
    }

    private static int CountWords(string s) =>
        string.IsNullOrWhiteSpace(s) ? 0 : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            StatsDto? d = JsonSerializer.Deserialize<StatsDto>(File.ReadAllText(FilePath));
            if (d is not null)
            {
                TotalDictations = d.TotalDictations;
                TotalWords = d.TotalWords;
                TotalDictationSeconds = d.TotalDictationSeconds;
            }
        }
        catch { /* corrupt/unreadable — start from zero */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(new StatsDto(TotalDictations, TotalWords, TotalDictationSeconds)));
        }
        catch { /* best effort */ }
    }

    private sealed record StatsDto(int TotalDictations, long TotalWords, double TotalDictationSeconds);
}
