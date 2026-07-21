# Jot for Windows — Feature Inventory

> **This is the canonical Windows feature list — keep it updated here.** The macOS
> `JOT-Transcribe/docs/features.md` is only a historical reference; do not treat it as the
> source of truth for what Windows does. When you ship or change a Windows feature, update the
> status marker below.

**Related docs:** this file = *what exists + status*; `docs/plans/fixit-worklist.md` = *what's
broken / the todo backlog* (referenced by item ID below); `docs/plans/windows-ui-plan.md` = *the
build plan / architecture*. Keep the three in sync.

**Stack (Windows reality):** WPF on .NET 10 + [WPF-UI](https://github.com/lepoco/wpfui) Fluent
theme. Transcription is on-device via **NVIDIA Nemotron 3.5 streaming multilingual 0.6B (int4 ONNX)**
through ONNX Runtime, CPU by default with an optional **DirectML** GPU backend. Audio capture is
**WASAPI** (NAudio); paste is **SendInput** clipboard-sandwich; global hotkeys via `RegisterHotKey`;
tray via `NotifyIcon`. Optional AI (cleanup / rewrite / Ask Jot) is **bring-your-own-provider**:
OpenAI, Anthropic, Gemini, or local Ollama over raw HTTP. There is **no Apple Intelligence, no
FluidAudio/Parakeet-on-ANE, and no ⌘-key / NSPanel** on Windows.

Status markers:
- ✅ **Works** — implemented and verified real in code.
- ⚠️ **Partial** — present but incomplete (what's missing is noted).
- ❌ **Missing / planned** — not built yet.
- 🚫 **N/A on Windows** — macOS-only; the Windows equivalent (if any) is noted.

---

## Core dictation & recording

- ✅ **Toggle recording** — press the hotkey (default **Alt+Space**) to start, press again to stop
  and transcribe. Also from the tray menu ("Start/Stop dictation") and the Recents record button.
  (`RecorderController.Toggle`)
- ✅ **Any-length recordings** — no hard duration cap; WASAPI capture streams to disk.
- ⚠️ **Cancel recording** — a global **Esc** hotkey discards the in-flight recording while
  recording. Two problems: it registers **bare Esc system-wide**, so dismissing an unrelated popup
  (UAC / dialog) mid-dictation kills the take, and it **discards** rather than saving. Target is
  stop-and-**save** + contextual (non-global) Esc routing. (worklist D8)
- ⚠️ **Per-device microphone selection** — pick any WASAPI input device in Settings; the choice is
  remembered. Missing the "Last used (not connected)" placeholder for a disconnected preferred
  device. (worklist C: mic-resilience)
- ⚠️ **Never lose audio** — a busy/errored transcribe still lands as a "Needs transcription" pending
  row that you re-transcribe with one click (`RecordingDetailViewModel.ReTranscribe`). Missing the
  startup **orphaned-audio adoption scan** that the Mac does. (worklist C: never-lose-audio)
- ❌ **Push to talk** (hold-to-record) — a `PushToTalkHotkey` field exists but there is no Settings
  row and no key-up support; `RegisterHotKey` gives no key-up, so this needs a `WH_KEYBOARD_LL`
  low-level hook. (worklist C: Push-to-Talk)
- ❌ **Silent-capture detection** — no zero-amplitude / muted-BT-mic detection or actionable error.
  (worklist C)
- ❌ **Graceful mic-disconnect handling** — no idle-fallback notice, mid-dictation salvage, or
  mid-voice-command clean error. (worklist C)

## Transcription engine (on-device)

- ✅ **On-device only** — audio is transcribed locally by Nemotron int4 ONNX; nothing leaves the PC
  via the STT path. (`NemotronTranscriber`)
- ✅ **Genuinely streaming** — a cache-aware FastConformer/RNNT session threads the encoder cache +
  decoder LSTM state forward per 560 ms chunk, so stopping is near-instant (only the tail remains).
  Byte-exact to the validated Python reference. This powers **live captions** (below).
- ✅ **Multilingual via one model** — a single Nemotron model covers ~33 languages, selected by a
  `lang_id` conditioning index (English confirmed = 0; Spanish/Hindi/Portuguese/Vietnamese
  empirically confirmed; a lower-confidence "adaptation" tier exists). Unlike the Mac, this is
  **one model**, not per-language Parakeet/Nemotron/JA downloads — so language switching is
  instant and there is no per-language download. (`NemotronLanguages`)
- ✅ **In-app model download** — the model is fetched from within Settings on first use with a
  progress bar and an install-state row. (`NemotronModelInstaller`, Settings → Transcription)
- ✅ **CPU / GPU (DirectML) processing** — a Settings toggle picks the compute backend; GPU falls
  back to CPU automatically. (applies after restart)
- ✅ **Live captions while recording** — an opt-in toggle streams a running partial transcript into
  the status pill as you speak. (`LiveTranscription`, `RecorderController` PartialTranscript)
- 🚫 **Deterministic post-processing chain** (filler-strip / number-normalize / paragraph
  segmentation) — this was a Mac **Parakeet-v2-only** cleanup because that model emitted raw text.
  Nemotron emits cased, punctuated text natively, so the chain is **not applicable**; running it
  would risk regressing correct casing. Optional LLM cleanup covers filler removal instead.
- ❌ **Startup model self-healing** — no launch-time verify-that-the-model-actually-loads /
  surgical re-download / "temporarily use another model during repair" logic.

## AI: cleanup, rewrite & prompts

Optional and **bring-your-own-provider** (OpenAI / Anthropic / Gemini / Ollama). Ollama runs fully
local; the rest need an API key.

- 🚫 **Apple Intelligence provider** — no Windows analog. Replaced by the "Bring your own provider"
  model above (surfaced as an InfoBar in Settings → AI).
- ✅ **Transcript cleanup** — off by default; when enabled + a provider is configured, each transcript
  runs a light cleanup pass (punctuation, capitalization, filler removal) with a 30 s budget and a
  **model-independent faithfulness guard** that discards over-eager rewrites and keeps the raw text.
  Graceful fallback to raw on any failure. (`AiClient.CleanupAsync`)
- ✅ **Test connection** — a real credentialed probe per provider (GET /models, 1-token messages
  call, /api/tags) with friendly HTTP error messaging. (`AiClient.TestConnectionAsync`)
- ❌ **Rewrite (no voice)** — DOES NOT WORK on Windows 11 (user-tested). The code path exists
  (`RewriteController.BeginRewrite`) but the selection is captured empty, so it just says "select text
  first." (worklist A4)
- ❌ **Rewrite with voice** — DOES NOT WORK (same root cause). (`RewriteController.ToggleVoiceRewrite`,
  worklist A4)
- ⚠️ **Rewrite prompt picker** — the overlay UI exists (`PromptPickerWindow`) but is unreachable in
  practice because the Rewrite hotkey/selection doesn't work. (worklist A4)
- ❌ **Selection capture (synthetic Ctrl+C via SendInput) is unreliable on Win11** — the root cause of
  the above. A longer clipboard poll did NOT fix it; needs UI Automation `TextPattern` + Win11 input-
  permission (UIPI) research. (worklist A4)
- ❌ **Rewrite / paste-last / rewrite-with-voice hotkeys** — not registered and hidden from Settings
  (only Toggle + Cancel shown) until they actually work. (worklist A4/A5)
- ❌ **Rewrite intent classifier** — Mac routes instructions into voice-preserving / structural /
  translation / code branches; Windows uses one shared rewrite preamble. (worklist C: intent classifier)
- ❌ **Editable prompts** — the cleanup prompt and the rewrite invariants are hardcoded constants in
  `AiClient`; no "Customize prompt" / "Reset to default" UI, and the cleanup prompt is not a managed
  catalog entry. (worklist C: editable prompts)
- ✅ **Prompt library CRUD + pin + default** — author/edit/delete custom prompts, pin any prompt,
  set one as the default fired by a bare Rewrite tap; all persisted to `prompts.json`. Search filters
  the catalog. (`PromptCatalog`, `PromptsPage`)
- ⚠️ **Bundled prompt catalog** — 15 bundled prompts across Essentials / Convert / Email / Rewrite /
  Code / Translate (Mac ships 30+). No sample input/output, no read-only detail sheet, no
  provider/voice-compatibility metadata. (worklist C: prompt library depth)
- ❌ **AI-assisted authoring ("Generate sample")** — not built (tied to the missing sample fields).
  (worklist C)

## Ask Jot (help assistant)

- ⚠️ **Ask Jot chat pane** — an Advanced-gated chat pane routes questions to the configured provider
  and shows offline fallback answers when none is set. **Grounding is a single hardcoded facts
  string**, not the Mac's doc-grounded retrieval. Missing: feature-citation links that open Help
  cards, in-chat voice input, response streaming, and the Ctrl+K-style shortcuts. (worklist C: Ask Jot depth)

## Output — paste & clipboard

- ✅ **Auto-paste at cursor** — SendInput clipboard-sandwich into the frontmost app. (`TextInjector.PasteAtCursor`)
- ✅ **Press Enter after paste** — optional; pastes and sends in one step.
- ✅ **Return to the app I started in** — opt-in (Advanced); captures the foreground window at start
  and delivers back to it. Off by default.
- ✅ **Clipboard preservation** — keep the transcript on the clipboard, or restore the previous
  contents after paste.
- ⚠️ **Copy last transcription** — the tray and Recents-row (button) paths work; the **Paste-last
  hotkey** path does NOT work on Win11 (same selection/paste issue as rewrite). (worklist A4)
- ✅ **Quick copy from any row** — inline copy on every Recents row. (`RecentsViewModel.Copy`)

## Library & transcripts (Recents)

- ✅ **Single library surface** — Recents interleaves dictation and rewrite items chronologically.
- ✅ **Date-grouped, virtualized list** — Today / Yesterday / Last 7 days buckets; virtualized for
  long histories. (`RecentsViewModel`)
- ✅ **Substring search** — filters across title, transcript, rewrite instruction/original, and tags.
- ✅ **Tag filter chips** — tap a tag to filter; add/remove tags in the detail view.
- ✅ **Recording detail reading surface** — transcript + a slim playback bar (play/pause + scrubber),
  with **real playback** for rows that have on-disk audio. (`RecordingDetailViewModel`)
- ✅ **Editable transcripts** — Edit → Done saves an edited transcript and sets an "edited" marker.
- ✅ **Rewrite session detail** — stacked Instruction → Original → Rewritten view.
- ✅ **Import an audio or video file** — drop or browse for a file; the bundled **FFmpeg** decodes
  virtually any format (mp3/mp4/m4a/mov/webm/mkv/ogg/opus/flac/aac/wav/…) to 16 kHz mono, then it
  transcribes on the same on-device engine and lands as a normal row. Shows a pending row while it
  works. (`MediaImporter`)
- ❌ **AI semantic search** — Windows is substring-only; no on-device embedding index. (worklist C: semantic search)
- ✅ **Transcript text selection** — read-mode transcript + rewrite panes are read-only borderless
  `TextBox`es, so text can be drag-selected and Ctrl+C'd (Copy button still works too). (worklist A1)
- ✅ **Overflow "…" menu opens on click** — the detail-view "…" button opens its menu (Find & Replace /
  Export WebVTT / Reveal / Delete) on left-click, not just right-click. (worklist A2)
- ✅ **Find & Replace in the transcript** — a bar (… menu or Ctrl+F) with Find/Replace fields, live
  match count, Match-case, Replace and Replace all; edits persist to the library. (worklist A2)
- ⚠️ **Export transcript as WebVTT** — an Export button writes a `.vtt`, but timestamps are
  **fabricated** (fixed 4 s cues) because the engine doesn't surface word timings yet. (worklist C: WebVTT timings)
- ❌ **Show original vs cleaned** — only one transcript is stored, so there's no raw/cleaned toggle.
  (worklist C: show-original)
- ❌ **Speaker diarization ("Detect speakers")** — a **stub** that fakes speakers by alternating on
  sentence boundaries; currently **hidden** so users can't hit it. Needs real on-device diarization.
  (worklist B2)
- 🚫 **`jot` command-line tool** — explicit non-goal for the Windows GUI phase.

## Status pill (overlay)

- ✅ **Floating pill overlay** — a borderless, topmost WPF window (bottom-center, above the taskbar)
  that reflects pipeline state without stealing focus. Draggable; expandable. (`PillWindow`)
- ✅ **Live amplitude waveform** — a real reactive waveform driven by mic RMS (flat when silent,
  squiggly on speech), not a canned loop. (`WaveformView`)
- ✅ **Live preview text + expand** — inline running transcript when live captions are on; click to
  expand into a scrollable multi-line transcript with Copy and a Stop button (recently shipped).
- ⚠️ **State coverage** — Recording / Transcribing / Success / Error are covered. Missing dedicated
  **Cleaning up**, **Rewriting**, and **"Did you mean X?"** (vocab confirm) states the Mac shows —
  cleanup/rewrite currently fold into the Transcribing/Working states.

## Global shortcuts & tray

- ⚠️ **Hotkeys** — only **Toggle recording** and **Stop & save** (Esc) are active. Paste-last / Rewrite /
  Rewrite-with-voice are **not registered** (they don't work — worklist A4). ⚠️ **Rebinding re-enabled**
  on the **Shortcuts** left-nav page (editable `HotkeyBox`; the click-to-focus bug is fixed and capture
  verified — physical click+rebind awaits a hands-on test). (`HotkeyManager`, `ShortcutsPage`, worklist A5)
- ✅ **Pill key hint** — while recording, the status pill shows the stop/cancel chords under the waveform
  (e.g. `Alt + Space to stop  ·  Esc to cancel`), read live from settings. (`PillWindow.SetKeyHints`)
  - Windows note: `RegisterHotKey` **does** accept some bare keys (F13–F24, media keys); the real
    constraints are no key-up event and OS-reserved combos.
- ✅ **System tray menu** — Start/Stop dictation (dynamic label), Copy last transcription, Recent
  transcriptions submenu (last 10, click to copy), Open Jot…, Quit Jot. Closing the window hides to
  tray; Quit exits. (`App.SetupTray`)
- ❌ **"Check for updates…" in the tray** — hidden because it's a canned "up to date" with no network
  check. (worklist B3)
- ⚠️ **Uninstall via "Add or remove programs" + wipe data** — the Velopack uninstall hook
  (`WipeAllData`) is wired to delete all Jot data + the launch-at-login entry on removal; the
  Add/Remove Programs entry appears once Jot is installed via the Velopack `Setup.exe` (packaging
  step pending). (worklist B3b)

## Main window, navigation & settings

- ✅ **Fluent NavigationView shell** — left nav: Recents, **Shortcuts**, Help, About, and Settings
  (footer). Ask Jot + Prompts are currently **hidden** (AI off). Custom Mica-capable titlebar. (`MainWindow`)
- ✅ **Single instance** — a second launch focuses the running tray-resident instance (named mutex +
  activation event). (`App.ClaimSingleInstance`)
- ✅ **Show advanced features toggle** — master switch that reveals Ask Jot, extra shortcuts, and the
  (currently hidden) Vocabulary surface.
- ⚠️ **Sidebar back/forward history** — a `Navigator` with GoBack exists; Alt+←/→ shell affordances
  are not fully wired. (worklist / windows-ui-plan)
- ✅ **Settings — basic vs advanced** — by default only **Appearance, Microphone, Language**, and the
  **Show advanced features** toggle are shown. Turning it on reveals the rest (launch-at-login, save
  location, keep-audio retention, model download, CPU/GPU processing, live captions, auto-paste,
  press-Enter, keep-clipboard, per-event sound toggles + preview, reset). AI provider/key/cleanup and
  Vocabulary stay hidden until they work. (`SettingsPage`, `SettingsViewModel`, `SoundService`)
- ✅ **Reset group** — Reset settings and Erase all data (with confirmations).
- 🚫 **Reset permissions** — macOS `tccutil` has no Windows analog; Windows mic access is managed in
  OS Settings, so there's no in-app reset.
- ❌ **Custom vocabulary** — the terms list is in-memory only, never persisted or fed to the decoder;
  currently **hidden**. Needs real persistence + decoder biasing. (worklist B1)
- ❌ **Per-field info popovers / "Learn more →" deep-links** — Mac has an info dot on every field.
  (worklist C: info popovers)
- ✅ **"Restart Jot" troubleshooting action** — About → Troubleshooting relaunches the app cleanly.
- ✅ **Usage impact / "time saved"** — on-device counters (`UsageStats`, `stats.json`) surface words,
  recordings, and estimated minutes saved vs typing on the About page; nothing leaves the PC. (worklist D2)
- ✅ **Donate to charity popup** — About → Donate opens an in-app window that fetches the live donations
  summary (`jot-donations.ideaflow.page/summary`) and lists charities with per-charity Donate buttons
  (out to Every.org); Jot never handles money or PII. (worklist D1)
- ✅ **Send feedback (API, not email)** — About → Send feedback opens an in-app composer that POSTs to
  the feedback service (`jot-donations.ideaflow.page/feedback`); no mailto/email client. (worklist D3)
- ✅ **Setup wizard** — first-run guided flow, re-runnable from Settings. (`SetupWizardWindow`)

## About, updates & feedback

- ✅ **App identity / version / privacy pledge / Help pane** (Basics / Advanced / Troubleshooting).
- ❌ **Check for updates** — hardcoded "you're on the latest preview build"; no network check.
  (worklist B3)
- ❌ **Auto-update** — no Sparkle/Velopack updater wired yet. (worklist C / windows-ui-plan non-goal)
- ⚠️ **Donate to charity** — opens the donations page in the browser; the Mac in-app popup + `/summary`
  API is not ported. (worklist D1)
- ⚠️ **Send feedback** — a `mailto:` link, not the Mac's redacted-log feedback API with screenshot
  attachments. (worklist D3)
- ⚠️ **View log** — only surfaces `crash.log`; there's no general activity log to point it at, and
  logs write to `%LOCALAPPDATA%\Jot` regardless of the chosen save folder. (worklist D4, D5)
- 🚫 **"Jot for iPhone" share-sheet row** — macOS-only; dropped on Windows.
- ❌ **Help deep-links from Settings** — the Settings↔Help deep-link contract isn't wired. (worklist C)

## Platform integration & privacy

- ✅ **Launch at login** — per-user `HKCU\...\Run` registry entry, no admin needed.
- ✅ **Hide to tray on close; Quit exits** — the app boots into the tray (no `StartupUri`).
- ✅ **Single instance** (see above).
- ⚠️ **Permissions** — Windows needs only microphone privacy (no Input-Monitoring/Accessibility
  analog), so there are fewer gates; the wizard points at mic privacy settings.
- ✅ **Core transcription stays local** — Nemotron runs on-device; the only outbound calls are ones
  the user configures (an AI provider) or triggers (model download).
- ✅ **Optional AI is local or cloud** — Ollama is fully local; OpenAI/Anthropic/Gemini are opt-in and
  only contacted when configured and enabled.
- ✅ **No telemetry** — no analytics or crash pings.
- ✅ **Custom prompts stay local** — persisted to `prompts.json`; only cross the network when used.
- ✅ **Retention controls** — "Keep audio" prunes old audio; transcripts are kept.
- ⚠️ **All data in the chosen save folder** — recordings honor `DataDirectory`, but logs
  (`crash.log` / `dictation.log`) still write to `%LOCALAPPDATA%\Jot`. (worklist D5)
- 🚫 **Semantic-search model download** — N/A; semantic search isn't built (see Library).

---

### Backlog pointers

The full, prioritized todo list lives in `docs/plans/fixit-worklist.md` (Section A user bugs,
B hidden fakes, C Mac-parity gaps, D product backlog). This file tracks *status*; the worklist
tracks *work*.
