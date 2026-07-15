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

### A4. [~] Rewrite / paste-last / rewrite-with-voice — REAL BUG FOUND AND FIXED 2026-07-14, needs the
user's own hands-on retest before this can be called done
**Given this item's history — a previous claim of "fixed" here was wrong and cost trust (see
[[claude-verify-before-claiming]]) — this is deliberately NOT marked done, even though the fix is real
this time.** Timeline of today's session, in full, so nothing gets glossed over:

1. First re-enabled the hotkeys off `--rewriteselftest` passing against an in-process target — the user
   tested Alt+/ against real Notepad and it **still failed** ("no text selected"). The earlier test
   result was real but didn't generalize; re-opened the investigation instead of re-asserting it worked.
2. Built `--notepadselftest` to drive REAL `notepad.exe` (Windows 11's packaged/MSIX Notepad — a
   genuinely separate process). Chased two real environmental red herrings first: (a) Notepad is
   single-instance and **tabbed**, and every test run left a leftover tab; UIA reads were picking up a
   **stale background tab** (from an earlier run where `SendUnicodeText`-based typing had genuinely
   corrupted the content — a separate, unresolved minor bug, not investigated further since it's not on
   the critical path). (b) Notepad **persists tabs/session to disk**
   (`%LOCALAPPDATA%\Packages\...WindowsNotepad...\LocalState\TabState`) and restores them across
   launches regardless of process kill — deliberately NOT wiped (that's the user's real Notepad history);
   worked around by explicitly selecting our tab and focusing the document element via UI Automation
   instead.
3. With a clean, correctly-focused single tab, found the REAL bug: `TextInjector.CaptureSelection`'s
   `ReleaseModifiers()` sends a *synthetic* key-up for Alt, but a real, physically-still-held Alt (the
   normal case — `WM_HOTKEY` fires the instant the combo completes, before the user has necessarily
   released either key) is NOT reliably cleared by that alone. Empirically confirmed with a dedicated
   test (`--notepadselftest` step E: hold Alt down via `SendScanKeyDown`, call the real
   `CaptureSelection` — FAILS, reproducing the user's exact symptom; every step that never touched Alt
   passed cleanly).
4. First fix attempt (`TextInjector.WaitForRealModifiersReleased`, polling `GetAsyncKeyState`) didn't
   fully explain the mechanism on its own — diagnostic logging showed Alt reads as already "up" (via
   `GetAsyncKeyState` and WPF's own `Keyboard.Modifiers`) by the time `SendKeyChord(Ctrl, C)` runs, yet
   capture still failed in one test variant. The piece that actually made it reliably pass: re-selecting
   the text **immediately** before the real (Alt-held) capture attempt, matching real usage (select →
   immediately press the hotkey) rather than the test's earlier order (select once, do an unrelated
   isolation check, *then* attempt capture minutes of test-time later). **Honest residual uncertainty:**
   the exact mechanism may be "a bare Alt tap disturbs the target's text selection" rather than purely
   "stuck modifier state" — both `WaitForRealModifiersReleased` (kept, it's cheap and correct regardless)
   and realistic immediate-selection timing contributed to the fix; not claiming perfect mechanistic
   certainty, only a **reproducible pass** (3/3 runs) against the realistic scenario (select text, hold
   Alt ~100ms as in a normal fast tap, release, capture).
5. Result: `--rewriteselftest` now passes end-to-end **including the real Alt-held timing case** —
   selection captured, AI rewrite correct, paste-back landed, library row saved. 3 consecutive PASSes.
6. Rebuilt (`dotnet publish -c Release`) and redeployed to the real running install
   (`%LOCALAPPDATA%\Programs\Jot`) — confirmed via `--hotkeytest` that the live instance owns
   PasteLast/Rewrite/RewriteWithVoice (Win32 error 1409 "already taken" from a second process trying the
   same chords).

**What's still NOT verified: the user's own hands, on real Notepad, again, after this fix.** That's the
step that actually failed last time, and it's the only thing that gets to close this out. Still hidden
from the Shortcuts page (only Toggle + Cancel shown) — the hotkeys now fire with their default chords
but aren't yet visible/rebindable there.

Reality as of the *previous* audit (now superseded by the above, kept for context — user-tested
2026-07-14): none of these worked. Rewrite still said "select text first"; paste-last didn't paste;
rewrite-with-voice did nothing.
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

### A5. [~] Shortcut rebinding — FIXED the click bug, awaiting hands-on review
**Root cause found & fixed 2026-07-14 (user reported "clicking does nothing").** The click handler
called `Focus()`, which only sets *logical* focus within the NavigationView page's focus scope — not
keyboard focus — so `GotKeyboardFocus` never fired and capture never started. Changed to
`Keyboard.Focus(this)` on `PreviewMouseLeftButtonDown`. Verified headlessly: simulating a click now
focuses the box and a following keypress is captured (`--hotkeyboxtest` → PASS, `focusedAfterClick=HotkeyBox`,
`after=[J]`). Physical click+rebind still wants a hands-on confirm. Original investigation below.

**Investigated 2026-07-14.** The `HotkeyBox` capture logic is actually **correct**: a `--hotkeyboxtest`
harness that focuses a real `HotkeyBox` and raises a routed `PreviewKeyDown` shows the box captures the
key and updates its two-way `Chord` DP (PASS; `after=[J]`). The earlier "doesn't work" could not be
reproduced — synthetic OS key input can't be routed to a non-foreground window in this headless env
(`foregroundMatch=False`, `rawKeysSeenByBox=0`), which is why physical-input testing failed here, not a
control bug. So **re-enabled rebinding**: the Shortcuts page now uses editable `HotkeyBox`es (Toggle +
Stop) bound TwoWay to the VM (persists + App re-registers on change). **Still needs the user's physical
keyboard test** — I can't drive real OS key input headlessly. If it doesn't stick when tested, reopen
(check click-to-focus in the NavigationView page context, and that the new chord actually re-registers).

### AI cleanup — VERIFIED BROKEN for slow/local models (2026-07-14 dev-hook test, real bug found)
Tested for real via a new `--cleanuptest` dev hook (App.xaml.cs) that calls the actual
`AiClient.CleanupAsync` (production code, including the faithfulness guard) against local Ollama
`gemma4:e4b` on a filler-laden sample transcript. **Result: FAIL, reproducibly.** Root cause confirmed
by an independent raw `POST /api/chat` call: `gemma4:e4b` on this box (partial CPU offload, see
[[jot-local-ai-eval]]) takes **~80 seconds** to answer, but `AiClient`'s shared `HttpClient` has a
**hardcoded 30-second `Timeout`**. The call always times out, the exception is swallowed by
`CleanupAsync`'s `catch { return transcript; }` (by design — cleanup must never lose the transcript),
and the ORIGINAL unclean text is silently returned every time — no error surfaced anywhere. The raw
API call confirms the prompt/model itself IS faithful when given enough time (fillers removed, all
content/dates preserved, output matched exactly). **Fix needed:** either raise `AiClient`'s HTTP
timeout (cleanup already has its own 30s `CancellationTokenSource` at the call site in
`RecorderController.MaybeCleanupAsync` — that's the one to extend, since it's tighter than a slow
local model needs) or default Ollama cleanup to a faster model. Gemini path (the actual configured
default) still separately unverified against a live key — this only confirms/roots the Ollama case.

### AI Rewrite (replace selected text) — mechanics VERIFIED WORKING end-to-end (2026-07-14, reverses A4's premise)
A4 disabled the Rewrite/PasteLast/RewriteWithVoice hotkeys on the belief that selection capture
(synthetic Ctrl+C) + paste-back is fundamentally broken on Windows 11. Built `--rewriteselftest` (App.xaml.cs)
to test the REAL, unmodified pipeline (`RewriteController.CaptureContext` → `AiClient.RewriteAsync` →
`TextInjector.PasteAtCursor`) against a target text window running on its **own thread with its own
message pump** (not the caller's thread — see method doc on `RewriteTestTarget`), using local Ollama
`qwen3:4b-instruct` (fast enough to stay under the 30s HTTP timeout). **Result: PASS, twice in a row** —
selection captured (60/60 chars), AI rewrite came back faithful ("um so yeah i think we should uh ship
this on friday you know" → "So, yeah, I think we should ship this on Friday."), pasted back exactly
matching the AI output, and a `Kind=Rewrite` row was saved. **Important caveat (be honest, don't
over-claim):** the target here is a separate *thread* in the same process, not a separate OS *process*
like a real foreign app (Notepad/browser/Office) — chosen deliberately because an EARLIER same-thread
self-test attempt gave a false-negative FAIL: `TextInjector.CaptureSelection` blocks the calling thread
with `Thread.Sleep` while polling, which — when the "target" shared that same thread/dispatcher — also
froze the target's own message pump and starved it of the Ctrl+C we'd just sent. A genuinely separate
thread (or process) doesn't have that problem, since its message pump runs independently. This result
is strong evidence the mechanism itself is sound, but a hands-on test against a real foreign app
(Notepad, browser, Office) is still the gold-standard confirmation before re-enabling the hotkeys in
`HotkeyManager`/Settings — the original A4 diagnosis may simply have been the same same-thread
test artifact, not a real Windows 11 selection-capture limitation.

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

- [~] **B3b. Uninstall via "Add or remove programs" + wipe data — hook DONE, needs packaging + hands-on.**
  (User request 2026-07-14: users should remove Jot the Windows way, and it should delete all data.)
  **Done:** `VelopackApp.Build().OnBeforeUninstallFastCallback(_ => WipeAllData()).Run()` now runs first in
  `App.OnStartup`; `WipeAllData` removes the launch-at-login Run key + Jot's data (recordings, models,
  logs, library.json, aikey.dat, stats.json; and `%LOCALAPPDATA%\Jot`), targeting known artifacts so a
  shared custom save folder isn't blindly nuked. Verified: normal launch still works with Velopack wired
  (no regression). **Remaining (packaging, not code):** the **Add/Remove Programs entry only exists once
  Jot is installed via the Velopack `Setup.exe`** — the current dev build runs from `bin`, so it won't
  appear there yet. Build the installer with `vpk` (`dotnet publish -c Release -r win-x64` → `vpk pack`),
  install it, then the uninstall→wipe path is real. End-to-end uninstall NOT yet verified (needs a real
  install). The `WipeAllData` deletion logic itself is unit-testable but only fires from the Velopack hook.

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
- [~] "Restart Jot" troubleshooting action — **DONE, awaiting review** (About → Troubleshooting → Restart Jot). — S
- Auto-update (Velopack; see B3) + verify About donate/feedback flows are real not stubbed. — M

---

## D. Product backlog — deferred (future features, not bugs)

Captured 2026-07-14 from user testing. None are urgent; they're future work so nothing gets lost.

- [~] **D1. Donate to charity → in-app popup + API — DONE, awaiting review.** New `DonationsWindow`
  (opened from About → Donate) fetches the live `GET https://jot-donations.ideaflow.page/summary`
  (public, read-only, no auth/PII — same endpoint the Mac/iOS apps use) via `Services\DonationsService`
  and lists charities (biggest supporters first) with per-charity **Donate** buttons that open the
  Every.org fundraiser; a header shows total raised. Framing kept: Jot is free, donate to charity.
  **Verified end-to-end via `--donatedemo`**: shows "$40 raised … across 3 donations" with PCRF / Foster
  Love / Room to Read etc. from live data. (The actual donating happens on Every.org, so Jot never
  touches money/PII.)
- [~] **D2. Analytics + "time saved" — DONE, awaiting review.** New `Services\UsageStats` (on-device,
  `<DataDir>\stats.json`, never sent anywhere) counts total dictations/words/seconds; `RecorderController`
  records each completed dictation. About shows a **"Your impact"** card — words, recordings, and estimated
  minutes saved vs typing at ~40 wpm — tied to the donate ask (D1). Verified by screenshot with seeded
  stats (3,200 words / 12 recs / 900 s → 65 min saved; math correct). Real accrual needs live dictations.
- [~] **D3. Send feedback → API, not email — DONE, awaiting review.** New `FeedbackWindow` (About →
  Send feedback) posts to `POST https://jot-donations.ideaflow.page/feedback` via
  `Services\FeedbackClient`, mirroring the Mac wire format exactly (`{platform:"windows", version,
  message}` → `{status, id | error}`); surfaces the server's own success/rate-limit/error text inline.
  No more `mailto:`. UI verified by screenshot; the client matches the Mac `FeedbackClient` 1:1.
  **Not auto-sent** (would put a test entry in the real feedback inbox) — a one-click user test confirms
  the live POST.
- [~] **D4. Real activity log + View Log — DONE, awaiting review.** New `Services\JotLog` is the single
  best-effort activity log (INFO/WARN/ERROR, ~2 MB roll + one backup). App startup, the full dictation
  trace (`RecorderController`), suppressed errors, and unhandled crashes (`App.LogCrash`) all funnel to
  it; About → View Log opens it. Verified: launching Jot writes `Jot starting` to the log.
- [~] **D5. All logs/data live in the chosen save folder — DONE, awaiting review.** `JotLog` writes to
  `<DataDir>\logs\jot.log` and `aikey.dat` now lives at `<DataDir>\aikey.dat` (both via
  `JotPaths.DataDir`). Verified: the log landed at `D:\Jot\logs\jot.log`, not LocalAppData.
  **Honest caveat (from adversarial review):** an unhandled crash during the first moments of startup —
  *before* `JotLog.Initialize` runs (Services aren't built yet, so the data dir isn't known) — falls back
  to `%LOCALAPPDATA%\Jot\logs`. That's the only remaining case that can write to LocalAppData; everything
  after startup goes to the data folder. `settings.json` intentionally stays in `%LOCALAPPDATA%` (small
  app config, must be found before the data dir is known).
- [x] **D6. Save-location default — VERIFIED already correct (no action).** `JotPaths.DefaultDataDir`
  (`Services\JotPaths.cs:21`) auto-picks the roomiest **non-system** drive and falls back to
  `%LOCALAPPDATA%\Jot` on single-drive PCs — it is **not** hardcoded to `D:`. `D:\Jot` is just what it
  resolves to on this machine. The only real gap is D5 (logs bypass it).
- [ ] **D12. Offer NumLock / CapsLock / F13+ / the Menu(Apps) key as toggle-hotkey options — RESEARCHED
  2026-07-14, empirically verified, not yet built.** User wants single "special" keys (no modifier held)
  as options for the toggle-recording hotkey. Findings from a new `--specialkeytest` dev hook
  (registers each bare via `RegisterHotKey`, synthesizes a press, checks `GetKeyState` before/after):
  - **`HotkeyBox`/`HotkeyChord` already support this with ZERO code changes** — the chord parser/capture
    (`HotkeyChord.TryParse`, `HotkeyBox.OnPreviewKeyDown`) is generic over any WPF `Key`, including
    `NumLock`/`CapsLock`/`Apps`/`F13`–`F24`, and `RegisterHotKey` already accepts bare (no-modifier) keys
    (`CancelRecordingHotkey` defaults to bare `Escape` today). So the answer to "select one of these, or
    a full combination" is **both, for free** — the existing capture box lets the user press any single
    key alone (these included) OR a modifier combo; no separate preset-picker control is technically
    required, though a short "Tip: NumLock, the Menu key, or F13+ work well as a single tap" hint in the
    UI would help users discover it.
  - **Apps (Menu/"right-click") key** — clean. Registers, fires, no side effects. Best safe option.
  - **F13** — clean, same as Apps. Registers, fires, no conflicts. **Caveat: most consumer keyboards
    have no physical F13–F24 key** (present on some mechanical/programmable boards, or reachable via
    software remap e.g. PowerToys Keyboard Manager) — fine to offer, but won't be usable out of the box
    for most users.
  - **F1 (representative of F1–F12)** — registers fine too, but while registered **F1 stops reaching
    every other app system-wide** (browser Help, etc.) — `RegisterHotKey` steals the key wholesale, not
    just when Jot has focus. Usable but a real conflict-risk; if offered, warn the user.
  - **NumLock / CapsLock / Scroll Lock — CONFIRMED real gotcha: registering any of the three does NOT
    stop the OS from ALSO toggling its LED/state.** Empirically verified: `hotkeyFired=True` **and**
    `UNWANTED_TOGGLE_SIDE_EFFECT=True` for all three (added Scroll Lock 2026-07-14, same result as
    NumLock/CapsLock). Each dictation toggle would also flip whether the numpad emits numbers vs.
    navigation, or flip text case, or flip Scroll Lock — but **practical severity differs a lot**:
    NumLock/CapsLock toggling is genuinely disruptive (breaks numpad typing / flips case); **Scroll
    Lock does essentially nothing in almost any modern app**, so it's the one of the three that's
    reasonably safe to ship even WITHOUT the fix below — the other two really do want it. Two fix
    options if wanted: **(a) compensate** — immediately re-synthesize the same key
    (`TextInjector.SendVirtualKeyPress`, added for this test) right after handling the hotkey, to flip
    the LED back to where it was; simple, reuses code already written for the test, tiny visible LED
    flicker. **(b) suppress properly** — a `WH_KEYBOARD_LL` low-level keyboard hook that swallows the
    key before Windows' own toggle handling runs (how AutoHotkey/PowerToys remap CapsLock cleanly); no
    flicker, but Jot has no low-level hook today — bigger, separate piece of infrastructure (would also
    be reusable for D8's "true" contextual-Escape fix).
  **Decide:** which keys to actually surface (Apps + F13 are risk-free; NumLock/CapsLock need the
  compensate-or-hook decision above; F1–F12 need a conflict warning) and whether to add a short
  discovery hint near the `HotkeyBox` rows. — S (Apps/F13, already works) / S (NumLock/CapsLock via
  compensate) / M (NumLock/CapsLock via low-level hook, shared with D8b)

  **Update 2026-07-14 (user's follow-up, changes the recommended approach):** user asked for a
  **passive, non-consuming listener** instead — detect the press but let the key's normal OS behavior
  keep happening untouched (F1 still reaches the foreground app, NumLock still toggles as it always
  has), rather than `RegisterHotKey`'s exclusive grab. Built `--passivehooktest` (`App.xaml.cs`) using a
  `WH_KEYBOARD_LL` low-level keyboard hook that **always calls `CallNextHookEx`** (never swallows) and
  just watches for the target VK codes. **Result: PASS, twice in a row.** Against a real focused target
  window (the same separate-thread `RewriteTestTarget` used for the rewrite test): F1 was detected by
  the hook AND still delivered a normal KeyDown to the focused window; NumLock was detected by the hook
  AND its LED still toggled exactly as it always does. This is a strictly better fit than
  `RegisterHotKey` for every key in this list:
  - **F1–F12**: no longer steals the key from other apps at all — solves that conflict outright.
  - **Apps/Menu key**: the context menu would still open normally alongside toggling dictation.
  - **NumLock/CapsLock/ScrollLock**: the LED toggling is no longer an "unwanted side effect" to fight —
    it's just the key doing what it's always done, same as if Jot didn't exist, plus dictation toggles
    as a bonus. **The compensate/suppress work above becomes unnecessary if this approach is adopted** —
    one mechanism handles all three toggle keys with zero special-casing.
  **Trade-offs to weigh before building this into production:** (1) new infrastructure — a
  `WH_KEYBOARD_LL`-based listener to sit alongside (not replace) `GlobalHotkey`'s `RegisterHotKey` path;
  natural split is combo chords (Alt+Space) stay on `RegisterHotKey`, bare special keys use the passive
  hook. (2) `WH_KEYBOARD_LL` fires on every keystroke system-wide (filtered in-process to the VK codes
  we care about, nothing logged/stored) — the same mechanism keylogging tools use, so some antivirus/
  endpoint software may flag a raw keyboard hook with more scrutiny than `RegisterHotKey`, though this
  is how AutoHotkey/PowerToys/most remap tools already work. (3) Needs real engineering care: dedupe
  key-repeat manually (unlike `RegisterHotKey`'s built-in `NoRepeat` flag, `WH_KEYBOARD_LL` fires
  repeatedly while a key is held), and keep the hook callback fast — Windows silently unhooks a slow
  one (~a few hundred ms budget), so the actual toggle-recording call must be dispatched off the hook
  callback rather than run inline. **Not yet wired into `HotkeyManager`** — proven in isolation only;
  next step is deciding whether to build it in.

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
