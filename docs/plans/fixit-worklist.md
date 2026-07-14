# Jot Windows — Fix-it Worklist

Compiled 2026-07-14 from (a) user hands-on testing, (b) a fresh "shown-but-broken" code audit, and
(c) a Mac↔Windows feature-parity gap analysis (source: `projects/JOT-Transcribe/docs/features.md`).
We work these **one at a time**; nothing here is fixed yet. `file:line` refs are from the audit and
may drift as we edit.

Legend — effort: S (≤1h) · M (half-day) · L (multi-session).

---

## ✅ Just shipped (2026-07-14)
- **Transcript scroll** — expanded pill transcript was clipped at `MaxHeight=140` with no scroll; wrapped
  in a themed `ScrollViewer` (auto-scrolls to newest). User-confirmed working.
- **Stop button** — added next to Copy in the expanded pill (neutral Fluent styling, right of Copy,
  visible only while recording; wired to stop+deliver). Awaiting user's visual confirmation.

---

## A. User-reported — DO FIRST (all confirmed in code)

### A1. [ ] Transcript text isn't selectable — S
- **Now:** read-mode transcript is a plain `TextBlock` (`Views\RecordingDetailPage.xaml:163`); WPF
  `TextBlock` can't drag-select. Copy works only because it goes through the VM. Same problem on the
  rewrite view's Instruction/Original/Rewritten bodies (`:81,86,92`).
- **Fix:** swap to a read-only borderless `TextBox` (`IsReadOnly=True`, `BorderThickness=0`,
  `Background=Transparent`) or a `SelectableTextBlock`. Keep Edit mode as-is (it already uses a real
  `ui:TextBox` at `:167`).

### A2. [ ] The "…" (overflow) button does nothing — S (fix) + M (feature)
- **Now:** the `MoreHorizontal` button (`RecordingDetailPage.xaml:42-52`) has only a `ContextMenu` and
  **no left-click handler**, so left-click is a no-op (the menu only opens on right-click). The menu's
  items (Detect speakers / Export VTT / Reveal / Delete) are themselves wired and work.
- **Fix (S):** left-click should open the menu — `btn.ContextMenu.IsOpen = true` with `PlacementTarget`.
- **Feature (M):** add **Find & Replace** into that menu (user's request) — a small find/replace bar over
  the transcript, operating on `Item.Transcript`, with replace/replace-all and match count.

### A3. [ ] Simplify Settings layout — M
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

### A4. [ ] AI rewrite: "Select some text first" fires even when text IS selected + not shown in Shortcuts — M
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
- [ ] **D4. View log is empty — add real logging.** `OnViewLog` only checks
  `%LOCALAPPDATA%\Jot\crash.log` (`Views\AboutPage.xaml.cs:22-31`) and ignores the existing
  `dictation.log`; there's no general activity log. Want: real activity logging + point View Log at it. — S–M
- [ ] **D5. All logs/data must live in the chosen save folder.** `crash.log` / `dictation.log` write to
  `%LOCALAPPDATA%\Jot` regardless of the user's `DataDirectory`. Route logs (and any stray writes)
  through `JotPaths.DataDir(settings)` so nothing lands randomly. — S
- [x] **D6. Save-location default — VERIFIED already correct (no action).** `JotPaths.DefaultDataDir`
  (`Services\JotPaths.cs:21`) auto-picks the roomiest **non-system** drive and falls back to
  `%LOCALAPPDATA%\Jot` on single-drive PCs — it is **not** hardcoded to `D:`. `D:\Jot` is just what it
  resolves to on this machine. The only real gap is D5 (logs bypass it).
- [ ] **D7. ARM64 support.** Verify the save-location logic (drive-based, so arch-independent) and,
  more importantly, that native deps (ONNX Runtime / DirectML / FFmpeg) have arm64 builds before
  shipping an ARM target. — L
- [ ] **D8. A system dialog mid-recording cancels the dictation — HIGH (data loss).** Root cause
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

## Cross-cutting — do it the right way (no shortcuts)

Once the basics above are solid, do a **hardening / cleanup pass** before adding more features — the
user's explicit direction: don't build on a shaky foundation. Scope: consolidate the record→
transcribe→deliver pipeline's error handling, remove dev hooks/dead fields, ensure every write goes
through `JotPaths` (D5), make "recording survives interruptions" a first-class invariant (crash
recovery + D8 + WASAPI resilience), and add real logging (D4) so failures are diagnosable. Treat this
as a milestone, not a side quest.

## Suggested order
1. **A1** (selectable text, S) → **A2 fix** (…-button opens menu, S) → **A3** (Settings reorg, M) →
   **A4** (rewrite hotkey ungate + selection-capture reliability, M).
2. **A2 feature** (Find & Replace, M).
3. **B1/B2/B3** — decide hide-vs-build for each fake surface.
4. **C** parity backlog by leverage: editable prompts, show-original, PTT, then the L items.
