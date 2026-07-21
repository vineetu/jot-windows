# Jot for Windows — Feature Inventory

> **Draft — initial agent-generated bootstrap, to be refined by the maintainer.** It was written by
> reading the source; treat it as a starting skeleton, correct anything that looks off, and keep it
> current from here.

> **Scope**: user-facing features only, grouped by surface. No file paths, class names, or framework
> primitives in the body — those live in [`ARCHITECTURE.md`](../ARCHITECTURE.md). Exception:
> user-visible labels the app shows on screen (e.g. the "Nemotron" model tag, "Alt + Space") are fine.
> **Cross-links**: related features link to each other by anchor, in both directions.
> **Caveats are deliberate**: several surfaces advertise a control whose behavior isn't wired yet, and a
> few features are built but held back behind a "hidden until real" gate. Those are documented as
> **advertised-but-not-yet-wired** / **built-but-hidden** notes, not omitted and not overstated. Do not
> "clean them up" — fix the underlying code if you want the caveat gone.

**What Jot for Windows is**: press a global hotkey, speak, and the transcript is pasted at the cursor in
whatever app is focused. Speech-to-text runs entirely on-device. Optional AI cleanup/rewrite is
bring-your-own-provider. No accounts, no telemetry.

---

## Table of Contents

- [1. Setup & First Run](#1-setup--first-run)
  - [1.1 First-Run Wizard](#11-first-run-wizard)
  - [1.2 Microphone Permission Step](#12-microphone-permission-step)
  - [1.3 Re-runnable Setup](#13-re-runnable-setup)
- [2. Dictation & Recording](#2-dictation--recording)
  - [2.1 Toggle Recording](#21-toggle-recording)
  - [2.2 Any-Length Recordings](#22-any-length-recordings)
  - [2.3 Stop & Save with Esc](#23-stop--save-with-esc)
  - [2.4 Microphone Selection](#24-microphone-selection)
  - [2.5 Never Lose Audio](#25-never-lose-audio)
  - [2.6 Per-Event Sounds](#26-per-event-sounds)
- [3. Transcription Engine (On-Device)](#3-transcription-engine-on-device)
  - [3.1 On-Device Only](#31-on-device-only)
  - [3.2 Streaming Transcription](#32-streaming-transcription)
  - [3.3 One Multilingual Model](#33-one-multilingual-model)
  - [3.4 In-App Model Download](#34-in-app-model-download)
  - [3.5 CPU / GPU Processing](#35-cpu--gpu-processing)
  - [3.6 Deterministic On-Device Cleanup](#36-deterministic-on-device-cleanup)
- [4. Status Pill & Live Captions](#4-status-pill--live-captions)
  - [4.1 Floating Status Pill](#41-floating-status-pill)
  - [4.2 Live Amplitude Waveform](#42-live-amplitude-waveform)
  - [4.3 Live Captions & Expand](#43-live-captions--expand)
  - [4.4 Pill Key Hints](#44-pill-key-hints)
  - [4.5 Pill State Coverage](#45-pill-state-coverage)
- [5. Output — Paste & Clipboard](#5-output--paste--clipboard)
  - [5.1 Auto-Paste at Cursor](#51-auto-paste-at-cursor)
  - [5.2 Press Enter After Paste](#52-press-enter-after-paste)
  - [5.3 Return to the App I Started In](#53-return-to-the-app-i-started-in)
  - [5.4 Clipboard Preservation](#54-clipboard-preservation)
  - [5.5 Copy / Paste Last Transcription](#55-copy--paste-last-transcription)
- [6. Recents Library & Transcript Detail](#6-recents-library--transcript-detail)
  - [6.1 Single Library Surface](#61-single-library-surface)
  - [6.2 Date-Grouped, Virtualized List](#62-date-grouped-virtualized-list)
  - [6.3 Search & Tag Filters](#63-search--tag-filters)
  - [6.4 Recording Detail & Playback](#64-recording-detail--playback)
  - [6.5 Editable Transcripts](#65-editable-transcripts)
  - [6.6 Find & Replace](#66-find--replace)
  - [6.7 Import an Audio or Video File](#67-import-an-audio-or-video-file)
  - [6.8 Export as WebVTT](#68-export-as-webvtt)
  - [6.9 Rewrite Session Detail](#69-rewrite-session-detail)
- [7. AI: Cleanup, Rewrite & Prompt Library](#7-ai-cleanup-rewrite--prompt-library)
  - [7.1 Bring Your Own Provider](#71-bring-your-own-provider)
  - [7.2 Transcript Cleanup](#72-transcript-cleanup)
  - [7.3 Test Connection](#73-test-connection)
  - [7.4 Rewrite & Rewrite with Voice](#74-rewrite--rewrite-with-voice)
  - [7.5 Prompt Library](#75-prompt-library)
  - [7.6 Prompt Picker Overlay](#76-prompt-picker-overlay)
- [8. Ask Jot](#8-ask-jot)
- [9. Settings](#9-settings)
  - [9.1 Basic vs Advanced](#91-basic-vs-advanced)
  - [9.2 Show Advanced Features](#92-show-advanced-features)
  - [9.3 Data Location & Retention](#93-data-location--retention)
  - [9.4 Reset & Erase](#94-reset--erase)
  - [9.5 Custom Vocabulary](#95-custom-vocabulary)
- [10. Global Shortcuts, Tray & Window](#10-global-shortcuts-tray--window)
  - [10.1 Global Hotkeys & Rebinding](#101-global-hotkeys--rebinding)
  - [10.2 System Tray](#102-system-tray)
  - [10.3 Main Window & Navigation](#103-main-window--navigation)
  - [10.4 Launch at Login & Single Instance](#104-launch-at-login--single-instance)
- [11. About, Feedback & Donations](#11-about-feedback--donations)
  - [11.1 About & Help](#111-about--help)
  - [11.2 Usage Impact](#112-usage-impact)
  - [11.3 Donate to Charity](#113-donate-to-charity)
  - [11.4 Send Feedback](#114-send-feedback)
  - [11.5 Check for Updates](#115-check-for-updates)
- [12. Privacy & Data](#12-privacy--data)

---

## 1. Setup & First Run

### 1.1 First-Run Wizard
On first launch Jot presents a short guided wizard that welcomes the user, walks them through granting
microphone access ([§1.2](#12-microphone-permission-step)), and explains the core loop — press the
hotkey, speak, and the text is pasted where the cursor is. It hands off to the tray-resident app
([§10.2](#102-system-tray)) at the end so the user is ready to dictate.

### 1.2 Microphone Permission Step
The wizard points the user at Windows microphone privacy settings so dictation can capture audio.
Windows only gates the microphone — there is no separate input-monitoring or accessibility gate to grant
(unlike macOS) — so this is the only permission step. Microphone selection itself lives in
[Settings](#24-microphone-selection).

### 1.3 Re-runnable Setup
The wizard can be re-run at any time from Settings, so a user who skipped a step or changed machines can
walk the flow again without reinstalling.

---

## 2. Dictation & Recording

### 2.1 Toggle Recording
Pressing the global hotkey (default **Alt + Space**) starts recording; pressing it again stops and
transcribes. The same toggle is available from the [tray menu](#102-system-tray) and from a record button
in [Recents](#61-single-library-surface). While recording, the [status pill](#41-floating-status-pill)
shows live state.

### 2.2 Any-Length Recordings
There is no hard duration cap — capture streams continuously until the user stops. Because transcription
[streams as you speak](#32-streaming-transcription), stopping a long take is near-instant.

### 2.3 Stop & Save with Esc
While recording, **Esc** stops the dictation and **saves** it through the normal transcribe-and-paste
path — a stray Esc (for example, dismissing an unrelated dialog mid-dictation) can never discard the
take. A discard-without-saving path exists in the code but is deliberately **not bound to any key**
today. **Caveat**: the stop-Esc is registered system-wide while recording, so it is a global key during a
take; contextual (non-global) Esc routing is still on the backlog.

### 2.4 Microphone Selection
Any Windows input device can be picked in Settings, and the choice is remembered across restarts.
**Caveat**: there is not yet a "Last used (not connected)" placeholder for a preferred device that is
currently unplugged, nor graceful mid-dictation handling of a mic that disconnects.

### 2.5 Never Lose Audio
If a transcribe fails or the engine is busy, the take still lands in [Recents](#61-single-library-surface)
as a pending "Needs transcription" row that the user can re-transcribe with one click. **Caveat**: a
startup scan that adopts orphaned audio files left behind by a crash is not yet implemented.

### 2.6 Per-Event Sounds
Jot plays short cues for start, stop, success, cancel, and error. Each event has its own toggle and a
preview button in [Settings](#91-basic-vs-advanced).

---

## 3. Transcription Engine (On-Device)

### 3.1 On-Device Only
Speech-to-text runs entirely on the local machine — audio and transcripts never leave the PC through the
transcription path. The only network activity Jot initiates on its own is the one-time
[model download](#34-in-app-model-download); everything else is user-configured
([AI providers](#71-bring-your-own-provider)) or user-triggered.

### 3.2 Streaming Transcription
The engine transcribes as the user speaks, threading its internal state forward chunk by chunk, so the
transcript is essentially built by the time recording stops — only the final tail remains to process.
This is what powers [live captions](#43-live-captions--expand) and makes stopping near-instant.

### 3.3 One Multilingual Model
A single on-device model covers roughly 33 languages, selected by a language setting rather than a
per-language download. Switching languages is instant and needs no extra download. English is the
best-validated language; several others are confirmed working, with a lower-confidence tier beyond that.

### 3.4 In-App Model Download
On first use the speech model is fetched from within Settings, with a progress bar and an install-state
row. The model is stored under the user's data folder (kept off the system drive by default), not the
system drive.

### 3.5 CPU / GPU Processing
By default the engine runs on the CPU. A Settings toggle switches it to a GPU (DirectML) backend, which
falls back to CPU automatically if the GPU path is unavailable. The change applies after a restart.

### 3.6 Deterministic On-Device Cleanup
Every dictation passes through an always-on, on-device tidy before it is saved and pasted: it scrubs
model artifacts, removes filler words (for the major Western European languages), and normalizes spoken
numbers to digits (English only). It uses no AI and no network, is separate from the optional
[AI cleanup](#72-transcript-cleanup), and can be turned off in [Settings](#91-basic-vs-advanced).
Unknown languages pass through untouched. A lighter cosmetic version of this pass also runs on the
[live-caption preview](#43-live-captions--expand). **Maintainer note**: this replaces the macOS
Parakeet-era post-processing chain; verify the language gates and ordering still match intent.

---

## 4. Status Pill & Live Captions

### 4.1 Floating Status Pill
A small borderless, always-on-top window sits near the bottom-center of the screen (above the taskbar)
and reflects pipeline state — recording, transcribing, success, error — without stealing focus from the
app the user is typing into. It is draggable and can expand.

### 4.2 Live Amplitude Waveform
While recording, the pill shows a real waveform driven by live microphone level — flat when silent,
active on speech — so the user has honest feedback that audio is being heard, not a canned animation.

### 4.3 Live Captions & Expand
With live captions enabled ([Settings](#91-basic-vs-advanced)), a running partial transcript streams into
the pill as the user speaks. Clicking the pill expands it into a scrollable multi-line transcript with a
Copy button and a Stop control. Live captions are powered by the [streaming engine](#32-streaming-transcription).

### 4.4 Pill Key Hints
While recording, the pill shows the active stop/cancel key chords under the waveform (read live from the
[shortcut settings](#101-global-hotkeys--rebinding)) so the user always knows how to stop or cancel.

### 4.5 Pill State Coverage
The pill covers Recording, Transcribing, Success, and Error. **Caveat**: dedicated **Cleaning up**,
**Rewriting**, and vocab-confirm ("Did you mean…?") states that macOS shows are not yet distinct on
Windows — those fold into the Transcribing/working state for now.

---

## 5. Output — Paste & Clipboard

### 5.1 Auto-Paste at Cursor
When a dictation finishes, Jot places the text on the clipboard and pastes it into the focused app via a
synthetic paste, so the transcript appears where the cursor is. Auto-paste can be turned off, in which
case the text is left on the clipboard for the user to paste manually.

### 5.2 Press Enter After Paste
An optional setting sends Enter immediately after pasting, so a dictated message is pasted and submitted
in one step.

### 5.3 Return to the App I Started In
An opt-in advanced option remembers which app was focused when recording began and delivers the
transcript back to it. Off by default, in which case Jot pastes into whatever is focused when
transcription completes (usually still the same app, since the [pill](#41-floating-status-pill) never
takes focus).

### 5.4 Clipboard Preservation
The user can choose to keep the transcript on the clipboard after pasting, or to restore the clipboard's
previous contents once the paste lands.

### 5.5 Copy / Paste Last Transcription
The most recent transcript can be copied from the [tray menu](#102-system-tray) or from any row in
[Recents](#61-single-library-surface); every Recents row also has an inline copy button. **Caveat**: a
dedicated **Paste-last hotkey** is not wired up on Windows yet (same selection/paste reliability work as
[Rewrite](#74-rewrite--rewrite-with-voice)).

---

## 6. Recents Library & Transcript Detail

### 6.1 Single Library Surface
Recents is one chronological list that interleaves dictations and [rewrite sessions](#69-rewrite-session-detail).
It is the home surface the app opens to, and it hosts a record button that toggles dictation
([§2.1](#21-toggle-recording)).

### 6.2 Date-Grouped, Virtualized List
Entries are bucketed into Today / Yesterday / Last 7 days groups and the list is virtualized so long
histories stay responsive. The library persists across launches.

### 6.3 Search & Tag Filters
A search box filters the list by substring across titles, transcript text, rewrite instruction/original,
and tags. Tapping a tag chip filters to that tag; tags can be added and removed in the
[detail view](#64-recording-detail--playback). **Caveat**: search is substring-only — there is no
AI/semantic search on Windows.

### 6.4 Recording Detail & Playback
Opening a row shows the full transcript plus a slim playback bar (play/pause + scrubber) with real audio
playback for rows that still have their audio file on disk. Tags are edited here, and an overflow "…"
menu (opens on click) offers [Find & Replace](#66-find--replace), [Export WebVTT](#68-export-as-webvtt),
Reveal in folder, and Delete.

### 6.5 Editable Transcripts
The transcript can be edited in place — Edit, then Done — which saves the change and marks the row as
edited. The read-mode transcript and rewrite panes are selectable text, so any portion can be
drag-selected and copied directly (the Copy button still works too).

### 6.6 Find & Replace
Within a transcript, a Find & Replace bar (from the "…" menu or Ctrl+F) offers Find/Replace fields, a live
match count, Match-case, Replace, and Replace-all. Edits persist to the library.

### 6.7 Import an Audio or Video File
The user can drop or browse for an audio/video file; a bundled decoder handles virtually any format
(mp3/mp4/m4a/mov/webm/mkv/ogg/opus/flac/aac/wav and more), converts it to the engine's input format, and
transcribes it on the same [on-device engine](#31-on-device-only), landing it as a normal Recents row. A
pending row is shown while it works. The decoder is downloaded on first use rather than bundled, to keep
the app small.

### 6.8 Export as WebVTT
A transcript can be exported to a `.vtt` subtitle file. **Caveat**: the engine does not surface per-word
timings yet, so the exported cue timestamps are **fabricated** (fixed-length cues), not real word timings.

### 6.9 Rewrite Session Detail
A [rewrite](#74-rewrite--rewrite-with-voice) produces its own Recents entry shown as a stacked
Instruction → Original → Rewritten view, distinct from a plain dictation row.

---

## 7. AI: Cleanup, Rewrite & Prompt Library

Optional and **bring-your-own-provider**. All AI features are off until the user configures a provider,
and the whole AI surface (provider/key fields, Ask Jot, Prompts) stays hidden until then — see
[§9.2](#92-show-advanced-features).

### 7.1 Bring Your Own Provider
Cleanup, rewrite, and [Ask Jot](#8-ask-jot) route to a provider the user configures: OpenAI, Anthropic,
Gemini, or local Ollama. Ollama runs fully on the machine; the cloud providers are opt-in and need an API
key. The key is stored encrypted at rest, per-user, and is only ever sent to the provider the user
configured. (There is no Apple Intelligence analog on Windows; a Settings note explains this substitution.)

### 7.2 Transcript Cleanup
When enabled with a provider configured, each new transcript runs a light AI cleanup pass (punctuation,
capitalization, filler removal) under a time budget, guarded by a faithfulness check that discards an
over-eager rewrite and keeps the raw text. Any failure falls back to the raw transcript. This is separate
from the always-on [deterministic cleanup](#36-deterministic-on-device-cleanup). **Maintainer note**:
this pass has been reported failing against slow local models because of a hardcoded HTTP timeout shorter
than some models' response time; confirm current behavior.

### 7.3 Test Connection
Each provider offers a real credentialed probe that verifies the key/endpoint actually works and returns
friendly messaging on HTTP errors, so the user gets a clear yes/no before relying on it.

### 7.4 Rewrite & Rewrite with Voice
The intended flow: select text in any app, fire the Rewrite hotkey (optionally speaking an instruction),
and Jot rewrites the selection in place via the [prompt picker](#76-prompt-picker-overlay).
**Advertised-but-not-yet-wired**: on Windows this does not work end-to-end yet — capturing the current
selection from another app via synthetic copy is unreliable on Windows 11, so the Rewrite and
Rewrite-with-voice hotkeys are **not registered and are hidden from Settings** until the selection-capture
path is solid. The mechanics have been exercised in isolated testing but are held back pending a
real-world confirmation. A UI-Automation-based selection reader is the planned fix.

### 7.5 Prompt Library
Users can author, edit, delete, pin, and search custom rewrite prompts, and mark one as the default that a
bare Rewrite fires. Prompts persist locally and only cross the network when actually used against a
provider. A set of bundled starter prompts ships across categories (Essentials / Convert / Email /
Rewrite / Code / Translate). **Caveat**: the bundled set is smaller than macOS's, and per-prompt sample
input/output, a read-only detail sheet, and AI-assisted "generate a sample" authoring are not built.
Because [Rewrite](#74-rewrite--rewrite-with-voice) itself isn't wired, the Prompts surface is currently
hidden in navigation.

### 7.6 Prompt Picker Overlay
An overlay lets the user choose which prompt to apply to a rewrite. It is built but effectively
unreachable in normal use today because the [Rewrite hotkey/selection path](#74-rewrite--rewrite-with-voice)
doesn't yet work.

---

## 8. Ask Jot

An advanced-gated chat pane routes natural-language questions to the configured
[AI provider](#71-bring-your-own-provider) and shows offline fallback answers when none is set.
**Caveat**: grounding is a single hardcoded facts string, not macOS's document-grounded retrieval, and it
lacks feature-citation links, in-chat voice input, and response streaming. Ask Jot is hidden from
navigation while AI is off ([§9.2](#92-show-advanced-features)).

---

## 9. Settings

### 9.1 Basic vs Advanced
By default Settings shows only the essentials — Appearance, Microphone, Language, and the
[Show advanced features](#92-show-advanced-features) toggle. Turning that on reveals the rest: launch at
login, [save location and retention](#93-data-location--retention),
[model download](#34-in-app-model-download), [CPU/GPU processing](#35-cpu--gpu-processing), live captions,
auto-paste / press-Enter / keep-clipboard, [per-event sounds](#26-per-event-sounds), and
[reset controls](#94-reset--erase).

### 9.2 Show Advanced Features
A master switch reveals the more advanced surfaces — Ask Jot, extra shortcuts, and the vocabulary
surface. The [AI provider/key/cleanup fields](#71-bring-your-own-provider) and
[Vocabulary](#95-custom-vocabulary) additionally stay hidden until they actually work, so the app never
shows a control that would fail.

### 9.3 Data Location & Retention
Recordings and the transcript library live in a user-chosen data folder; by default Jot picks the fixed
drive with the most free space that isn't the system drive, to keep the system drive clear. A retention
control ("Keep audio") prunes old audio files while keeping transcripts. **Caveat**: log files still write
to the default per-user location regardless of the chosen data folder.

### 9.4 Reset & Erase
A Reset group offers "Reset settings" and "Erase all data," each behind a confirmation. (There is no
"reset permissions" action — Windows manages microphone access in OS Settings, with no in-app analog.)

### 9.5 Custom Vocabulary
**Built-but-hidden**: a custom-terms surface exists in the code but is non-functional — the term list is
in-memory only, never persisted or fed to the decoder — so it is hidden rather than shown broken. It needs
real persistence plus decoder biasing before it ships.

---

## 10. Global Shortcuts, Tray & Window

### 10.1 Global Hotkeys & Rebinding
Jot registers a system-wide toggle hotkey (default **Alt + Space**) plus the
[stop-and-save Esc](#23-stop--save-with-esc) while recording. Hotkeys can be rebound on the Shortcuts
page. **Caveat**: only Toggle recording and stop-Esc are active today; Paste-last, Rewrite, and
Rewrite-with-voice are **not registered** because those features aren't wired
([§7.4](#74-rewrite--rewrite-with-voice), [§5.5](#55-copy--paste-last-transcription)). Windows imposes its
own limits — some bare keys can't be registered, and hold-to-record (push-to-talk) needs a low-level
keyboard hook that isn't built yet.

### 10.2 System Tray
Jot lives in the system tray. The tray menu offers Start/Stop dictation (dynamic label), Copy last
transcription, a Recent transcriptions submenu (last 10, click to copy), Open Jot…, and Quit. Closing the
main window hides to the tray; only Quit exits. **Caveat**: a "Check for updates…" tray item is
[hidden until a real update check exists](#115-check-for-updates).

### 10.3 Main Window & Navigation
The main window is a Fluent navigation shell with a custom Mica-capable titlebar and a left nav: Recents,
Shortcuts, Help, About, and Settings (footer). Ask Jot and Prompts are hidden while AI is off. Page
scrolling and PageUp/PageDown/Ctrl+Home/End paging are handled centrally, and Alt+←/→ move through nav
history. **Caveat**: the back/forward history affordances are only partially wired.

### 10.4 Launch at Login & Single Instance
Jot can start at login via a per-user registry entry (no admin needed) and boots straight into the tray. A
second launch focuses the already-running instance instead of opening a duplicate.

---

## 11. About, Feedback & Donations

### 11.1 About & Help
The About page shows app identity, version, and a privacy pledge, alongside a Help pane organized into
Basics / Advanced / Troubleshooting. A "Restart Jot" troubleshooting action relaunches the app cleanly.
**Caveat**: Settings↔Help deep-links and per-field info popovers are not wired on Windows.

### 11.2 Usage Impact
On-device counters track words dictated, number of recordings, and estimated minutes saved versus typing,
surfaced on the About page. These never leave the PC.

### 11.3 Donate to Charity
About → Donate opens an in-app window that fetches a live donations summary and lists charities with
per-charity Donate buttons that hand off to an external donation service. Jot never handles money or
personal information.

### 11.4 Send Feedback
About → Send feedback opens an in-app composer that posts to a feedback service — there is no mailto/email
client hop. **Maintainer note**: macOS's redacted-log-with-screenshot feedback attachments are not ported;
confirm what the Windows composer sends.

### 11.5 Check for Updates
**Advertised-but-not-yet-wired**: the update path is not connected. An update framework is bundled for
installer-based builds, but there is no working "check for updates" — the About action and the tray item
are hidden/canned until a real network check lands.

---

## 12. Privacy & Data

Core transcription stays local ([§3.1](#31-on-device-only)); the only outbound calls are ones the user
configures (an [AI provider](#71-bring-your-own-provider)) or triggers (the
[model download](#34-in-app-model-download), [import decoder download](#67-import-an-audio-or-video-file),
[donations](#113-donate-to-charity), [feedback](#114-send-feedback)). Optional AI is local (Ollama) or
opt-in cloud, contacted only when configured and enabled. [Custom prompts](#75-prompt-library) and
[AI keys](#71-bring-your-own-provider) are stored locally (keys encrypted at rest). There is no telemetry,
no analytics, and no crash pings. There are no accounts — the app is fully usable without signing in
anywhere.
