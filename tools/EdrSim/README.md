# EdrSim — corporate anti-keylogger simulator

Bench instrument for the "paste doesn't work on the corporate laptop" class of bug. **Never shipped**;
not referenced by Jot, not in any solution.

## What it reproduces

Corporate EDR / anti-keylogger products install a `WH_KEYBOARD_LL` hook and swallow any event whose
`KBDLLHOOKSTRUCT.flags` carries `LLKHF_INJECTED`, by returning `1` instead of calling `CallNextHookEx`.
The keystroke never reaches the foreground app — but **`SendInput` still returns success**, which is why
the app cannot tell that its paste went nowhere.

This tool does exactly that and nothing else.

## Safety

- Only **injected** input is dropped. Real hardware keystrokes pass through untouched, so your keyboard
  keeps working and you can always close the window.
- **Self-expires** (default 120 s, hard cap 900 s) so it cannot be left armed by accident.
- Topmost window with a Stop button; closing the process removes the hook.

While armed it breaks anything that types for you: AutoHotkey, password-manager autofill, remote-control
tooling, the on-screen keyboard, and this repo's own paste self-tests (that last one is the point).

> Force-killing the process (`Stop-Process`) skips the summary file — use the Stop button or let it
> expire if you want `%TEMP%\edrsim-result.txt` written.

## Build & run

```powershell
dotnet build tools\EdrSim\EdrSim.csproj

# interactive: window shows live blocked/passed counters
tools\EdrSim\bin\Debug\net10.0-windows\EdrSim.exe --seconds 180

# scripted: no visible window, same hook
tools\EdrSim\bin\Debug\net10.0-windows\EdrSim.exe --headless --seconds 75
```

## Verifying it is actually armed

Use Jot's own probe rather than trusting the window:

```powershell
Jot.exe --injecttest    # %TEMP%\jot-injecttest.txt -> "SyntheticInputWorks = False" when armed
```

## Measured result (2026-08-13, this machine)

With the simulator armed, `Jot.exe --pastenotepadtest`:

| | value |
|---|---|
| Jot's reported outcome | `pasteResult=Pasted` |
| Jot's log | `eventsSent=4/4` |
| Notepad document | **empty** |
| clipboard afterwards | the user's previous clipboard, restored |

So the transcript was silently destroyed: Jot reported success, nothing was pasted, and the clipboard
sandwich then restored the old contents over it.

**`eventsSent` cannot detect this failure.** `SendInput` reports all 4 events accepted because the hook
drops them downstream. Any "did the paste land?" logic has to read the target or the clipboard, not the
`SendInput` return value.
