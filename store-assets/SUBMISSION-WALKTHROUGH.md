# Partner Center submission — click-by-click (Jot Transcribe)

Everything you paste is here. Full copy lives in `store-listing.md`; this is the ordered walkthrough.
Package to upload: `AppxPackages\JotTranscribe_1.0.0.0_x64.msix`. Screenshots: `store-assets\listing\` (5 × 1920×1080).

## 0. Start
Partner Center → Apps and games → **Jot Transcribe** → **Start new submission** (product type: MSIX/PWA).

## 1. Packages
- Drag in `JotTranscribe_1.0.0.0_x64.msix`. Wait for green validation.
- If it flags the publisher/identity, stop and tell me — but it's already set to match your account
  (`Vineet sriram` / `Vineetsriram.JotTranscribe`).

## 2. Pricing and availability
- Price: **Free**
- Markets: **All markets** (or your choice)
- Visibility: **Public**

## 3. Properties
- Category: **Productivity**
- Privacy policy URL: `https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/`
- Website (optional): `https://github.com/vineetu/jot-windows`
- Support contact info: your email or the GitHub issues URL

## 4. Age ratings (IARC questionnaire) — answer exactly:
- Violence / fear / etc. → **No** to all
- Sexual content / nudity → **No**
- Profanity / crude humor → **No**
- Drugs / alcohol / tobacco → **No**
- Gambling (simulated or real) → **No**
- Users can communicate / interact with other users → **No** (transcripts are local; no user-to-user comms)
- Shares user's physical location with others → **No**
- In-app purchases of digital goods → **No**
- Unrestricted internet access (e.g. a built-in browser) → **No**
→ Expected result: rated for everyone (ESRB Everyone / PEGI 3 / IARC 3+).

## 5. Store listing (language: English)
- **Product name:** Jot Transcribe
- **Short description / subtitle:** On-device dictation for Windows
- **Description:** paste the "Description" block from `store-listing.md`
- **What's new in this version:** `First release of Jot for Windows.`
- **Product features** (one per line): paste the "Feature bullets" block from `store-listing.md`
- **Search terms:** dictation, speech to text, voice typing, transcription, offline stt, voice to text, private
- **Screenshots** (upload all 5 from `store-assets\listing\`, this order + captions):
  1. `01-main.png`     — "Press a hotkey, speak, and it's typed at your cursor."
  2. `02-pill.png`     — "A live pill shows it's listening — waveform and words as you talk."
  3. `03-rewrite.png`  — "Rewrite dictated text: shorten, fix grammar, bulletize — from the keyboard."
  4. `05-ai.png`       — "Bring your own AI provider, or run Ollama fully on-device."
  5. `04-settings.png` — "Simple, native Windows settings. Private by default."

## 6. Submission options → data collection ("Does this app collect data?")
- The app itself collects **no** personal data / no telemetry. Answer per `store-listing.md`
  "Data collection" — if there's a free-text notes field, note the optional BYO-AI path.

## 7. Microphone capability justification (if prompted during cert)
"Jot uses the microphone to capture your speech, which is transcribed to text entirely on your
device. Audio is processed locally and is never uploaded, streamed, or stored anywhere."

## 8. Submit
Review the summary → **Submit to the Store**. Certification typically takes a few hours to a day.
