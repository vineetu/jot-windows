using System.IO;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Enforces the "Keep recordings" retention setting by pruning old AUDIO only — transcripts are kept
/// forever. On launch it deletes WAV files older than the configured window and clears the matching
/// rows' <c>WavPath</c> (so the transcript stays, playback just goes away). A window of 0 means "keep
/// audio forever" and prunes nothing. Best-effort — a locked/again-missing file is skipped.
/// </summary>
public sealed class RetentionCleaner
{
    private static readonly string RecordingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot", "recordings");

    private readonly ISettingsStore _settings;
    private readonly IRecordingStore _store;

    public RetentionCleaner(ISettingsStore settings, IRecordingStore store)
    {
        _settings = settings;
        _store = store;
    }

    /// <summary>Prunes audio older than the retention window (keeps transcripts). Safe to call at startup.</summary>
    public void Prune()
    {
        int days = _settings.Current.RetentionDays;
        if (days <= 0) return; // keep audio forever

        DateTime cutoff = DateTime.Now.AddDays(-days);

        // 1. Clear WavPath on old library rows and delete their audio — the transcript row stays.
        foreach (Models.RecordingItem item in _store.Items.Where(i => i.CreatedAt < cutoff).ToList())
        {
            if (string.IsNullOrEmpty(item.WavPath)) continue;
            try { if (File.Exists(item.WavPath)) File.Delete(item.WavPath); }
            catch { /* locked or already gone */ }
            item.WavPath = null; // transcript kept; playback disabled
        }

        // 2. Sweep up orphan WAVs on disk older than the window (files no row references).
        try
        {
            if (Directory.Exists(RecordingsDir))
            {
                var referenced = _store.Items
                    .Where(i => !string.IsNullOrEmpty(i.WavPath))
                    .Select(i => Path.GetFullPath(i.WavPath!))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (string file in Directory.EnumerateFiles(RecordingsDir, "*.wav"))
                {
                    if (referenced.Contains(Path.GetFullPath(file))) continue;
                    try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
                    catch { /* locked or already gone */ }
                }
            }
        }
        catch { /* enumeration failed — nothing to sweep */ }
    }
}
