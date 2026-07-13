# Jot for Windows — Native UI Build Plan (v1, pre-review)

**Status:** DRAFT for independent review. No code yet. Scope = build **every visual
component** of the Windows app with the transcription engine left stubbed behind
`ITranscriber`. When this plan is solid and approved, we build; STT (milestone 2)
comes later.

**Guiding intent (from the user):** it must feel like a genuine **Windows 11** app —
*not* a ported Mac app. Aesthetic reference: **Willow Voice** and **SuperWhisper** on
Windows (minimal, floating-bar overlay, Fluent surfaces). The Mac app's
`docs/design-requirements.md` and `docs/features.md` define *what* Jot does; this plan
defines *how it looks and is structured on Windows*.

---

## 0. Review resolutions (v2 — authoritative delta over the rest of this doc)

Three independent reviews (Windows-native UX, WPF architecture, product completeness)
all returned "buildable from, with must-fixes." Their fixes, plus the user's refined
pill spec, override anything below that conflicts:

**Blockers / important — fold into the build:**
1. **Win10 no-Mica fallback (BLOCKER).** Mica (`DWMWA_SYSTEMBACKDROP_TYPE`) is Win11
   22000+ only; the app runs on Win10 19044 *today*. Design every surface to be
   self-sufficient on a **solid Fluent neutral background**; apply Mica only as
   progressive enhancement gated on `Environment.OSVersion >= 22000`. Test the Win10
   path before calling any screen done. (Both UX + arch reviewers.)
2. **`ShutdownMode=OnExplicitShutdown` — already set** in `App.xaml`. Keep it; drive exit
   only from tray Quit. (Blocker was pre-handled.)
3. **Flatten Settings nav (BLOCKER).** No NavigationView-inside-NavigationView. Settings
   is **one scrollable page of grouped `CardExpander`s** with an in-page category rail /
   anchor list — the Windows 11 Settings-app pattern — reached from a single top-level
   nav. Avoids the SuperWhisper "overwhelming settings" smell.
4. **Waveform/RMS threading (IMPORTANT), pulled into Phase 2.** `AudioRecorder` gains a
   throttled **live-RMS event (~30 Hz)** marshaled/coalesced onto the Dispatcher; the
   pill waveform is a simple `StreamGeometry`/polyline capped in redraw rate. New
   recorder plumbing, not polish-pass work.
5. **Grouped virtualized list (IMPORTANT).** Date grouping + virtualization requires
   `VirtualizingPanel.IsVirtualizingWhenGrouping="True"` + `ScrollUnit="Item"`, else WPF
   realizes every row. Set it and verify with a large seed.
6. **Custom titlebar work is real (IMPORTANT).** Snap Layouts need `WM_NCHITTEST` →
   `HTMAXBUTTON` over the custom maximize button; add `AutomationProperties` names to the
   custom caption buttons **and** the borderless pill so Narrator/UIA see them. Not a
   free checkbox.
7. **Modern toasts, not balloon tips (IMPORTANT).** Out-of-context events (update ready,
   mic fell back, error while hidden) use Windows App notifications (toast; AUMID +
   Start-shortcut registration for unpackaged WPF). The pill covers in-context state.
8. **Overlay geometry (IMPORTANT).** Anchor on the **active window's monitor** via
   per-monitor `GetMonitorInfo().rcWork` (not `SystemParameters.WorkArea`, primary-only);
   handle **auto-hide taskbars** (they don't reserve work area) and `WM_DPICHANGED`.
9. **Single-instance needs an activation channel (IMPORTANT).** Named mutex + a named
   pipe / `WM_COPYDATA` nudge so a second launch focuses the tray-resident instance.
10. **Correct factual error:** Windows `RegisterHotKey` **does** accept bare keys
    (F13–F24, media keys, etc.); the real constraints are (a) **no key-up event** (so PTT
    needs a `WH_KEYBOARD_LL` hook) and (b) OS-reserved combos. Fix the hotkey-recorder
    validation accordingly (§4.5/§8-Q4).
11. **PTT low-level hook details:** callback on the UI thread (needs a message pump),
    keep it trivial (marshal + return non-zero to swallow), beware `LowLevelHooksTimeout`
    auto-unhook and Defender heuristics on an unsigned exe.
12. **Mock playback honesty:** playback is real **only** for genuinely-recorded items;
    seeded/mock rows get a disabled player or a bundled sample WAV — don't claim playback
    on rows with no audio on disk.

**Missing surfaces to ADD to §4 (completeness review):**
- **Rewrite prompt-picker overlay** — searchable catalog overlay at rewrite time
  (pinned/recent float up, search by title/category, set-default). Sibling to the pill.
- **"Never lose audio" / pending items** — "Needs transcription" pending row + one-click
  re-transcribe + startup orphaned-audio adoption; empty-state handling.
- **Sidebar back/forward history** — nav history with **Alt+←/→** and a back button in
  the shell.
- **"Return to the app I started in"** — opt-in delivery toggle (Settings → General,
  advanced).
- **Mic-resilience surfaces** — silent-capture ("looks like a muted/BT mic") error;
  idle-fallback notice ("Recorded with system default — X unavailable"); mid-dictation
  salvage notice; mid-voice-command clean error. All pill states.
- **Diarization result rendering** — per-speaker colour blocks, right-click Rename
  speaker, single-speaker skip, Show-plain toggle.
- **Editable transcript detail** — Edit→Done save, "edited" marker, Show-original
  (raw/cleaned) toggle.
- **Smaller:** device "Last used (not connected)"; Ask Jot shortcuts (Ctrl+K new / etc.)
  + citation-opens-Help-card; live-preview text in pill; right-click "Add to
  Vocabulary…" from the transcript reader; inline row rename; drop-zone "browse"
  affordance; **taskbar jump list** (Toggle Recording / Copy Last / Open Jot).

**Sequencing tweaks:** pull **Prompts** into Phase 4 (Settings → AI and the rewrite
picker both depend on the catalog); stub an early `ISettingsStore` read in Phase 1 so the
theme choice survives restart; pull the recorder RMS-event plumbing into Phase 2.

**Non-goal decisions to name explicitly (§7):** `jot` CLI — **out** (GUI-only phase);
"Jot for iPhone" share-sheet row — **drop** (macOS-only; replace with a static link if
anything); Sparkle auto-update — **replace** with a Windows updater later (Check for
Updates is a stub now).

**Light mode is authored, not derived** — it's the Win11 default most users see; dark is
the design canvas but light must be first-class, not an inversion afterthought.

### Pill spec (user-refined — this is the signature surface)
A **single-line bar** by default:
- One horizontal line centered in a slim capsule. **Flat/straight line when no one is
  speaking; a live squiggly waveform that reacts to speech amplitude** (driven by the new
  RMS event) — a real reaction to the mic, not a canned loop.
- When streaming/live-preview is on, the bar can **show live transcript text** inline on
  that same single line as the user speaks.
- **Click to expand** into the fuller multi-line view (scrollable running transcript,
  Copy, state detail); **drag to move** anywhere (session-only position). Click-through
  when idle so it never steals input.
- Compact-first, matching the Willow "small bar" / SuperWhisper "mini pill" convention.
  If any part proves too costly, degrade gracefully (table the live-text-on-one-line
  refinement before the reactive waveform).

---

## 1. What "native Windows, not Mac" means concretely

A translation table so we never reflexively copy a Mac idiom:

| Mac idiom (current Jot) | Windows-native replacement |
|---|---|
| `NSVisualEffectView` vibrancy sidebar | **Mica** backdrop on the window (DWM system-backdrop on Win11); Acrylic for flyouts/overlay |
| Source-list sidebar (`NSSplitView`) | Fluent **NavigationView** (left nav, hamburger collapse, settings gear pinned bottom) |
| SF Symbols | **Segoe Fluent Icons** font (ships on Win11) |
| System semantic colors / System Blue accent | Windows **system accent color** (user's personalization pick) + Fluent neutral tokens |
| Dynamic Island pill "below the notch" | **No notch on Windows.** Floating pill anchored **bottom-center, above the taskbar** (Willow/SuperWhisper convention), draggable, click-through when idle |
| `⌘,` opens Settings, `⌘Q` quits | `Ctrl+,` Settings, standard Windows shortcuts; Settings is a nav destination, not a scene |
| Traffic-light window, unified/inset titlebar | Standard Windows caption buttons; custom titlebar that extends Mica into the caption; **Snap Layouts** support (maximize hover) |
| Menu-bar extra (`NSStatusItem`) | **System tray** `NotifyIcon` (already working) with a Fluent-styled context menu |
| Sheets / popovers | Fluent **ContentDialog** and **Flyout** |
| "Reveal in Finder" | "Show in File Explorer" (`explorer /select,`) |
| Rounded 10px cards, hairlines | Fluent **cards** (`CardControl`/`CardExpander`), 4/8px corner radii per Fluent, subtle 1px stroke over Mica |

**Rule:** rounded corners, Mica depth, Segoe Fluent iconography, system accent, and
Fluent motion curves. Dark mode is the design canvas (matches Mac history); light mode
mirrors it. Follow the user's Windows light/dark + accent settings automatically.

---

## 2. Technology decision (the load-bearing one — please stress-test)

**Recommendation: stay on WPF (.NET 10) and adopt the [WPF-UI](https://github.com/lepoco/wpfui)
Fluent library. Do NOT rewrite in WinUI 3.**

Rationale:

- **Keeps 100% of the proven skeleton.** Tray (`NotifyIcon`), global hotkey
  (`RegisterHotKey` message window), NAudio WASAPI capture, `SendInput` paste — all
  already work in WPF and would need re-solving under WinUI 3.
- **The overlay pill is trivial in WPF, painful in WinUI 3.** A click-through,
  always-on-top, screen-anchored window needs `WS_EX_TRANSPARENT` + `Topmost` +
  per-monitor DPI — a well-trodden WPF path. WinUI 3's windowing (`AppWindow`) makes
  click-through/topmost overlays fiddly.
- **Tray icon is first-class in WPF interop, second-class in WinUI 3** (needs
  `H.NotifyIcon` shims).
- **No-admin, per-user, unpackaged is native to WPF.** WinUI 3 unpackaged needs the
  Windows App SDK runtime bootstrapper — friction on this locked-down box (non-elevated,
  UAC auto-declines). Matches `windows-dev-setup` constraints.
- **We still get real Mica on Windows 11.** WPF-UI applies the DWM system-backdrop
  (`DWMWA_SYSTEMBACKDROP_TYPE`) so top-level windows get genuine Mica, plus a full
  Fluent control theme (light/dark, accent-aware), `NavigationView`, `CardControl`,
  Fluent `ContentDialog`, Segoe Fluent Icons, title-bar helper.
- **Cost:** WPF's render thread is DX9-era and coupled to the UI thread. For an app of
  this scale (a few list views + settings + a tiny animated pill) that is a non-issue.

Trade-off we accept: no compositor-level Acrylic *depth* beyond what WPF-UI provides;
mitigated because our surfaces are mostly flat Fluent cards on Mica.

**Alternatives considered:** WinUI 3 (most "native" but fights every one of our
integration needs); raw WPF Fluent theme in .NET 9+ (viable, but WPF-UI gives far more
out of the box — NavigationView, dialogs, snackbars, icon font, nav service);
Avalonia/Uno (cross-platform, but we are Windows-only and would lose native tray/overlay
ergonomics). **Decision to confirm in review: WPF-UI vs. the built-in .NET Fluent theme
alone.**

Supporting libraries: **CommunityToolkit.Mvvm** (source-gen `ObservableObject` /
`RelayCommand`), **Microsoft.Extensions.DependencyInjection** (composition root),
**Microsoft.Extensions.Hosting** optional. Persistence for history later:
**SQLite** (`Microsoft.Data.Sqlite`) — schema designed now, wired when real recordings
exist; UI reads a mock repository until then.

---

## 3. Design system (define once, in a Fluent resource dictionary)

- **Color:** neutral Fluent grays for surfaces (Mica shows through the window);
  `SystemAccentColor` for primary actions, selection, recording-active glow. Semantic:
  red (recording/destructive/error), green (success), amber (warning/"needs
  transcription"). All via WPF-UI theme resources so light/dark + accent track the OS.
- **Type:** **Segoe UI Variable** (Win11 system face) — Display / Title / Subtitle /
  Body / Caption ramp per Fluent type scale. **Cascadia Mono / Consolas** for
  transcripts, timestamps, and keybind chips.
- **Spacing:** 4px base unit (4/8/12/16/20/24/32) — matches Mac scale, also Fluent-legal.
- **Radius:** Fluent defaults — 4px controls, 8px cards, pill for toggles & the record
  button.
- **Iconography:** Segoe Fluent Icons glyphs (mapped to a small named-icon helper so we
  don't sprinkle raw code points).
- **Motion:** Fluent curves; short and decisive (~80ms hover/press, ~150ms state,
  ~200ms nav transition, one ~320ms "success reveal"). Honor
  `SystemParameters` reduced-motion / accessibility.
- **ThemeService:** central toggle — Follow system (default) / Light / Dark — applied at
  startup and live.

---

## 4. Component inventory & Windows adaptation

Mapped from `docs/features.md`. Each item notes the **Windows form** and what is **real
UI vs. stubbed data**. Everything below is *view + view-model + mock service*; the mock
services sit behind the same interfaces the real engine/repo will implement, so wiring
the engine later is a swap, not a rewrite.

### 4.1 Main window — Fluent `NavigationView` shell
- Custom Mica titlebar (drag region + caption buttons + Snap Layouts). App title +
  wordmark left; theme/settings affordances.
- Left nav destinations: **Recents**, **Ask Jot** (Advanced-gated), **Settings** (opens
  a settings sub-shell), **Help**, **About**. Settings gear pinned to nav footer (Fluent
  convention) in addition to a Recents-side entry.
- Single-instance: second launch focuses the running window (mutex + activation).
- **Close hides to tray; Quit (tray) exits.** Matches Mac "hide on close."

### 4.2 Recents (landing + library)
- Header: current-hotkey glance chips, dismissible first-run banner, big **record
  button** (circular, ~88px: idle/hover/recording-red-pulse/transcribing states).
- **Dictate drop-zone**: drag an audio/video file to transcribe (UI + drag-over cue;
  actual decode stubbed — accepts file, routes to stub transcriber).
- **Library list**: date-grouped (Today / Yesterday / Last 7 days…), virtualized
  (`VirtualizingStackPanel`) for long histories, leading icon per kind (waveform =
  dictation, wand = rewrite), inline copy button, right-click context menu
  (Re-transcribe, Show in Explorer, Copy, Delete-with-confirm). **Search box** +
  **tag-filter chip bar**. Data from a **mock `IRecordingStore`** seeded with sample rows.
- **Recording detail**: reading surface (serif-ish body → use a comfortable Segoe
  reading size; transcripts are prose), slim playback bar (play/pause + scrubber +
  elapsed/total; playback of the on-disk WAV is *real* since capture works), Edit toggle,
  tags editor, "Detect speakers" button (UI + stub result), Export .vtt (UI wired to a
  real file-save of stubbed cues).
- **Rewrite session detail**: stacked Instruction → Original → Rewritten panes (stubbed
  content).

### 4.3 Status pill overlay (the signature surface)
- Separate **borderless, topmost, transparent WPF window**, per-monitor-DPI aware,
  anchored **bottom-center above the taskbar** by default; **draggable** (session-only
  position, resets on relaunch — matches Mac).
- **Click-through when idle/recording** (`WS_EX_TRANSPARENT`), becomes interactive for
  Copy/info affordances on success/error.
- States (drive from a `PillState` enum, exhaustive switch): **Recording** (elapsed time
  + **live amplitude waveform** rendered from real mic RMS — capture is real, so this
  animates for real), **Transcribing** (3-dot loader), **Cleaning up**, **Rewriting**,
  **Did you mean X?** (vocab confirm with Use/Keep + ~10s timeout — UI only), **Success**
  (preview + copy), **Error** (message + info). Mini/compact vs expanded view
  (SuperWhisper-style hover expand). Acrylic background, subtle shadow + 1px stroke.

### 4.4 Tray menu (already native — restyle only)
- Toggle Recording (dynamic label), Copy Last Transcription, Recent Transcriptions
  submenu (last 10), Open Jot…, Check for Updates…, Quit. Keep `NotifyIcon`; give it a
  themed context menu and a proper `.ico`.

### 4.5 Settings (sub-shell with its own left list)
Panes, each a Fluent `CardControl`/`CardExpander` form (System-Settings-like grouped
rows, per-row info affordance opening a Fluent teaching tip / flyout that deep-links to
Help):
- **General** — input device picker (with live level meter — real, mic works), launch at
  login (Startup registry / Startup folder shortcut, per-user, no admin), library
  retention, semantic-search toggle, **Show advanced features** master toggle, run setup
  wizard again, Restart Jot, Reset group (Reset settings / Erase all data / Reset — with
  confirm dialogs).
- **Transcription** — language picker (model names hidden), install-state + footprint +
  download-progress row (stubbed states), auto-paste / auto-Enter / keep-clipboard
  toggles.
- **AI** — provider picker (OpenAI/Anthropic/Gemini/Ollama; **no Apple Intelligence** on
  Windows — replace with a Windows-appropriate default note), base URL / model / API key,
  cleanup toggle, editable-prompt disclosure, Test Connection (UI + stubbed result).
- **Prompts** — searchable catalog (bundled read-only + "My prompts" CRUD), editor
  dialog with ✨ Generate sample (stubbed), pin & set-default.
- **Vocabulary** (Advanced-gated, Experimental badge) — term list CRUD, learned
  corrections, boost-model status (stubbed).
- **Sound** — per-event chime toggles (start/stop/cancel/success/error) with preview
  (real `SoundPlayer`).
- **Shortcuts** — bindable rows (Toggle Recording, Push to Talk, Paste Last, Rewrite,
  Rewrite with Voice) with a **Windows hotkey recorder** control (captures modifier+key,
  validates against `RegisterHotKey` reality — Windows differs from macOS: single bare
  keys generally can't be global hotkeys; surface that constraint). Cancel = Esc note.

### 4.6 Setup wizard (first-run)
- Own centered, chromeless window; horizontal slide between steps; step indicator.
- Windows-appropriate steps: **Welcome → Permissions (Microphone privacy — deep-link to
  `ms-settings:privacy-microphone`; note Windows has no Input-Monitoring/Accessibility
  analog, so fewer gates) → Language/model (stubbed download) → Microphone (live meter)
  → Shortcut (bind + test) → Done → [advanced: AI provider → Cleanup → Rewrite intro]**.

### 4.7 Ask Jot, Help, About
- **Ask Jot**: Fluent chat pane (message bubbles, ASK JOT role label, streaming typing
  indicator, mic input button reusing pill states, starter prompts, New chat). Answers
  stubbed (canned) until an LLM path is wired.
- **Help**: Basics / Advanced / Troubleshooting cards; Windows-specific troubleshooting
  (mic privacy, hotkey conflicts, Defender/SmartScreen first-run note). Settings info
  tips deep-link here.
- **About**: identity, version, privacy pledge, Check for Updates (stub), donate link,
  troubleshooting log viewer + feedback composer (UI; redaction stub).

---

## 5. Project / code architecture

```
src/Jot/
  App.xaml(.cs)              ← composition root: DI container, ThemeService, tray, hotkey, pill lifetime
  Shell/                     ← MainWindow (NavigationView), custom titlebar, nav service
  DesignSystem/              ← ResourceDictionaries: colors, type, spacing, icon helper, converters, control styles
  Views/ + ViewModels/       ← one pair per surface (Recents, RecordingDetail, Settings.*, Wizard, AskJot, Help, About, Pill)
  Services/
    Abstractions/            ← ITranscriber (exists), IRecordingStore, ISettingsStore, IHotkeyService,
                               IThemeService, IAudioDevices, IUpdateService, ILlmClient, IPromptCatalog …
    Mock/                    ← in-memory / canned implementations that drive the UI now
  Recording/ Delivery/ Transcription/   ← existing working skeleton (unchanged)
docs/plans/windows-ui-plan.md
```

- **MVVM** via CommunityToolkit.Mvvm; views are XAML-only, logic in view-models.
- **DI** wires mock services now; real services (SQLite store, ONNX transcriber, LLM
  clients) drop in behind the same interfaces later with zero view changes.
- **Design-time data**: `d:DataContext` sample view-models so XAML previews render.
- **All engine/network work stays behind interfaces** and is explicitly out of scope for
  this phase (stub or mock). The record→(stub)transcribe→paste loop already works and
  stays functional throughout.

---

## 6. Build phasing (once approved)

1. **Foundation** — add WPF-UI + MVVM + DI; ThemeService; design-system dictionaries;
   MainWindow NavigationView shell with empty destinations; Mica titlebar. App still
   dictates via tray/hotkey throughout.
2. **Status pill** — the signature overlay window, all states, live waveform, drag,
   click-through; wire it to the real record/transcribe state machine (replacing balloon
   tips).
3. **Recents + detail** — library list, search, detail reading surface, playback, mock
   store.
4. **Settings** — all panes with real toggles persisted via `ISettingsStore` (JSON in
   `%LOCALAPPDATA%\Jot`); device picker + sound previews real; downloads/AI/vocab stubbed.
5. **Setup wizard** — first-run flow + re-runnable.
6. **Ask Jot / Help / About / Prompts / Rewrite detail** — remaining panes with
   canned/mock content.
7. **Polish pass** — motion, reduced-motion, keyboard nav, high-contrast, DPI, light/dark
   parity, tray restyle + `.ico`.

Each phase builds clean and launchable; nothing regresses the working dictation loop.

---

## 7. Explicit non-goals for this phase
- No real transcription model / ONNX / DirectML work (stays stubbed behind `ITranscriber`).
- No real LLM/cloud calls (Ask Jot, cleanup, rewrite, generate-sample are canned).
- No real model downloads, semantic-search index, diarization, or FFmpeg decode.
- No installer/packaging/signing (separate later milestone).

---

## 8. Open questions for the user / reviewers
1. **WPF-UI vs. the built-in .NET Fluent theme** — confirm the library choice.
2. **Apple Intelligence has no Windows analog.** Default AI provider on Windows =
   Ollama (local) or "none until configured"? Proposed: no default; user must pick.
3. **Overlay pill default position** — bottom-center above taskbar (proposed) vs.
   top-center vs. near tray. Willow uses a small bar near where you type; SuperWhisper a
   movable pill.
4. **Push-to-talk on Windows** — `RegisterHotKey` gives no key-up; PTT needs a low-level
   keyboard hook (`WH_KEYBOARD_LL`). Build the UI now, flag the hook as an
   implementation note for the engine phase.
5. **Scope of stubbed history** — ship a few realistic sample recordings for the demo, or
   start empty with an empty-state illustration? Proposed: seed a handful behind a debug
   flag; empty-state by default.
```
