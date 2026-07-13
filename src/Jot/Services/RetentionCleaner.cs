using System.IO;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Enforces the "Keep recordings" retention setting: on launch, deletes WAV files older than the
/// configured window from the recordings folder, and drops any matching library rows. A window of
/// 0 means "keep forever" and prunes nothing. Best-effort — a locked/again-missing file is skipped.
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

    /// <summary>Prunes anything older than the retention window. Cheap and safe to call at startup.</summary>
    public void Prune()
    {
        int days = _settings.Current.RetentionDays;
        if (days <= 0) return; // keep forever

        DateTime cutoff = DateTime.Now.AddDays(-days);

        // 1. Old WAVs on disk (the only thing that survives a restart today).
        try
        {
            if (Directory.Exists(RecordingsDir))
            {
                foreach (string file in Directory.EnumerateFiles(RecordingsDir, "*.wav"))
                {
                    try { if (File.GetLastWriteTime(file) < cutoff) File.Delete(file); }
                    catch { /* locked or already gone */ }
                }
            }
        }
        catch { /* enumeration failed — nothing to prune */ }

        // 2. Any in-memory rows past the window (and their audio).
        var expired = _store.Items.Where(i => i.CreatedAt < cutoff).ToList();
        foreach (Models.RecordingItem item in expired)
        {
            try { if (!string.IsNullOrEmpty(item.WavPath) && File.Exists(item.WavPath)) File.Delete(item.WavPath); }
            catch { /* best effort */ }
            _store.Delete(item);
        }
    }
}
