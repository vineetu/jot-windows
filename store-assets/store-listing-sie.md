# Jot Sie — Microsoft Store listing copy (DRAFT)

For the SIE flavor, distributed as **public + hidden (direct-link only)**. The public-facing copy
below is deliberately GENERIC — it does NOT name Sony, SIE, PlayStation, PFB, or "AI gateway",
because the listing page is reachable by URL and seen by MS cert reviewers. Internal specifics live
only in the clearly-marked "INTERNAL — not for the public listing" section at the bottom.

Fill into Partner Center → the "Jot Sie" product → Store listing (+ Properties / Age ratings).

---

## Notes for certification (submission → Submission options → "Notes for certification")

Paste verbatim. Explains the one-time model download (the step the public app's 10.1.2.10 review
tripped on) and tells the reviewer NOT to test the optional rewrite (it needs an organization
sign-in they won't have).

> Jot transcribes speech to text 100% on-device. On first launch a short setup wizard opens and one
> step downloads the speech model (~754 MB, one time, needs internet — hosted on our own GitHub
> release CDN, https://github.com/vineetu/jot-windows/releases). Please make sure the test PC has
> internet for this step. After it, dictation works fully offline.
>
> To verify the primary feature (dictation):
> 1. Launch the app — the setup wizard opens automatically on first run.
> 2. On the language step, click "Download & continue" and let the model finish downloading
>    (progress bar "Downloading… X MB of 754 MB"). It is required for transcription.
> 3. Finish the wizard (pick a microphone; leave the rest at defaults).
> 4. Click into any text field (Notepad, a browser box, this form). Press the dictation hotkey —
>    default Alt+Space (shown in Settings → Shortcuts) — speak a sentence, press it again to stop.
>    Your words are typed at the cursor.
>
> Notes:
> • Microphone access is required; Windows prompts on first use — please Allow.
> • The download resumes/retries automatically and the app never crashes on network failure.
> • The optional "Rewrite with…" feature is OFF by default and requires signing in to the
>   organization's own AI service; it is NOT part of core dictation and cannot be exercised without
>   that organizational access. Please review core dictation only.

---

## Product name
Jot Sie

## Short title / subtitle (≤ 50 chars)
On-device dictation for Windows

## Short description (≤ 270 chars)
Press a hotkey, speak, and your words are typed at the cursor — in any app. Jot transcribes
100% on your PC, so your voice never leaves your device. Fast and private. No account, no
telemetry.

## Description (main listing body)
Jot is the fastest way to turn speech into text on Windows — and it runs entirely on your
own PC.

Press one hotkey, start talking, and Jot types what you said right where your cursor is —
in your editor, browser, chat app, email, anywhere. When you release the key, the text is
already pasted. No window to switch to, no "upload and wait."

Everything happens on-device. Your audio is transcribed locally by a modern speech model and
is never uploaded, streamed, or stored in the cloud. After a one-time model download on first
run, Jot works fully offline.

WHY JOT
• Type at the speed of speech — dictate into any app, hands on the keyboard.
• Truly private — transcription runs on your PC; your voice never leaves the device.
• Instant — GPU-accelerated (DirectML) with a CPU fallback that's still real-time.
• A live caption pill shows a waveform and your words as you speak.

MORE
• Transcribe existing audio or video files — just drop them in.
• Searchable history of everything you've dictated, kept locally.
• Native Windows 11 design: Fluent UI, Mica, light/dark, system tray, global hotkeys.
• Works with many spoken languages (English is the most accurate).

An optional "Rewrite with…" feature can clean up or reshape dictated text; it is off by default
and requires signing in to your organization's approved AI service. Core dictation never needs it.

## Feature bullets (Product features field)
Press a hotkey, speak, and text is typed at your cursor in any app
100% on-device transcription — your voice never leaves your PC
Works offline after a one-time model download
GPU-accelerated (DirectML) with a real-time CPU fallback
Live caption pill with waveform while you speak
Transcribe your own audio and video files
Local, searchable dictation history

## Search terms / keywords (up to 7, not shown to users)
dictation, speech to text, voice typing, transcription, offline stt, voice to text, private

---

## Store settings — answers to fill in

### Category
Productivity

### Privacy policy URL (required — app uses the microphone)
https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/
(Reuse the public policy, or publish a Sie-specific variant if the org wants one. If kept, make
sure the policy's optional-AI clause is generic enough to cover the org sign-in path.)

### Microphone capability — justification string
Jot uses the microphone to capture your speech, which is transcribed to text entirely on
your device. Audio is processed locally and is never uploaded, streamed, or stored anywhere.

### Data collection / "How does this app use your data?"
- Jot itself collects NO personal data and includes NO telemetry or analytics.
- Audio and transcripts stay on the device (local files only).
- Network usage is limited to: (1) a one-time speech-model download on first run; (2) an optional
  media-decoder (FFmpeg) download only if you import audio/video files; (3) the OPTIONAL rewrite
  feature — ONLY if you enable it and sign in — sends the transcript text you choose to rewrite to
  your organization's approved AI service, under that organization's terms.
- Answer the questionnaire as: does NOT collect data. (The optional rewrite path shares text with
  the organization's configured service, disclosed above and in the privacy policy.)

### Age rating (IARC questionnaire)
No objectionable content, no user-to-user communication, no ads. Expect the lowest tier
(Everyone / 3+). Answer "No" to violence, sexual content, profanity, gambling, and
unrestricted internet/communication features.

### Pricing
Free

### Distribution (the whole point of this flavor)
Pricing and availability → Audience = PUBLIC (not Private audience). Discoverability →
"Make this product available but not discoverable in the Store" → "Direct link only."
Share the resulting Store link internally; anyone with the link can install, no allowlist.

---

## Screenshots — use a SUBSET of the public set (store-assets/listing/)
Use only the shots that don't reveal provider internals:
  1. 01-main.png    — Recents / dictation history
  2. 02-pill.png    — live recording pill (waveform + caption)
  3. 03-rewrite.png — the "Rewrite with…" palette (provider-agnostic; shows prompts, not providers)
DO NOT reuse 04-settings.png / 05-ai.png — those show the PUBLIC bring-your-own-provider AI section
(OpenAI/Anthropic/etc.), which is wrong for this build. If a settings shot is wanted, recapture it
from the Sony build with the AI section scrolled off-screen (the PFB sign-in card would otherwise
reveal the internal service).

===================================================================================================
## INTERNAL — not for the public listing (context only)
This flavor is the `-p:Flavor=Sony` build: AI providers = None + PFB (Sony AI Gateway) only, gateway
hostnames compiled in. The "organization's approved AI service" referenced generically above IS the
PFB gateway, reachable only on the Sony network with Okta sign-in. Live gateway call is verified by a
colleague on-network, not by MS cert (reviewers can't reach it — hence the cert note above).
