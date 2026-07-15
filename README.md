# Jot for Windows

A native Windows port of [Jot](https://github.com/vineetu/JOT-Transcribe) — free, on-device
dictation. Press a hotkey, speak, and the transcript is pasted at your cursor in any app.
Built from scratch in C#/.NET, using the macOS app as the product guide.

**Status:** a working WPF app with real on-device speech-to-text, a full Fluent UI (Recents
library, Settings, Shortcuts, first-run wizard, status pill, About/Help), and system
integration (tray, global hotkeys, clipboard paste). Some features are explicitly unfinished
or hidden pending more work — see `docs/features.md` (canonical status per feature) and
`docs/plans/fixit-worklist.md` (the prioritized todo) for the honest, up-to-date picture.
Highlights of what's real vs. not:

- ✅ **Transcription is real, not stubbed.** On-device STT via NVIDIA Nemotron 3.5 (int4
  ONNX), streaming, multilingual (~33 languages via one model), CPU by default with an
  optional DirectML GPU backend.
- ✅ **Core dictation loop works end-to-end:** global hotkey → mic capture (WASAPI) → local
  transcription → clipboard-sandwich paste at your cursor.
- ✅ **Recents library** (date-grouped, virtualized, searchable, tag filters), recording
  detail with real playback, editable transcripts, Find & Replace, WebVTT export (timestamps
  are currently fabricated 4s cues, not real word timings), media import via bundled FFmpeg.
- ✅ **Settings, Shortcuts, first-run wizard, live-caption status pill** (real reactive
  waveform from mic RMS), system tray, donate/feedback/usage-stats surfaces.
- ⚠️ **AI cleanup** — implemented (bring-your-own-provider: OpenAI/Anthropic/Gemini/local
  Ollama) but **verified broken** against slow local models due to a hardcoded HTTP timeout
  shorter than some models' response time; falls back silently to the uncleaned transcript.
- ⚠️ **AI rewrite / rewrite-with-voice / paste-last hotkeys** — the underlying mechanics have
  been verified working end-to-end in testing, but the hotkeys remain unregistered and hidden
  from Settings pending a hands-on confirmation on real-world apps.
- ❌ **Custom vocabulary, speaker diarization** — present in the UI code but non-functional
  stubs; both are hidden rather than shown broken.

Don't take any of the above as a substitute for `docs/features.md` — that file is the
source of truth and is kept current as things change.

## Stack

| Concern | Implementation |
|---|---|
| App shell | WPF + [WPF-UI](https://github.com/lepoco/wpfui) Fluent theme, .NET 10 (`net10.0-windows`) |
| Global hotkey | Win32 `RegisterHotKey` on a message-only window (`Recording/GlobalHotkey.cs`) |
| Microphone | WASAPI via NAudio → 16 kHz mono Float32 (`Recording/AudioRecorder.cs`) |
| Transcription | On-device NVIDIA Nemotron 3.5 streaming (int4 ONNX) via ONNX Runtime, CPU or DirectML (`Transcription/Nemotron`) |
| AI (optional) | Bring-your-own-provider (OpenAI / Anthropic / Gemini / local Ollama) for cleanup, rewrite, Ask Jot (`Services/Ai`) |
| Delivery | Clipboard sandwich + synthetic `Ctrl+V` (`Delivery/TextInjector.cs`) |
| Updates | [Velopack](https://velopack.io) self-updater (unpackaged/Setup.exe installs only — skipped automatically in an MSIX/Store package) |

## Build & run

```powershell
dotnet build
dotnet run --project src/Jot
```

Jot lives in the system tray. Press **Alt+Space** to start dictation, **Alt+Space** again
to stop → transcribe → paste. Right-click the tray icon to quit.

## License

Jot for Windows is licensed under the **PolyForm Noncommercial License 1.0.0** — the same
license as the [macOS app](https://github.com/vineetu/JOT-Transcribe). See [LICENSE](LICENSE)
for the full text; in short, noncommercial use is permitted, commercial use is not.

## Privacy

Transcription runs entirely on-device — audio and transcripts never leave your PC. No
accounts, no telemetry. Optional AI cleanup/rewrite only calls a provider you configure
yourself. Full policy: <https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/>
