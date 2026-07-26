using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Moves Jot's on-device data — the ~0.75 GB model, recordings, and the transcript library — from one
/// data folder to another when the user changes the Save location in Settings, instead of stranding it
/// (which would otherwise force a full re-download the next launch). Observable so the Settings row can
/// bind a progress bar + status line, mirroring <see cref="ModelDownload"/>'s one-component pattern.
///
/// Crash-safe and resumable, on the same discipline as the model downloader: every file is copied to a
/// <c>.part</c> temp then swapped into place, and files already present intact at the destination are
/// skipped — so an interrupted move resumes rather than restarting. A marker file records the in-flight
/// <c>from → to</c> so <see cref="ResumePending"/> can finish it on the next launch. Crucially the source
/// is deleted only AFTER the copy is verified and the setting is repointed, so at every instant the app
/// still resolves a COMPLETE dataset (before the flip: the old folder; after it: the new one). A crash
/// therefore never loses data — the worst case is leftover bytes in the old folder, cleaned up on resume.
/// </summary>
public sealed partial class DataFolderMigrator : ObservableObject
{
    // The marker lives beside settings.json in the fixed config folder (JotPaths.ConfigDir), NEVER under a
    // data folder being moved — otherwise the record describing the move could itself get moved or orphaned
    // mid-operation. ConfigDir is that fixed root (the MSIX container, or %LOCALAPPDATA%\Jot unpackaged).
    // Tests pass a temp configDir so they never touch the real user's config folder.
    private readonly string _configDir;
    private string MarkerPath => Path.Combine(_configDir, "migration.json");

    // The top-level items (relative to a data folder) a move carries. Everything else in the folder
    // (settings.json, logs, the marker) is either fixed-location or transient and stays put.
    // "Vocabulary" is the learned corrections ledger: leaving it behind silently resets the owner's
    // training when they move the data folder. Internal so VocabStoreProvenanceTests asserts the real
    // list rather than a copy of it.
    internal static readonly string[] MigratedItems =
        ["models", "recordings", "library.json", JotPaths.VocabularyFolderName];

    private readonly ISettingsStore _store;

    /// <param name="configDir">Override for the fixed folder holding the migration marker. Null uses
    /// <c>%LOCALAPPDATA%\Jot</c> (production). Tests pass a temp dir so they never touch real config.</param>
    public DataFolderMigrator(ISettingsStore store, string? configDir = null)
    {
        _store = store;
        _configDir = configDir ?? JotPaths.ConfigDir;
    }

    [ObservableProperty] private bool _isMigrating;
    [ObservableProperty] private double _progress;   // 0..100, for a bound ProgressBar
    [ObservableProperty] private string _statusText = "";

    /// <summary>Inverse of <see cref="IsMigrating"/> — bound to button IsEnabled (no converter needed).</summary>
    public bool NotMigrating => !IsMigrating;
    partial void OnIsMigratingChanged(bool value) => OnPropertyChanged(nameof(NotMigrating));

    /// <summary>True when a previous move was interrupted (crash/kill) and needs finishing on launch.</summary>
    public bool HasPendingMigration => File.Exists(MarkerPath);

    private sealed record Marker(string From, string To);

    /// <summary>
    /// Moves all data from the current data folder to <paramref name="newDir"/> and repoints the setting.
    /// Reports progress into the observable state; safe to await from the UI. Returns true on success (or
    /// when the target already equals the current folder). On failure the setting is left unchanged, so
    /// the app keeps reading the old, still-complete folder.
    /// </summary>
    public async Task<bool> MoveToAsync(string newDir)
    {
        string from = JotPaths.DataDir(_store.Current);
        string to = newDir;

        if (PathsEqual(from, to)) return true;
        if (IsSubPath(from, to) || IsSubPath(to, from))
        {
            StatusText = "Choose a folder that isn't inside the current one.";
            return false;
        }
        if (IsMigrating) return false;

        IsMigrating = true;
        Progress = 0;
        StatusText = "Preparing to move…";
        // Progress<T> created on the UI thread marshals its callbacks back to it, so the bound bar/text
        // update safely while the copy runs on a background thread.
        var reporter = new Progress<(long Done, long Total)>(p =>
        {
            Progress = p.Total == 0 ? 100 : Math.Min(100, p.Done * 100.0 / p.Total);
            StatusText = Describe(p.Done, p.Total);
        });
        try
        {
            // Heavy copy off the UI thread; the flip + cleanup below resume on it (Save raises Changed,
            // whose handlers — hotkey re-registration — are UI-affine).
            await Task.Run(() => CopyPhase(from, to, reporter)).ConfigureAwait(true);
            FlipSetting(to);
            await Task.Run(() => Finish(from)).ConfigureAwait(true);
            Progress = 100;
            StatusText = "Move complete.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "Move failed — " + ex.Message;
            JotLog.Info($"data-folder move failed: {ex}");
            return false;
        }
        finally { IsMigrating = false; }
    }

    /// <summary>
    /// Finishes a move interrupted by a crash/kill, driven entirely by the marker's from→to (idempotent:
    /// already-copied files are skipped, an already-flipped setting is re-set, and a partly-deleted source
    /// is simply finished). Called once at startup, off the UI thread. No-op when no marker exists.
    /// </summary>
    public void ResumePending()
    {
        Marker? m = ReadMarker();
        if (m is null) return;
        JotLog.Info($"resuming interrupted data-folder move: {m.From} -> {m.To}");
        // No observable-state updates here: this runs on a background thread at startup with nothing bound
        // yet, and touching bound properties cross-thread would throw. The work itself is silent + idempotent.
        try
        {
            CopyPhase(m.From, m.To, progress: null);
            FlipSetting(m.To);
            Finish(m.From);
            JotLog.Info("data-folder move resume: complete");
        }
        catch (Exception ex)
        {
            // Leave the marker so the next launch tries again; the app is still usable meanwhile because
            // the setting points at a complete folder (old before the flip, new after it).
            JotLog.Info($"data-folder move resume failed (will retry next launch): {ex}");
        }
    }

    // Copy every source file into the destination, writing a marker first so an interruption is
    // recoverable. Each file goes to a .part then is swapped in; a file already present at full size is
    // skipped, making the whole phase safe to re-run.
    private void CopyPhase(string from, string to, IProgress<(long, long)>? progress)
    {
        List<(string Src, string Rel, long Size)> files = EnumerateFiles(from);
        long total = files.Sum(f => f.Size);
        long alreadyThere = files
            .Where(f => File.Exists(Path.Combine(to, f.Rel)) && new FileInfo(Path.Combine(to, f.Rel)).Length == f.Size)
            .Sum(f => f.Size);
        EnsureEnoughFreeSpace(to, total - alreadyThere);

        WriteMarker(new Marker(from, to));

        long done = 0;
        progress?.Report((done, total));
        foreach ((string src, string rel, long size) in files)
        {
            string dst = Path.Combine(to, rel);
            if (File.Exists(dst) && new FileInfo(dst).Length == size)
            {
                done += size;
                progress?.Report((done, total));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            string part = dst + ".part";
            long fileBase = done;
            CopyWithProgress(src, part, copied => progress?.Report((fileBase + copied, total)));
            if (new FileInfo(part).Length != size)
                throw new IOException($"copy size mismatch for {rel}");
            File.Move(part, dst, overwrite: true);
            done += size;
            progress?.Report((done, total));
        }
    }

    // Repoint the data folder. Marshalled to the UI thread when one exists, because Save raises
    // ISettingsStore.Changed and its handlers (hotkey re-registration) expect the UI thread. Runs inline
    // under tests / startup-before-UI, where Application.Current is null.
    private void FlipSetting(string to)
    {
        void Apply() { _store.Current.DataDirectory = to; _store.Save(); }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.Invoke(Apply);
        else Apply();
    }

    // Delete the now-copied source items, then the marker. Best-effort: a file still mapped by the running
    // engine can't be deleted yet (sharing violation) — we log and move on rather than fail the move; the
    // leftover is harmless (wasted disk) and the setting already points at the complete new folder.
    private void Finish(string from)
    {
        foreach (string item in MigratedItems)
        {
            string p = Path.Combine(from, item);
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception ex) { JotLog.Info($"migration: could not remove old {item}: {ex.Message}"); }
        }
        ClearMarker();
    }

    // Chunked copy so the big external-weights file (~690 MB) reports progress smoothly instead of jumping
    // in one lump; onCopied receives the running byte count for THIS file.
    private static void CopyWithProgress(string src, string dst, Action<long> onCopied)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var buffer = new byte[1 << 20];
        long copied = 0;
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
            copied += n;
            onCopied(copied);
        }
    }

    private static List<(string Src, string Rel, long Size)> EnumerateFiles(string from)
    {
        var list = new List<(string, string, long)>();
        foreach (string item in MigratedItems)
        {
            string p = Path.Combine(from, item);
            if (Directory.Exists(p))
            {
                foreach (string f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".part", StringComparison.Ordinal)) continue; // stale download temp — don't carry it
                    list.Add((f, Path.GetRelativePath(from, f), new FileInfo(f).Length));
                }
            }
            else if (File.Exists(p))
            {
                list.Add((p, item, new FileInfo(p).Length));
            }
        }
        return list;
    }

    private void WriteMarker(Marker m)
    {
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(MarkerPath, JsonSerializer.Serialize(m));
    }

    private Marker? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? JsonSerializer.Deserialize<Marker>(File.ReadAllText(MarkerPath)) : null;
        }
        catch { return null; } // unreadable marker — treat as nothing pending rather than block launch
    }

    private void ClearMarker()
    {
        try { if (File.Exists(MarkerPath)) File.Delete(MarkerPath); } catch { /* best-effort */ }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(Normalize(a), Normalize(b),
        StringComparison.OrdinalIgnoreCase);

    // True when child sits inside parent (so we reject moving a folder into its own subtree).
    private static bool IsSubPath(string parent, string child) =>
        Normalize(child).StartsWith(Normalize(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Mirror of NemotronModelInstaller's guard: the destination must hold a full copy while the source
    // still exists, so check the real target drive up front and say how much is short.
    private static void EnsureEnoughFreeSpace(string dir, long needBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (root is null) return;
            var drive = new DriveInfo(root);
            long required = needBytes + (1L << 30); // + ~1 GB headroom (a .part briefly doubles a file)
            if (drive.IsReady && drive.AvailableFreeSpace < required)
            {
                const double gb = 1L << 30;
                throw new IOException(
                    $"Not enough free space on {root.TrimEnd('\\')} — need ~{required / gb:0.#} GB, " +
                    $"{drive.AvailableFreeSpace / gb:0.#} GB free.");
            }
        }
        catch (IOException) { throw; }
        catch { /* couldn't probe the drive — don't block the move over a probe failure */ }
    }

    private static string Describe(long done, long total)
    {
        if (total == 0) return "Finishing…";
        const double mb = 1024.0 * 1024.0;
        return $"Moving your Jot data… {done / mb:0} MB of {total / mb:0} MB ({done * 100.0 / total:0}%)";
    }
}
