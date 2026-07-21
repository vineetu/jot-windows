# Jot for Windows — ARCHITECTURE

> **Draft — initial agent-generated bootstrap, to be refined by the maintainer.** It was written by
> reading the source; the subsystem boundaries and invariants below are real but coarse. Correct
> anything that looks off and keep it current on subsystem/boundary changes.

## Orientation

This is a **coarse, stable code map at subsystem granularity** — where each subsystem lives, its
entry-point files/symbols, and the cross-cutting invariants that reading a single file won't reveal. It
is deliberately **not** a feature spec: it does not restate feature prose, it points back to
[`docs/features.md`](docs/features.md) (the WHAT). It names subsystems and contracts, not individual
functions or line numbers, so it survives refactors.

**Stack.** WPF on .NET 10 (`net10.0-windows`, x64) + WPF-UI (Fluent). On-device speech-to-text is NVIDIA
Nemotron 3.5 streaming multilingual (int4 ONNX) via ONNX Runtime — CPU by default, optional DirectML GPU
backend. Microphone capture is WASAPI (NAudio); paste is a SendInput clipboard-sandwich; global hotkeys
are Win32 `RegisterHotKey`; the tray is a WinForms `NotifyIcon`. Optional AI (cleanup / rewrite / Ask Jot)
is bring-your-own-provider (OpenAI / Anthropic / Gemini / local Ollama) over raw HTTP. Everything runs in
a **single process** — there is no keyboard extension, watch app, or App Group as on iOS.

Source lives under `src/Jot/`; tests under `tests/Jot.Tests/`; the build/packaging script and plans under
the repo root and `docs/`.

## Code map

| Subsystem | Responsibility | Entry points (files / symbols) |
|---|---|---|
| App host & lifecycle | Process startup, DI container wiring, tray, single-instance claim, update/uninstall hooks, packaged-vs-unpackaged detection, wiring every service to the pill/tray. | `App.xaml.cs` (`OnStartup`, `SetupTray`, `ClaimSingleInstance`, `IsRunningAsPackagedApp`, `WipeAllData`) |
| Recording & pipeline | The record → transcribe → clean → save → paste state machine; stop-and-save Esc arming; origin-window capture. | `Recording/RecorderController.cs` (`RecorderState`, `Toggle`, `StopAndDeliverAsync`), `Recording/AudioRecorder.cs`, `Recording/LiveTranscription.cs` |
| Hotkeys | Global `RegisterHotKey` on a message-only window; chord parsing; the (unused) low-level hook scaffolding. | `Recording/GlobalHotkey.cs`, `Recording/HotkeyManager.cs`, `Recording/HotkeyChord.cs`, `Recording/LowLevelHotkeys.cs` |
| Transcription engine | On-device STT: Nemotron int4/fp16 streaming (cache-aware RNNT), mel frontend, model install, language table. | `Transcription/Nemotron/` (`NemotronTranscriber.cs`, `NemotronFp16Transcriber.cs`, `NemotronModel*.cs`, `MelFrontend.cs`, `NemotronLanguages.cs`, `NemotronModelInstaller.cs`), `Transcription/ITranscriber.cs` (`IStreamingTranscriber`) |
| Offline text cleanup | Deterministic, always-on, non-AI tidy every transcript passes through. | `Text/TextPipeline.cs` (`Clean`, `CleanPartial`), `Text/ModelArtifactScrubber.cs`, `Text/FillerWordCleaner.cs`, `Text/NumberNormalizer.cs`, `Text/LanguageCode.cs` |
| Delivery | Clipboard-sandwich paste via SendInput; foreground-window capture/restore; UI-Automation selection reader (for the not-yet-wired rewrite). | `Delivery/TextInjector.cs` (`PasteAtCursor`, `CaptureForegroundWindow`), `Delivery/UiaSelectionReader.cs` |
| Status pill & waveform | Borderless topmost overlay reflecting pipeline state; live RMS waveform; expand-to-transcript. | `Services/PillController.cs`, `Controls/PillWindow.xaml(.cs)`, `Controls/PillState.cs`, `Controls/WaveformView.cs`, `Controls/MicMeter.cs` |
| Library & storage | The Recents model + persistence (JSON library), retention pruning, usage stats. | `Services/JsonRecordingStore.cs`, `Services/Abstractions/IRecordingStore.cs`, `Models/RecordingItem.cs`, `Services/RetentionCleaner.cs`, `Services/UsageStats.cs`, `Services/JotPaths.cs` |
| Recents/Detail UI | The library list, detail reading surface, edit, Find & Replace, tags, WebVTT export, playback. | `Views/RecentsPage`, `Views/RecordingDetailPage`, `ViewModels/RecentsViewModel.cs`, `ViewModels/RecordingDetailViewModel.cs` |
| Media import | Drag/browse audio-video file → decode via downloaded FFmpeg → transcribe as a normal row. | `Import/MediaImporter.cs`, `Import/FfmpegInstaller.cs` |
| AI providers | Bring-your-own-provider HTTP client (cleanup / rewrite / ask), test-connection probes, defaults, DPAPI key store, optional Sony PFB gateway. | `Services/Ai/AiClient.cs` (`CleanupAsync`, `TestConnectionAsync`), `Services/Ai/IAiClient.cs`, `Services/Ai/AiCredentials.cs`, `Services/Ai/AiDefaults.cs`, `Services/Ai/PfbGateway.cs`, `Services/Ai/PfbAuth.cs` |
| Rewrite | Select → rewrite → paste-back controller and prompt picker (advertised-but-not-yet-wired). | `Rewrite/RewriteController.cs` (`BeginRewrite`, `ToggleVoiceRewrite`), `Controls/PromptPickerWindow.xaml(.cs)`, `ViewModels/PromptPickerViewModel.cs` |
| Prompt library | Custom-prompt CRUD/pin/default, bundled catalog, persistence. | `Services/PromptCatalog.cs`, `Models/PromptItem.cs`, `Views/PromptsPage`, `ViewModels/PromptsViewModel.cs` |
| Ask Jot | Advanced-gated help chat routed to the configured provider with offline fallback. | `Views/AskJotPage`, `ViewModels/AskJotViewModel.cs` |
| Settings | Basic/advanced settings surface + the settings store; theme; sounds. | `Views/SettingsPage`, `ViewModels/SettingsViewModel.cs`, `Services/JsonSettingsStore.cs`, `Services/Abstractions/ISettingsStore.cs`, `Services/ThemeService.cs`, `Services/SoundService.cs` |
| Shell & navigation | The single Fluent window, central page-scroll + FillHeight nav fix, back/forward, hide-to-tray. | `Shell/MainWindow.xaml(.cs)`, `Controls/NavContentHost.cs`, `Controls/PageScrolling.cs`, `Services/Navigation/Navigator.cs` |
| Setup wizard | First-run guided flow (welcome / mic permission / how-it-works), re-runnable. | `Views/SetupWizardWindow.xaml(.cs)`, `ViewModels/WizardViewModel.cs` |
| Community surfaces | Donations window + summary fetch, feedback composer + POST. | `Controls/DonationsWindow`, `Controls/DonationNudgeWindow`, `Services/DonationsService.cs`, `Controls/FeedbackWindow`, `Services/FeedbackClient.cs` |
| Packaging & updates | Velopack (Setup.exe) hooks + the MSIX/Store build; packaged-app detection gates the two apart. | `App.xaml.cs` (`VelopackApp.Build`, `IsRunningAsPackagedApp`), `build-msix.ps1`, `src/Jot/Package.appxmanifest`, `app.manifest` |

## Cross-cutting invariants

These are the boundary rules that reading one file won't reveal. Break one and something fails silently.

- **Single process, single instance.** Everything is in one WPF process; a second launch focuses the
  running instance (named mutex + activation event) rather than starting a duplicate. There is no
  cross-process channel to keep in sync — the iOS App-Group/keyboard model does not apply here.

- **DPAPI key store, with a self-healing fuse.** AI provider API keys are persisted encrypted at rest via
  Windows DPAPI (`ProtectedData`, `DataProtectionScope.CurrentUser`) as a provider→key JSON map in
  `<DataDir>\aikey.dat`. The store carries a **load-fuse invariant**: while the on-disk store hasn't been
  faithfully loaded, saving refuses to overwrite/delete the file, and a transient load failure leaves the
  previously-loaded keys in memory. Both guards exist because earlier bugs wiped a saved key or made
  "Test connection" pass while real use failed. Don't relax them.

- **Velopack + MSIX dual packaging.** The same binary ships two ways: a Velopack Setup.exe/self-updater
  (unpackaged) **and** an MSIX/Store package. `IsRunningAsPackagedApp()` (raw P/Invoke, no WinRT
  dependency) branches them: Velopack's install/update/uninstall hooks run **only** when unpackaged — the
  Store owns the packaged lifecycle, so running Velopack there would misfire. Keep packaged-vs-unpackaged
  logic behind this one check.

- **Nemotron-only, no per-word timings.** There is one speech engine (Nemotron), streaming via a
  cache-aware RNNT session whose encoder cache + decoder state thread forward per chunk. It does **not**
  surface per-word timestamps, so WebVTT export fabricates fixed-length cues — treat any timing-dependent
  feature as blocked until the engine surfaces real timings. Tensor cache dimensions
  (`EncLayers/ChannelCache/Hidden/TimeCache`) and layer ordering differ between the int4 and fp16
  transcribers and are byte-sensitive; they must match the validated reference.

- **Native runtime pinning is load-bearing.** ONNX Runtime is pinned to a specific native build, and a
  **newer DirectML** (`Microsoft.AI.DirectML`) is layered on top of the one ORT bundles because the older
  runtime rejects Nemotron's int4 ops on this GPU. The `Microsoft.Windows.SDK.BuildTools.WinApp` package
  is used **instead of** `Microsoft.WindowsAppSDK` specifically because the latter transitively ships its
  own `onnxruntime.dll` that collides with the STT one (APPX1101 duplicate-payload build error). Don't
  swap these package choices casually.

- **Icon is Resource-only, not Content.** `jot.ico` is embedded as a WPF `<Resource>` and explicitly
  **excluded** from the `<Content>` copy glob. Listing the same file as both drops the embedded copy, the
  `pack://` URI resolves to null, and the tray/window icon load crashes startup. This is a real trap
  guarded by a comment in `Jot.csproj`.

- **Ship the runtime alongside new DLLs.** Because the MSIX is a **self-contained** publish
  (`dotnet publish --self-contained`) that `makeappx` packs from the `publish/` folder, any new native or
  managed dependency must actually land in that publish output (and its manifest/`.deps.json` entry) or
  the packaged app fails to load it while the local `dotnet run` still works. *(Maintainer: verify the
  exact `.deps.json` handling — this invariant is inferred from the publish+pack flow, not from explicit
  code.)*

- **Data lives off the system drive by default.** The default data folder picks the roomiest fixed
  non-system drive (falling back to `%LOCALAPPDATA%\Jot` on single-drive PCs); recordings, the JSON
  library, models, and the key store all resolve under it and follow a changed save location. **Known
  gap**: log files still write to the per-user location regardless of the chosen data folder.

- **Central page-scroll + FillHeight nav fix.** WPF-UI's `NavigationView` measures hosted pages with
  *infinite* height, so a page-level scroller never gets a bounded viewport and content grows past the
  window. The shell applies `NavContentHost.FillHeight` on every navigated page to bind its height to the
  bounded host (subtracting the page's own margin so bottom rows don't clip). Keyboard paging and
  wheel-over-text scrolling are also handled once in the shell — pages don't manage their own scroll.

- **Offline TextPipeline order is fixed.** Every dictation runs
  `ModelArtifactScrubber → FillerWordCleaner(lang) → NumberNormalizer(English-only)` before the
  empty-transcript gate, so an all-filler take still routes to "nothing transcribed." Filler cleaning
  gates on the major Western-European languages; number normalization is a hard English gate; unknown
  languages pass through byte-identical. A cosmetic scrubber-only variant runs on the live-caption
  partial. This is deterministic and shares no mutable state between the recorder and pill threads. It is
  **separate** from the optional AI cleanup.

- **Esc stops and saves; discard is unbound.** While recording, a global Esc is armed that routes through
  the normal stop→transcribe→save path so a stray Esc can never lose a take. The discard path exists as an
  API but is deliberately bound to no key. The stop chord is a fixed constant shared by the shortcuts UI,
  the pill hint, and the arming logic so they can't drift.

- **Build flavors: Public vs Sony.** A `Flavor` MSBuild property (default `Public` = the Store build with
  bring-your-own cloud providers) gates a `SONY` compile symbol that swaps in the internal PFB gateway as
  the only online AI (external providers removed, gateway hostnames compiled in). Provider wiring must
  respect the flavor gate.

## Keeping this current

Two docs, two triggers:

- **[`docs/features.md`](docs/features.md)** — update on **any user-facing change**: a new/changed/removed
  feature, a caveat becoming real (or a real feature regressing), a control appearing/disappearing.
- **This file** — update **only on subsystem/boundary changes**: a new subsystem, a moved boundary, a new
  or retired cross-cutting invariant, a changed packaging/native-dependency contract. Most feature edits
  will *not* touch this file — it is intentionally coarse so it survives refactors. A stale row (moved
  file, dead invariant) is a bug; fix it in the same change that moved the boundary.
