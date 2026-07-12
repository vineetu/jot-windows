# Jot for Windows

A native Windows port of [Jot](https://github.com/vineetu/JOT-Transcribe) — free, on-device
dictation. Press a hotkey, speak, and the transcript is pasted at your cursor in any app.
Built from scratch in C#/.NET, using the macOS app as the product guide.

**Status:** early. Milestone 1 (walking skeleton) works: tray app + global hotkey +
microphone capture + clipboard paste, with transcription stubbed.

## Stack

| Concern | Implementation |
|---|---|
| App shell | WPF + WinForms tray icon, .NET 10 (`net10.0-windows`) |
| Global hotkey | Win32 `RegisterHotKey` on a message-only window (`Recording/GlobalHotkey.cs`) |
| Microphone | WASAPI via NAudio → 16 kHz mono Float32 (`Recording/AudioRecorder.cs`) |
| Transcription | **Planned:** Parakeet TDT via ONNX Runtime + DirectML (stubbed today) |
| Delivery | Clipboard sandwich + synthetic `Ctrl+V` (`Delivery/TextInjector.cs`) |

## Build & run

```powershell
dotnet build
dotnet run --project src/Jot
```

Jot lives in the system tray. Press **Alt+Space** to start dictation, **Alt+Space** again
to stop → transcribe → paste. Right-click the tray icon to quit.

## Roadmap

1. ✅ Walking skeleton — hotkey / mic / paste (transcript stubbed)
2. Real STT — Parakeet on DirectML (Python sidecar first, native C# ONNX later)
3. Settings, recording history (SQLite), on-screen status pill, first-run wizard
4. Rewrite-by-voice, transcript cleanup, custom vocabulary, Ask Jot
