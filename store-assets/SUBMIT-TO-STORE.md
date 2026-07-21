# Submit "Jot Transcribe" to the Microsoft Store — runbook for a browser agent

**Goal:** upload the package and submit the app for certification. This is a resubmission of an existing
product; the store listing, screenshots, age rating and pricing were already filled in and should carry over —
the two things that MUST change are the **package** and the **Notes for certification**. Full listing values
are in the Appendix in case any field is empty.

---

## Facts

- **Partner Center:** https://partner.microsoft.com/dashboard/products (sign in with the owner account)
- **Product:** Jot Transcribe   **Product ID:** 9NDXTM774RGM
- **Package to upload (on this PC):**
  `C:\Users\vinee\projects\jot-windows\AppxPackages\JotTranscribe_1.0.0.0_x64.msix`
  (a single unsigned `.msix`, ~99 MB — the Store signs it automatically; do NOT sign it yourself)

---

## Steps

1. Go to **https://partner.microsoft.com/dashboard**, open **Apps and games -> Jot Transcribe**.
2. Click **Start a new submission** (or **Update**). This opens a submission with the previous values pre-filled.
3. **Packages** page:
   - Remove/replace any old package, then **upload** `JotTranscribe_1.0.0.0_x64.msix` from the path above.
   - Wait for it to validate (green/no errors). It should show version **1.0.0.0**, architecture x64.
   - If it complains that version 1.0.0.0 was already used by a prior submission, STOP and tell the owner —
     the package needs a version bump (a dev rebuild), which can't be done from the browser.
4. **Store listing** page: it should already be filled. If anything is blank, fill it from the **Appendix**.
   Confirm at least: name, description, and 1+ screenshot. Leave existing screenshots as-is.
5. **Properties** page: Category = **Productivity**. Privacy policy URL =
   `https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/`
6. **Age ratings**: if not already rated, complete the questionnaire answering **No** to all violence /
   sexual content / profanity / gambling / unrestricted communication. Expect the lowest tier (Everyone).
7. **Pricing and availability**: **Free**. Markets: all. (Leave as previously set.)
8. **Submission options -> Notes for certification**: **clear the field and paste the block below, exactly.**
   This is the most important step — it tells the human tester how to trigger the model download in the wizard.
9. Click **Submit to the Store**. Done. Certification takes up to ~3 business days.

---

## PASTE THIS into "Notes for certification" (verbatim)

```
Jot transcribes speech to text 100% on-device. On first launch a short setup wizard opens, and one step
downloads the speech model (~754 MB, one time, needs internet). Please make sure the test PC has internet
for this step. The model is hosted on our own GitHub release CDN
(https://github.com/vineetu/jot-windows/releases). After this one-time download, dictation works fully offline.

To verify the primary feature (dictation):
1. Launch Jot Transcribe - the setup wizard opens automatically on first run.
2. Choose a storage folder (or keep the default), then on the "What language will you speak?" step click the
   "Download and continue" button. The model downloads with a progress bar ("Downloading... X MB of 754 MB")
   and the wizard continues automatically once it finishes. Please let it finish - it is required for
   transcription (time depends on the connection).
3. Finish the wizard (pick a microphone; the remaining steps can be left at their defaults).
4. Click into any text field (Notepad, a browser box, even this form). Press the dictation hotkey - default
   Alt+Space (the current binding is shown in Settings > Shortcuts) - speak a sentence, then press it again to
   stop. Your words are typed at the cursor.

Notes:
- Microphone access is required; Windows prompts on first use - please Allow.
- The download resumes/retries automatically if interrupted, and the app never crashes on a network failure.
- The optional "Rewrite with..." / AI features are OFF by default and require the tester to supply their own
  AI provider key (OpenAI/Anthropic/Google) or a local Ollama endpoint - they are not part of core dictation.

Product ID: 9NDXTM774RGM
```

---

## Appendix — listing values (only needed if a field is blank)

**Product name:** Jot Transcribe

**Short description (<=270 chars):**
Press a hotkey, speak, and your words are typed at the cursor - in any app. Jot transcribes 100% on your PC,
so your voice never leaves your device. Fast, private, and free. No account, no cloud, no telemetry.

**Description:**
Jot is the fastest way to turn speech into text on Windows - and it runs entirely on your own PC.

Press one hotkey, start talking, and Jot types what you said right where your cursor is - in your editor,
browser, chat app, email, anywhere. When you release the key, the text is already pasted. No window to switch
to, no "upload and wait."

Everything happens on-device. Your audio is transcribed locally by a modern speech model and is never
uploaded, streamed, or stored in the cloud. There's no account to create and no telemetry. After a one-time
model download on first run, Jot works fully offline.

WHY JOT
- Type at the speed of speech - dictate into any app, hands on the keyboard.
- Truly private - transcription runs on your PC; your voice never leaves the device.
- Instant - GPU-accelerated (DirectML) with a CPU fallback that's still real-time.
- A live caption pill shows a waveform and your words as you speak.
- Free, with no account and no ads.

REWRITE & CLEAN UP (OPTIONAL)
- A keyboard-first "Rewrite with..." palette turns dictated text into a cleaner version - shorter, fix
  spelling & grammar, convert to bullet points, and more.
- Optional transcript clean-up removes filler words and fixes punctuation.
- These optional AI features are bring-your-own-provider: connect Ollama to run fully on-device, or add your
  own OpenAI, Anthropic, or Google Gemini API key. Core dictation never needs any of this.

MORE
- Transcribe existing audio or video files - just drop them in.
- Searchable history of everything you've dictated, kept locally.
- Native Windows 11 design: Fluent UI, Mica, light/dark, system tray, global hotkeys.
- Works with many spoken languages (English is the most accurate).

**Feature bullets (one per line):**
Press a hotkey, speak, and text is typed at your cursor in any app
100% on-device transcription - your voice never leaves your PC
Works offline after a one-time model download
GPU-accelerated (DirectML) with a real-time CPU fallback
Live caption pill with waveform while you speak
"Rewrite with..." palette: shorten, fix grammar, bulletize, and more
Optional AI clean-up and rewrite (bring your own key, or run Ollama locally)
Transcribe your own audio and video files
Local, searchable dictation history
Free - no account, no ads, no telemetry

**Search terms (up to 7):** dictation, speech to text, voice typing, transcription, offline stt, voice to text, private

**Category:** Productivity
**Privacy policy URL:** https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/
**Pricing:** Free

**Microphone capability justification:**
Jot uses the microphone to capture your speech, which is transcribed to text entirely on your device. Audio is
processed locally and is never uploaded, streamed, or stored anywhere.

**Data collection answer:** Does NOT collect data. (Optional BYO-AI rewrite sends the text you choose to
rewrite to the third-party AI provider you configure, under your own account - disclosed in the privacy policy.)

**Screenshots (already-made 1920x1080 PNGs on this PC):**
`C:\Users\vinee\projects\jot-windows\store-assets\listing\` -> 01-main.png, 02-pill.png, 03-rewrite.png,
04-settings.png, 05-ai.png. Suggested order: 01, 02, 03, 05, 04.
