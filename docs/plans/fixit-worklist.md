# Jot Windows — Fix-it Worklist

**Related docs:** `../features.md` = the canonical Windows feature list + per-feature status;
**this worklist** = what's broken / the prioritized todo (items A–D, referenced by ID from
features.md); `windows-ui-plan.md` = the build plan / architecture. Keep the three in sync.

Compiled 2026-07-14 from (a) user hands-on testing, (b) a fresh "shown-but-broken" code audit, and
(c) a Mac↔Windows feature-parity gap analysis (source: `projects/JOT-Transcribe/docs/features.md`).
We work these **one at a time**; nothing here is fixed yet. `file:line` refs are from the audit and
may drift as we edit.

Legend — effort: S (≤1h) · M (half-day) · L (multi-session).

---

## ✅ Just shipped (2026-07-14)

> **Status convention (user, 2026-07-14):** items land as **DONE — awaiting review** first. They flip
> to user-confirmed only after the user tests and thumbs-up. Nothing is deleted even if it later turns
> out still-broken — reopen it instead.

- **Transcript scroll** — expanded pill transcript was clipped at `MaxHeight=140` with no scroll; wrapped
  in a themed `ScrollViewer` (auto-scrolls to newest). ✅ **User-confirmed working.**
- **Stop button** — added next to Copy in the expanded pill (neutral Fluent styling, right of Copy,
  visible only while recording; wired to stop+deliver). ✅ **User-confirmed working.**
- **Settings reorg (A3)** — **DONE — awaiting review.** Basic Settings now shows only Appearance,
  Microphone, Language + the **Show advanced features** toggle; everything else (launch-at-login, save
  location, keep-audio, return-to-origin, wizard, reset, Model, Processing, paste toggles, Sound, and the
  still-hidden AI/Vocabulary) is gated under that toggle. Verified by screenshot in both states.
- **Shortcuts → own left-nav page** — **DONE — awaiting review.** New `ShortcutsPage` (keyboard icon,
  between Recents and Help) shows Toggle + Cancel **read-only** with a "custom shortcuts coming soon"
  note; removed the Shortcuts section from Settings. No rebinding UI (A5 still broken). Verified by
  screenshot.
- **Pill stop/cancel key hint** — **DONE — awaiting review.** While recording, the pill shows a hint
  line under the waveform — e.g. `Alt + Space to stop   ·   Esc to cancel` — pulled live from the
  configured chords (`PillController.RecordingHints` → `PillWindow.SetKeyHints`). Verified by screenshot
  (`--pilldemo`). Note: Esc currently **discards** (D8 wants stop-and-save); hint says "cancel" honestly.

---

## A. User-reported — DO FIRST (all confirmed in code)

### A1. [~] Transcript text isn't selectable — DONE, awaiting review
**Done 2026-07-14:** the read-mode transcript and the rewrite Instruction/Original/Rewritten bodies are
now **read-only borderless transparent `TextBox`es** (`Style="{x:Null}"` to shed WPF-UI's input-box
look) — they read like body text but support drag-select + Ctrl+C. (`SelectableTextBlock` isn't in this
SDK's WPF, so used the TextBox approach.) Verified: renders as plain text (screenshot) and the transcript
is a **read-only Edit control** via UI Automation. Drag-select itself still wants a hands-on test.

### A2. [~] The "…" (overflow) button does nothing — FIX done (awaiting review); Find & Replace still TODO
- **Fix — DONE 2026-07-14:** left-click now opens the menu (`OnMoreClick` sets `ContextMenu.IsOpen` with
  `PlacementTarget` + `Placement=Bottom`). Verified by invoking the button via UI Automation — the menu
  (Export as WebVTT / Reveal in File Explorer / Delete) opened under it (screenshot).
- **Feature — DONE 2026-07-14, awaiting review:** Find & Replace bar over the transcript (opened from
  the … menu or **Ctrl+F**): Find + Replace fields, live match count, Match-case toggle, **Replace**
  (first) and **Replace all**. Operates on `Item.Transcript`; the store auto-persists. Verified
  end-to-end via UI Automation — replacing "content"→"SECTION" changed the saved transcript (then
  reverted so the sample recording is untouched).

### A3. [~] Simplify Settings layout — DONE, awaiting review
**Done 2026-07-14 (see "Just shipped"):** basic Settings = Appearance + Microphone + Language +
Advanced-features toggle; all else gated under Advanced. Shortcuts promoted out of Settings entirely into
its own left-nav page (better than "move the toggle up"). Verified by screenshot in both toggle states.
Original notes retained below for reference.
Current order (`SettingsPage.xaml`): Appearance → **Microphone(#2)** → Launch-at-login → Save-location →
Keep-audio → Return-to-origin(adv) → Advanced-toggle → Wizard → Reset → **Language(#10)** → Model →
Processing CPU/GPU → Live captions → Auto-paste → Enter-after-paste → Keep-clipboard → AI → Sounds →
**Shortcuts(#23, primary toggle near the very bottom)** → Vocabulary(adv).
- **Target (user):** at the very top, only **Microphone + Language** (theme can stay). Everything else
  moves down or under **Advanced**: Processing CPU/GPU, Live-captions-while-recording, Auto-paste, etc.
- **Promote the primary shortcut** (Toggle recording) up near the top-left — "no one scrolls to the
  bottom to find it."
- Note: shortcut **rebinding already exists** (`Controls\HotkeyBox`) — the user thought it didn't
  because it's buried; surfacing it near the top addresses "should be able to change it."

### A4. [ ] Rewrite / paste-last / rewrite-with-voice DON'T WORK on Windows 11 — HIGH (core AI feature)
**Reality (user-tested 2026-07-14): none of these work.** Rewrite still says "select text first";
paste-last doesn't paste; rewrite-with-voice does nothing.
- ❌ **My first attempt was WRONG** — I un-gated the hotkeys + polled the clipboard longer (~600ms) in
  `CaptureSelection`, then told the user it worked **without testing it**. It does not: the hotkey
  fires but the selection still comes back empty. (Lesson: never claim a Win11 input feature works
  without an end-to-end test — see [[claude-verify-before-claiming]].)
- **Root problem:** grabbing the selection via synthetic **Ctrl+C** (`SendInput`) and pasting via
  synthetic **Ctrl+V** is unreliable/broken on Windows 11 — most likely a focus / **UIPI** input-
  permission issue, not timing. Genuinely hard Win11 problem, not a one-liner.
- **Proper fix (needs real research; do NOT guess or claim success without an end-to-end test):** read
  the foreground selection via **UI Automation `TextPattern.GetSelection`** instead of synthetic copy;
  determine what Windows 11 requires to read another app's selection / inject input (UIPI / UIAccess /
  foreground rules). Test against a real app before saying it works.
- **Done 2026-07-14 (honesty, not a fix):** the three hotkeys are **not registered** and **hidden** from
  Settings (only Toggle + Cancel shown, read-only). The `CaptureSelection` clipboard-poll was kept
  (harmless, insufficient alone).

Original diagnosis (still accurate):
- **False negative (bug):** `TextInjector.CaptureSelection` (`TextInjector.cs:92-110`) clears the
  clipboard, sends synthetic Ctrl+C, then reads after a **fixed 120 ms sleep**. Slow apps
  (browsers/Electron/Office) don't finish the copy in time → empty read → treated as "nothing selected."
  Exceptions are swallowed too. **Fix:** poll the clipboard with retries + longer budget, and/or read
  the selection via **UI Automation `TextPattern`** instead of synthetic copy.
- **Not in Shortcuts:** Rewrite / Rewrite-with-voice / Paste-last are gated behind `AdvancedFeatures` in
  **two** places — the Settings UI (`SettingsPage.xaml:185-195`) **and** registration
  (`HotkeyManager.cs:39-44`). With Advanced off they're invisible AND unregistered. **Fix:** decide
  whether rewrite is a core (always-on) hotkey or keep it advanced but at least always *show* it.

---

### A5. [ ] Shortcut rebinding ("click to change") doesn't work — M
The `HotkeyBox` click-to-capture control does not actually rebind — pressing a new combination doesn't
stick. Shortcuts now live on their own **read-only** left-nav page (Toggle + Cancel) with a "custom
shortcuts coming soon" note — no rebinding UI is shown. Needs a real fix: verify the key-capture →
persist → re-register path and whether Win11 blocks the low-level capture. Until then, display-only.

### AI cleanup — UNVERIFIED (do NOT claim it works)
"Turn on cleanup" is wired (Gemini + key + enabled) but NOT confirmed working end-to-end. The stale
default `gemini-1.5-flash` was likely deprecated → silent 404 → raw transcript; D9 changed the default
to `gemini-3.1-flash-lite`, which *might* fix it — but this has NOT been tested against a live dictation.
Reliable alternative: point cleanup at local Ollama `gemma4:e4b` (installed + running) and actually verify.

---

## B. Other shown-but-broken (found in audit; user didn't flag these)

> **2026-07-14: all three HIDDEN (not removed) so users can't hit a dead feature.** Backing code is
> intact; each hidden spot has a comment pointing here. Un-hide when built for real.

- [ ] **B1. Vocabulary / custom terms is fake — L to build.** `SettingsPage.xaml` binds to an in-memory
  list seeded `["Jot","Nemotron","WASAPI"]` (`SettingsViewModel.cs:383`); never persisted, never read by
  any transcriber. ✅ **Hidden** (section `Visibility="Collapsed"`). To build: persist + bias the decoder
  (matches a real Mac feature).
- [ ] **B2. "Detect speakers" is a stub — L to build.** `RecordingDetailViewModel.cs:184-199` fakes
  speakers by alternating on sentence boundaries; exported VTT inherits the fake turns. ✅ **Hidden**
  (menu item `Visibility="Collapsed"`). To build: real on-device diarization.
- [ ] **B3. "Check for updates" is hardcoded — M to build.** Always says "up to date," no network check.
  ✅ **Hidden** in both the tray menu (`App.xaml.cs`) and About page (`AboutPage.xaml`). To build: tie
  into Velopack auto-update.

---

## C. Mac feature gaps (parity backlog — prioritize later)

### Core dictation
- Push-to-Talk (hold-to-record): `PushToTalkHotkey` field exists but no Settings row and no key-up
  support (needs `WH_KEYBOARD_LL` low-level hook). — M
- Silent-capture / mic-disconnect detection + mid-dictation salvage notices. — M
- "Never lose audio": startup scan that adopts orphaned audio files (Windows has pending rows +
  re-transcribe, but no orphan-adoption scan). — S

### AI / rewrite / prompts
- **Editable prompts** (Cleanup prompt + Rewrite invariants, with reset-to-default) — both hardcoded in
  `AiClient` today. — M
- Rewrite **intent classifier** (route voice-preserving / structural / translation / code). — M
- Prompt library depth: sample input/output, ✨Generate-sample, read-only detail sheet, provider/voice
  metadata (Windows has 15 prompts + pin/default/CRUD/search, no samples/detail). — M
- Ask Jot depth: doc-grounded answers, feature citations, in-chat voice input, streaming, ⌘K-style
  shortcuts (Windows routes to provider but grounding is one hardcoded string). — M

### Library / transcripts
- **AI semantic search** (on-device embeddings; on by default on Mac) — Windows is substring-only. — L
- Real speaker diarization (see B2). — L
- WebVTT export uses **fabricated** fixed 4s timestamps — needs real word timings. — S
- "Show original vs cleaned" toggle — Windows stores only one transcript. — S

### Settings & polish
- Custom Vocabulary made real (see B1). — L
- Per-field info popovers / "Learn more →" deep-links (Mac has info dots on every field). — S
- "Restart Jot" troubleshooting action. — S
- Auto-update (Velopack; see B3) + verify About donate/feedback flows are real not stubbed. — M

---

## D. Product backlog — deferred (future features, not bugs)

Captured 2026-07-14 from user testing. None are urgent; they're future work so nothing gets lost.

- [ ] **D1. Donate to charity → in-app popup + API.** Currently opens the browser
  (`Views\AboutPage.xaml.cs:17` → `jot-transcribe.com/donations/`). Want: an in-app popup that calls
  the donations API (the Mac app has it), showing charities + amounts. Framing: Jot is free — steer
  users to **donate to charity** rather than pay us. — M
- [ ] **D2. Analytics + "time saved."** Port the Mac app's analytics. Track usage locally and surface
  "how much time you've saved by dictating" — the hook that motivates the charity donation (D1). Keep it
  privacy-respecting / on-device. — M–L
- [ ] **D3. Send feedback → API, not email.** Currently a `mailto:` (`Views\AboutPage.xaml.cs:19-20`).
  Want: call a feedback API (same pattern as D1), no email client. — S–M
- [~] **D4. Real activity log + View Log — DONE, awaiting review.** New `Services\JotLog` is the single
  best-effort activity log (INFO/WARN/ERROR, ~2 MB roll + one backup). App startup, the full dictation
  trace (`RecorderController`), suppressed errors, and unhandled crashes (`App.LogCrash`) all funnel to
  it; About → View Log opens it. Verified: launching Jot writes `Jot starting` to the log.
- [~] **D5. All logs live in the chosen save folder — DONE, awaiting review.** `JotLog` writes to
  `<DataDir>\logs\jot.log` via `JotPaths.DataDir(settings)` (falls back to `%LOCALAPPDATA%\Jot\logs`
  before init). No more `crash.log`/`dictation.log` scattered in `%LOCALAPPDATA%`. Verified: the log
  landed at `D:\Jot\logs\jot.log` (this machine's data dir), not LocalAppData.
- [x] **D6. Save-location default — VERIFIED already correct (no action).** `JotPaths.DefaultDataDir`
  (`Services\JotPaths.cs:21`) auto-picks the roomiest **non-system** drive and falls back to
  `%LOCALAPPDATA%\Jot` on single-drive PCs — it is **not** hardcoded to `D:`. `D:\Jot` is just what it
  resolves to on this machine. The only real gap is D5 (logs bypass it).
- [ ] **D7. ARM64 support.** Verify the save-location logic (drive-based, so arch-independent) and,
  more importantly, that native deps (ONNX Runtime / DirectML / FFmpeg) have arm64 builds before
  shipping an ARM target. — L
- [~] **D8. Escape now STOPS-AND-SAVES (no more data loss) — DONE (part a), awaiting review.**
  **Done 2026-07-14:** the Esc hotkey (armed only while recording) is rerouted from `Cancel()` (discard)
  to `StopAndSaveFromHotkey()` → the normal `StopAndDeliverAsync` path, so a stray Esc — e.g. dismissing
  a dialog mid-dictation — saves the recording instead of losing it. Relabelled the Shortcuts row
  ("Stop & save recording … Never discards") and the pill hint ("Alt + Space or Esc to stop").
  `Cancel()`/discard kept as an unwired API for a future explicit discard affordance.
  **Still TODO (part b, harder):** Esc is still a *global* hotkey while recording, so it's swallowed
  system-wide during a dictation (a dialog's own Esc won't reach it, and recording ends early — but now
  safely saved). True fix = dynamic/contextual Esc (low-level hook that passes Esc through) instead of a
  global `RegisterHotKey`; plus verify WASAPI survives a UAC secure-desktop switch. Behavior needs a
  hands-on mic+Esc test (can't be driven headlessly).
  Original diagnosis below.
- [ ] ~~D8 original~~ — A system dialog mid-recording cancels the dictation — HIGH (data loss). Root cause
  (traced): cancel is a **global** `RegisterHotKey` for **bare Escape** (`Recording\RecorderController.cs:209-221`;
  `CancelRecordingHotkey` default `Esc`), registered system-wide while recording. **Any** Escape
  keypress anywhere — including dismissing a Windows UAC / permission / app popup that appears
  mid-dictation — fires `Cancel()` and discards the recording.
  **Target behavior (user, 2026-07-14):** Escape should **stop-and-SAVE** the in-progress recording into
  the library so it's viewable/recoverable later — **not discard**. (Windows currently discards; the Mac
  `features.md` says "discard," but the user wants save — matches the crash-recovery / never-lose-audio
  goal.) Fix properly: (a) change cancel semantics from discard → **stop-and-save** (reuse the normal
  stop→transcribe→save path, or persist audio as a pending/recoverable row); (b) stop stealing Escape
  globally — mirror the Mac app's **"dynamic Escape"** routing (claim Esc contextually, not a system-wide
  `RegisterHotKey`), or drop global Esc and use the pill's **Stop** button / a modifier chord. Secondary:
  verify WASAPI capture survives a UAC secure-desktop switch. Unifies with crash-recovery. — M
- [ ] **D9. Refresh AI provider model defaults.** `Services\Ai\AiDefaults.cs` ships stale models
  (`gpt-4o-mini` / `claude-3-5-haiku-latest` / `gemini-1.5-flash` / `llama3.2`). Anthropic →
  `claude-haiku-4-5` (confirmed current Haiku tier). Still need current OpenAI (GPT-5.x), Gemini (3.x
  flash/lite), and a modern small Ollama model (~2–4B, e.g. gemma) IDs before wiring. — S
- [ ] **D10. Remove macOS "Apple Intelligence" references.** No Apple Intelligence on Windows; drop the
  mentions in `SettingsPage.xaml:111` (InfoBar) and `HelpPage.xaml:39`. Keep "Bring your own provider." — S

- [~] **D11. API key persistence — DONE, awaiting review (persistence was already implemented).**
  Turns out `AiCredentials` already saves the key DPAPI-encrypted to `aikey.dat` and **loads it in its
  constructor** (a startup singleton), and `SettingsViewModel` seeds `_aiApiKey = credentials.ApiKey` at
  startup — so the key already survives restarts. The bug was the **stale UI/text**: the API-key row said
  "Stored only for this session" and the code comments said "session-only, never persisted". Fixed those
  to state it's saved encrypted, and added a "A key is already saved — type a new one to replace it" hint
  (the WPF PasswordBox is blank on load for security, which looked like the key was lost). Verified the
  DPAPI save→load round-trip works on this machine. (AI UI still hidden; correct for when it's revived.)

> **2026-07-14: AI turned OFF in the UI.** The whole **AI Settings section**, the **Ask Jot** nav item,
> and the **Prompts** nav item are hidden — AI (cleanup/rewrite/Ask Jot) isn't usable yet, and prompts
> do little without AI. Un-hide per-feature as each is built properly. Backing code is untouched.

## Cross-cutting — do it the right way (no shortcuts)

Once the basics above are solid, do a **hardening / cleanup pass** before adding more features — the
user's explicit direction: don't build on a shaky foundation. Scope: consolidate the record→
transcribe→deliver pipeline's error handling, remove dev hooks/dead fields, ensure every write goes
through `JotPaths` (D5), make "recording survives interruptions" a first-class invariant (crash
recovery + D8 + WASAPI resilience), and add real logging (D4) so failures are diagnosable. Treat this
as a milestone, not a side quest.

**Principle (user, 2026-07-14):** never surface a component/control unless its whole feature actually
works. A feature is the unit — if it isn't ready, hide the entire thing. No dead buttons, no toggles
that no-op, no shortcuts that don't fire, no config for a pipeline that fails. Wire a component in only
as part of a working feature.

## Suggested order
1. **A1** (selectable text, S) → **A2 fix** (…-button opens menu, S) → **A3** (Settings reorg, M) →
   **A4** (rewrite hotkey ungate + selection-capture reliability, M).
2. **A2 feature** (Find & Replace, M).
3. **B1/B2/B3** — decide hide-vs-build for each fake surface.
4. **C** parity backlog by leverage: editable prompts, show-original, PTT, then the L items.
