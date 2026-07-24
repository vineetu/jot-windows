# Data storage, Erase, and uninstall — blast radius + fix (2026-07-23)

## The bug (reported)
Uninstalling the Store build (JOT / Jot Sie) from Add/Remove Programs left all data behind, and in-app
"Erase all data" *also* left data behind. Root causes, and why touching the save-location move earlier
made it worse:

1. **The Jot data root was recomputed in ~8 places** (`JsonSettingsStore`, `JotLog`, `PromptCatalog`,
   `FfmpegInstaller`, `ParakeetModel`, and two divergent delete lists in `App.WipeAllData` +
   `SettingsPage.OnEraseData`). A location change couldn't be made in one place, so cleanup drifted.
2. **Erase ran while the app held files open** (the model is memory-mapped, the library is open). The
   deletes hit sharing violations that were silently swallowed → data survived, but the app "restarted"
   looking done. This is the "clicked Erase, nothing happened" symptom.
3. **Erase's list was incomplete** — it missed `stats.json`, `logs/`, the `migration.json` marker, the
   `Run` (launch-at-login) registry entry, and never removed the data folder itself.
4. **MSIX runs NO code on uninstall.** Unlike a classic EXE/MSI uninstaller (which the old Velopack build
   had — `WipeAllData` still wipes everything there, including a custom drive), a Store package is removed
   by Windows with zero app code. The only thing Windows auto-deletes is the **package container**. Data
   the full-trust app wrote to the real `%LOCALAPPDATA%\Jot` (or a custom drive) persisted.

## The fix

**One data root.** `JotPaths.AppDataRoot` is now the single source: the **MSIX package container**
(`%LOCALAPPDATA%\Packages\<PFN>\LocalCache`) when packaged — which Windows auto-wipes on uninstall — else
`%LOCALAPPDATA%\Jot`. Every former hardcoder routes through it (`JotPaths.ConfigDir` / `AppDataRoot`).
`PackagePaths` resolves identity + container via P/Invoke (no WinRT dependency).

**One wipe list.** `JotDataPurge` enumerates every artifact (data dir — any drive — + config dir) and is
the single routine both Erase and the (Velopack) uninstaller call, so they can't drift. It deletes only
Jot's *named* children (safe on a shared folder), retries briefly to ride out a just-exited process, and
removes the now-empty folders + the `Run` entry.

**Reliable Erase (deferred wipe).** The running app can't delete its own mapped model, so Erase writes a
`wipe.json` marker (capturing the resolved data dir, custom drive included) and restarts; `App.OnStartup`
consumes it and purges **before any service opens a file**, then falls through to first-run.

**Container default + upgrade that actually consolidates.** New installs store everything in the container
→ uninstall removes every trace. Existing installs: config files are **moved** into the container (settings
/prompts/FirstRunComplete carry over — no wizard re-run), and the data is **relocated** into the container
when it's on the **same volume** (a rename — instant, no copy, no extra space), so an upgraded install also
ends up fully inside the auto-wiped container. This was originally "adopt in place," which was wrong: it
left same-drive data outside the container so uninstall still missed it. Only data on a **different drive**
(a deliberately-chosen location) is adopted in place — it's outside the container and can't be auto-wiped
by MSIX, so Settings warns to Erase it first. Relocation is crash-resumable via a `relocating.marker`.

## Verification
- Builds: Public + Sony both 0/0.
- Unit tests: **160 pass** (12 new — `JotDataPurgeTests`, `StartupMigrationTests`): artifact coverage,
  full purge on any drive, shared-folder safety, deferred-wipe marker roundtrip, config-move + data-adopt
  decisions.
- `--datapaths` dev hook (writes `%TEMP%\jot-datapaths.txt`) confirmed unpackaged resolution + the full
  wipe list in a real process.
- **NEEDS A PACKAGED RUN (can't be verified off a real MSIX install):** run `Jot.exe --datapaths` on the
  installed package to confirm `ContainerRoot` is correct; then an install → use → **uninstall** to confirm
  the container (and thus all data) is gone; and an upgrade-over-existing-install to confirm settings/data
  carry across. These are the only unverified steps.
