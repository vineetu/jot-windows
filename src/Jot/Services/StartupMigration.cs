using System.IO;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// One-time upgrade glue for moving the default data root into the MSIX package container (so uninstall
/// wipes everything). Existing installs kept everything in the old <c>%LOCALAPPDATA%\Jot</c>; without this
/// they'd see the first-run wizard again and — worse — their data would sit outside the container and
/// survive uninstall. Two steps, both safe and crash-tolerant:
///
///  1. <see cref="PrepareContainer"/> (before the settings store loads): move the small config files into
///     the container, then — when the old data is on the SAME volume as the container (the common case) —
///     RELOCATE the data into the container too. Same-volume moves are renames: instant, no copy, no extra
///     space, so a full install ends up entirely inside the container and uninstall removes every trace.
///  2. <see cref="AdoptLegacyDataDir"/> (after the store loads): only for data on a DIFFERENT drive (a
///     deliberately-chosen location) — point the setting at it in place (no cross-volume copy). Such a
///     location is outside the container and can't be auto-wiped on uninstall; Settings says so.
///
/// No-op when unpackaged (no container) or once the container is established.
/// </summary>
public static class StartupMigration
{
    private static readonly string[] ConfigFiles = ["settings.json", "prompts.json", "migration.json"];
    // The data a full install holds (dirs + files), relative to a data folder. Same set JotDataPurge wipes.
    private static readonly string[] DataItems =
        ["models", "recordings", "logs", "library.json", "aikey.dat", "stats.json"];
    private const string RelocateMarker = "relocating.marker";

    /// <summary>Before the settings store loads: move config into the container, and (same-volume only)
    /// relocate the data into it so it's inside the auto-wiped container. Runs with nothing open yet.</summary>
    public static void PrepareContainer()
    {
        if (PackagePaths.ContainerRoot is not { } container) return;
        string legacy = JotPaths.LegacyLocalAppDataDir;
        if (PathsEqual(legacy, container)) return;

        MigrateConfig(legacy, container);
        FinishInterruptedRelocate(container);   // resume a relocate cut short by a crash

        // Only relocate when the user hasn't chosen a folder, the data is still in the old spot, and it's on
        // the SAME volume (so the move is a free rename — never a big cross-drive copy at startup).
        var probe = new JsonSettingsStore(); // reads the just-migrated settings from the container
        if (string.IsNullOrWhiteSpace(probe.Current.DataDirectory)
            && SameVolume(legacy, container) && HasData(legacy) && !HasData(container))
            RelocateData(legacy, container);
    }

    // Testable core: copy-then-delete each config file legacy -> container, but only when the container has
    // no settings yet (fresh container) and the legacy folder actually has settings (a real upgrade).
    public static void MigrateConfig(string legacy, string container)
    {
        if (PathsEqual(legacy, container)) return;
        if (File.Exists(Path.Combine(container, "settings.json"))) return; // already migrated, or fresh install here
        if (!File.Exists(Path.Combine(legacy, "settings.json"))) return;   // nothing to migrate
        try
        {
            Directory.CreateDirectory(container);
            foreach (string f in ConfigFiles)
            {
                string src = Path.Combine(legacy, f), dst = Path.Combine(container, f);
                if (!File.Exists(src)) continue;
                File.Copy(src, dst, overwrite: true);
                try { File.Delete(src); } catch { /* leftover is harmless — the container copy is authoritative */ }
            }
            JotLog.Info($"config migrated from {legacy} to package container");
        }
        catch (Exception ex) { JotLog.Info($"config container-migration skipped: {ex.Message}"); }
    }

    // Testable core: rename each data item legacy -> container. Same-volume, so each move is atomic and
    // instant. A marker makes a crash resumable; items already at the destination are skipped (idempotent).
    public static void RelocateData(string legacy, string container)
    {
        try
        {
            Directory.CreateDirectory(container);
            File.WriteAllText(Path.Combine(container, RelocateMarker), legacy);
            foreach (string item in DataItems)
            {
                string src = Path.Combine(legacy, item), dst = Path.Combine(container, item);
                bool srcIsDir = Directory.Exists(src);
                if (!srcIsDir && !File.Exists(src)) continue;
                if (Directory.Exists(dst) || File.Exists(dst)) continue; // already relocated (resume)
                try { if (srcIsDir) Directory.Move(src, dst); else File.Move(src, dst); }
                catch (Exception ex) { JotLog.Info($"relocate {item} failed: {ex.Message}"); }
            }
            try { File.Delete(Path.Combine(container, RelocateMarker)); } catch { }
            JotLog.Info($"data relocated from {legacy} into the package container");
        }
        catch (Exception ex) { JotLog.Info($"data relocation skipped: {ex.Message}"); }
    }

    // If a relocate was interrupted, its marker records the source; finish moving whatever's still there.
    private static void FinishInterruptedRelocate(string container)
    {
        string marker = Path.Combine(container, RelocateMarker);
        if (!File.Exists(marker)) return;
        try { RelocateData(File.ReadAllText(marker).Trim(), container); } catch { }
    }

    /// <summary>Different-drive fallback: if data still lives in the old folder on ANOTHER volume and no
    /// folder was chosen, point the setting at it (no cross-volume move). No-op after a same-volume relocate
    /// (legacy is then empty). Such a location can't be auto-wiped on uninstall — Settings warns.</summary>
    public static void AdoptLegacyDataDir(ISettingsStore store)
    {
        if (PackagePaths.ContainerRoot is null) return;
        if (ShouldAdoptLegacy(store.Current.DataDirectory, JotPaths.LegacyLocalAppDataDir, JotPaths.AppDataRoot))
        {
            store.Current.DataDirectory = JotPaths.LegacyLocalAppDataDir;
            store.Save();
            JotLog.Info($"adopted legacy data folder {JotPaths.LegacyLocalAppDataDir} (different volume; left in place)");
        }
    }

    // Testable core: adopt only when no folder is chosen, legacy != root, legacy has data, and the root doesn't.
    public static bool ShouldAdoptLegacy(string? dataDirectorySetting, string legacy, string root)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectorySetting)) return false;
        if (PathsEqual(legacy, root)) return false;
        return HasData(legacy) && !HasData(root);
    }

    /// <summary>One-time device-setting upgrade: the pre-GPU-tier default was "CPU", so existing users all
    /// carry "CPU" without ever having chosen it — migrate that to "Auto" (provably safe: no fp16 model
    /// shipped before this, so Auto can't route anyone to GPU until the probe passes on their machine).
    /// An explicit "GPU (DirectML)" pick is preserved. The marker makes any LATER explicit "CPU" pick
    /// sticky — migration never runs twice.</summary>
    public static void MigrateTranscriptionDevice(ISettingsStore store)
    {
        var s = store.Current;
        if (s.TranscriptionDeviceMigrated) return;
        if (s.TranscriptionDevice == Transcription.TranscriptionDevices.Cpu)
            s.TranscriptionDevice = Transcription.TranscriptionDevices.Auto;
        s.TranscriptionDeviceMigrated = true;
        store.Save();
    }

    /// <summary>One-time toggle-hotkey rescue: builds before 2026-07-23 DEFAULTED to Alt+Space, which
    /// Windows owns (system menu / focus steal) — users who simply kept that default carry it in
    /// settings.json forever. Move exactly "Alt+Space" to the current Ctrl+Shift+Space default once;
    /// the marker keeps a later DELIBERATE re-pick of Alt+Space sticky. Any other custom chord is
    /// untouched (it was a real choice).</summary>
    public static void MigrateToggleHotkey(ISettingsStore store)
    {
        var s = store.Current;
        if (s.ToggleHotkeyMigrated) return;
        if (s.ToggleRecordingHotkey == "Alt+Space")
            s.ToggleRecordingHotkey = "Ctrl+Shift+Space";
        s.ToggleHotkeyMigrated = true;
        store.Save();
    }

    /// <summary>One-time language-setting upgrade: older builds stored display names ("English",
    /// "Norwegian"); the picker now keys on locale codes ("en-US", "nb-NO"). Normalize preserves each
    /// name's exact prompt slot; unknown values become en-US (same fallback the engine applies anyway).
    /// Idempotent — normalizing a code is a no-op, so no marker is needed.</summary>
    public static void MigrateLanguageSetting(ISettingsStore store)
    {
        string normalized = Transcription.Nemotron.NemotronLocales.Normalize(store.Current.Language);
        if (normalized == store.Current.Language) return;
        store.Current.Language = normalized;
        store.Save();
    }

    private static bool HasData(string dir) =>
        Directory.Exists(Path.Combine(dir, "models")) || File.Exists(Path.Combine(dir, "library.json"));

    public static bool SameVolume(string a, string b) => string.Equals(
        Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);
}
