using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace Jot.Services;

/// <summary>
/// The ONE place that knows every artifact Jot writes and removes them all — the single source of truth
/// shared by in-app "Erase all data" and the (Velopack) uninstaller, so the two lists can never drift
/// (they did before: erase forgot stats/logs/marker/registry). Deletes only Jot's NAMED children, never a
/// folder wholesale, so it's safe even when the data folder is a shared drive location; empty folders go
/// last. Retries briefly to ride out a file still held by a just-exiting process (the erase→restart path).
///
/// Reliable-erase design: the running app can't delete its own memory-mapped model (delete throws a sharing
/// violation, which the old code silently swallowed → data survived). So "Erase" drops a <see cref="WipeMarker"/>
/// and restarts; the next launch calls <see cref="ConsumePendingWipe"/> BEFORE any service opens a file.
/// </summary>
public static class JotDataPurge
{
    public const string WipeMarkerFile = "wipe.json";
    private const string RunValue = "Jot";

    // The subfolders/files that make up a DATA folder (relative to the data dir — which may be a custom drive).
    // "Vocabulary" holds the correction ledger + provenance — learned text the owner dictated, so it
    // must go with an erase or the privacy claim is false.
    private static readonly string[] DataSubdirs =
        ["models", "recordings", "logs", JotPaths.VocabularyFolderName];
    private static readonly string[] DataFiles = ["library.json", "aikey.dat", "stats.json"];
    // Artifacts that always live in the fixed config root (JotPaths.ConfigDir): ffmpeg tools, the pre-init
    // log fallback, and the config/marker files.
    private static readonly string[] ConfigSubdirs = ["tools", "logs"];
    private static readonly string[] ConfigFiles = ["settings.json", "prompts.json", "migration.json", WipeMarkerFile, OrtModelCleanup.MarkerFile];

    public sealed record WipeMarker(string DataDir);

    /// <summary>Every absolute path a full wipe touches (config root + the given data dir). Single source of
    /// truth — the erase test asserts against this, so a newly-added artifact can't be silently forgotten.</summary>
    public static IEnumerable<string> ArtifactPaths(string dataDir, string configDir)
    {
        foreach (string d in DataSubdirs) yield return Path.Combine(dataDir, d);
        foreach (string f in DataFiles) yield return Path.Combine(dataDir, f);
        foreach (string d in ConfigSubdirs) yield return Path.Combine(configDir, d);
        foreach (string f in ConfigFiles) yield return Path.Combine(configDir, f);
    }

    /// <summary>Remove every Jot artifact under <paramref name="dataDir"/> and <paramref name="configDir"/>,
    /// plus the launch-at-login entry, then the now-empty folders. Best-effort and non-throwing (runs from
    /// an uninstall hook and from startup); retries transient locks.</summary>
    public static void PurgeAll(string dataDir, string configDir, bool removeLaunchEntry = true)
    {
        if (removeLaunchEntry) RemoveRunEntry();
        foreach (string d in DataSubdirs) DeleteDir(Path.Combine(dataDir, d));
        foreach (string f in DataFiles) DeleteFile(Path.Combine(dataDir, f));
        foreach (string d in ConfigSubdirs) DeleteDir(Path.Combine(configDir, d));
        foreach (string f in ConfigFiles) DeleteFile(Path.Combine(configDir, f));
        RemoveIfEmpty(dataDir);
        if (!PathsEqual(dataDir, configDir)) RemoveIfEmpty(configDir);
    }

    /// <summary>Record that a full wipe should run on the next launch, capturing the resolved data dir (the
    /// custom-drive path would otherwise be lost once settings.json is deleted). Then the caller restarts.</summary>
    public static void RequestWipe(string dataDir, string configDir)
    {
        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(Path.Combine(configDir, WipeMarkerFile),
                JsonSerializer.Serialize(new WipeMarker(dataDir)));
        }
        catch { /* if we can't even write the marker, erase just won't happen — never crash the UI */ }
    }

    /// <summary>If a wipe was requested, run it now — call at the very top of startup, before any service
    /// opens a file. Returns true if a wipe ran, so the caller treats this launch as a fresh first run.</summary>
    public static bool ConsumePendingWipe(string configDir, bool removeLaunchEntry = true)
    {
        string marker = Path.Combine(configDir, WipeMarkerFile);
        if (!File.Exists(marker)) return false;

        WipeMarker? m = null;
        try { m = JsonSerializer.Deserialize<WipeMarker>(File.ReadAllText(marker)); }
        catch { /* unreadable marker: still purge (config at least) so we don't loop on it forever */ }
        PurgeAll(m?.DataDir ?? configDir, configDir, removeLaunchEntry);
        return true;
    }

    private static void RemoveRunEntry()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }

    // A file/dir may be momentarily held by a just-exited previous instance (erase→restart) — retry a few
    // times over ~1.5s before giving up. All swallow: a wipe must never throw out of uninstall/startup.
    private static void DeleteDir(string dir)
    {
        for (int i = 0; i < 6; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(250); }
        }
    }

    private static void DeleteFile(string file)
    {
        for (int i = 0; i < 6; i++)
        {
            try { if (File.Exists(file)) File.Delete(file); return; }
            catch { Thread.Sleep(250); }
        }
    }

    private static void RemoveIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch { /* leave a non-empty / shared folder in place */ }
    }

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);
}
