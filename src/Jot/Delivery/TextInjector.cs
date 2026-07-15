using System.Runtime.InteropServices;
// WinForms interop is enabled project-wide, so alias to the WPF types explicitly.
using Clipboard = System.Windows.Clipboard;
using Application = System.Windows.Application;

namespace Jot.Delivery;

/// <summary>
/// Types transcribed text into whatever app currently has focus, using the same
/// "clipboard sandwich" the Mac app uses: save the user's clipboard, write our
/// text, send a synthetic paste, then restore the original clipboard.
///
/// Must be called on an STA thread (the WPF UI thread qualifies) because
/// <see cref="Clipboard"/> requires it.
/// </summary>
public static class TextInjector
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort SCAN_CONTROL = 0x1D;
    private const ushort SCAN_V = 0x2F;
    private const ushort SCAN_C = 0x2E;
    internal const ushort SCAN_A = 0x1E;
    private const ushort SCAN_RETURN = 0x1C;
    internal const ushort SCAN_ALT = 0x38;
    private const ushort SCAN_SHIFT = 0x2A;

    /// <summary>The foreground window right now — capture this when recording starts so the
    /// transcript can be delivered back to the app the user was in, even if focus drifts.</summary>
    public static IntPtr CaptureForegroundWindow() => GetForegroundWindow();

    /// <param name="restoreTo">Window to refocus before pasting (the app dictation began in), or
    /// <see cref="IntPtr.Zero"/> to paste into whatever currently has focus.</param>
    /// <param name="keepInClipboard">When true, leave the transcript on the clipboard instead of
    /// restoring the user's previous clipboard contents.</param>
    /// <param name="pressEnter">When true, send Enter after the paste (handy for chat/search boxes).</param>
    public static void PasteAtCursor(string text, IntPtr restoreTo = default,
        bool keepInClipboard = false, bool pressEnter = false)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 0. Return focus to the window the user started dictating in, so the paste lands there
        //    (not on the Jot window they may have glanced at). Never re-target our own window.
        if (restoreTo != IntPtr.Zero && !IsOwnWindow(restoreTo))
        {
            ForceForeground(restoreTo);
            Thread.Sleep(40); // let the focus change settle before synthesising Ctrl+V
        }

        // 1. Save whatever the user had on the clipboard (text only for now) — unless the user
        //    wants us to leave the transcript there, in which case there's nothing to restore.
        string? saved = null;
        if (!keepInClipboard)
        {
            try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { /* clipboard busy */ }
        }

        // 2. Put our transcript on the clipboard.
        SetClipboardText(text);

        // 3. Synthetic Ctrl+V into the focused app (release any held hotkey modifier first).
        ReleaseModifiers();
        SendCtrlV();

        // 4. Optionally submit (Enter) — useful for chat boxes and search fields. Give the paste a
        //    moment to land first so we don't submit an empty field.
        if (pressEnter)
        {
            Thread.Sleep(60);
            SendEnter();
        }

        // 5. Restore the original clipboard after the paste has landed (unless keeping our text).
        //    A short delay avoids racing the target app's paste handler.
        if (!keepInClipboard)
        {
            Task.Delay(150).ContinueWith(_ =>
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (saved is not null) SetClipboardText(saved);
                    else TryClear();
                });
            });
        }
    }

    /// <summary>
    /// Copies the current selection in the focused app (synthetic Ctrl+C) and returns it, restoring the
    /// user's previous clipboard afterward. Returns "" when nothing is selected. STA thread only.
    /// </summary>
    public static string CaptureSelection(int pollBudgetMs = 600)
    {
        string? saved = null;
        try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { /* clipboard busy */ }

        // Clear first so we can distinguish "nothing was copied" from "the same text was already there".
        TryClear();
        ReleaseModifiers(); // synthetic key-up for the hotkey's modifier (e.g. Alt) — necessary but NOT
        // sufficient: a synthetic up doesn't override a modifier the user's finger is still physically
        // holding down (confirmed empirically — with real Alt held, a synthetic release + Ctrl+C reads
        // as Ctrl+Alt+C to the target app, which isn't bound to Copy, so nothing gets captured). Since
        // WM_HOTKEY fires the instant the combo completes — before the user has necessarily released
        // either key — this is the normal case for a fast tap, not an edge case. Actively wait for the
        // REAL hardware state to clear instead of just hoping 40ms was enough.
        WaitForRealModifiersReleased();
        SendKeyChord(SCAN_CONTROL, SCAN_C);

        // Poll for the copy to land instead of a single fixed wait: slow apps (browsers, Electron,
        // Office) can take well over 100ms to service Ctrl+C, and a too-short wait reads an empty
        // clipboard and wrongly reports "nothing selected." Retry until text appears or we time out.
        // pollBudgetMs is overridable so a dev self-test can tell a real timeout apart from a real failure.
        string captured = "";
        for (int waited = 0; waited < pollBudgetMs; waited += 30)
        {
            Thread.Sleep(30);
            try { if (Clipboard.ContainsText()) { captured = Clipboard.GetText(); if (captured.Length > 0) break; } }
            catch { /* clipboard busy — retry */ }
        }

        // Restore the user's clipboard.
        if (saved is not null) SetClipboardText(saved); else TryClear();
        return captured;
    }

    private static void SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch { Thread.Sleep(20); } // clipboard is a shared global resource; retry briefly
        }
    }

    private static void TryClear()
    {
        try { Clipboard.Clear(); } catch { /* best effort */ }
    }

    // Ctrl+V by SCAN CODE, not virtual-key: some apps (terminals, games, Electron surfaces) only
    // honour scan-coded synthetic input. Ctrl held down around a V press-and-release.
    private static void SendCtrlV() => SendKeyChord(SCAN_CONTROL, SCAN_V);

    /// <summary>Presses the given scan codes together (in order), then releases them in reverse — e.g.
    /// <c>SendKeyChord(SCAN_CONTROL, SCAN_A)</c> for Ctrl+A. Shared by the paste path and dev self-tests.</summary>
    /// <summary>Injects key-ups for Alt/Ctrl/Shift so a still-held global-hotkey modifier (e.g. the Alt
    /// of "Alt+/") doesn't corrupt the synthetic Ctrl+C/Ctrl+V we're about to send.</summary>
    private static void ReleaseModifiers()
    {
        var ups = new[]
        {
            ScanInput(SCAN_ALT, keyUp: true),
            ScanInput(SCAN_CONTROL, keyUp: true),
            ScanInput(SCAN_SHIFT, keyUp: true),
        };
        SendInput((uint)ups.Length, ups, Marshal.SizeOf<INPUT>());
    }

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Actively waits (bounded) for Alt/Ctrl/Shift to be genuinely, physically released —
    /// <see cref="ReleaseModifiers"/>'s synthetic key-up alone isn't sufficient when the user's finger
    /// is still on the key (confirmed empirically against real Notepad: with Alt still physically held,
    /// a synthetic release + Ctrl+C still reads as Ctrl+Alt+C to the target app). Bounded so a user who
    /// genuinely holds the modifier for a while doesn't hang the capture indefinitely — falls through
    /// and lets the caller's own clipboard-poll timeout be the final backstop.</summary>
    private static long WaitForRealModifiersReleased(int timeoutMs = 400)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            bool anyDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if (!anyDown) return sw.ElapsedMilliseconds;
            Thread.Sleep(10);
        }
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Presses+releases a single key by VIRTUAL-KEY code rather than scan code — dev/self-test
    /// use only. Toggle keys (NumLock/CapsLock) route through a legacy extended-key scan-code
    /// translation table that virtual-key injection sidesteps, giving a cleaner read on whether the
    /// OS's own toggle-state handling fires independently of <c>RegisterHotKey</c>.</summary>
    internal static void SendVirtualKeyPress(ushort vk)
    {
        var inputs = new[] { VkInput(vk, keyUp: false), VkInput(vk, keyUp: true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT VkInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 } },
    };

    /// <summary>Presses a scan-coded key WITHOUT releasing it — dev/self-test use, to simulate a
    /// hotkey's modifier (e.g. Alt) still being physically held down at the moment its action runs,
    /// which is the real, common case for a live hotkey press but never happens when a dev hook
    /// invokes capture logic directly with no real keypress involved.</summary>
    internal static void SendScanKeyDown(ushort scan) => SendInput(1, [ScanInput(scan, keyUp: false)], Marshal.SizeOf<INPUT>());

    /// <summary>Releases a key pressed via <see cref="SendScanKeyDown"/>.</summary>
    internal static void SendScanKeyUp(ushort scan) => SendInput(1, [ScanInput(scan, keyUp: true)], Marshal.SizeOf<INPUT>());


    /// <summary>Types arbitrary text into whatever has focus via KEYEVENTF_UNICODE (layout-independent
    /// synthetic character input — no scan-code/VK mapping needed). Dev/self-test use, to populate a
    /// real external app (e.g. Notepad) with known text for an end-to-end selection-capture test.</summary>
    internal static void SendUnicodeText(string text)
    {
        foreach (char c in text)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } },
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } },
            };
            SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
        }
    }

    internal static void SendKeyChord(params ushort[] scanCodes)
    {
        var inputs = new INPUT[scanCodes.Length * 2];
        for (int i = 0; i < scanCodes.Length; i++)
            inputs[i] = ScanInput(scanCodes[i], keyUp: false);
        for (int i = 0; i < scanCodes.Length; i++)
            inputs[scanCodes.Length + i] = ScanInput(scanCodes[scanCodes.Length - 1 - i], keyUp: true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // Enter by scan code, matching SendCtrlV's approach.
    private static void SendEnter()
    {
        var inputs = new[]
        {
            ScanInput(SCAN_RETURN, false),
            ScanInput(SCAN_RETURN, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT ScanInput(ushort scan, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = scan,
                dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            },
        },
    };

    /// <summary>Forces a window to the foreground (dev/self-test use). Same mechanism the paste path
    /// uses to return focus to the origin app.</summary>
    internal static void FocusWindow(IntPtr hWnd) => ForceForeground(hWnd);

    // A background process can't just call SetForegroundWindow (Windows foreground-lock blocks it,
    // silently). Attaching our input queue to the target window's thread lifts the lock long enough
    // to hand it focus — the standard workaround — so the synthetic Ctrl+V lands in the right app.
    private static void ForceForeground(IntPtr hWnd)
    {
        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = targetThread != thisThread && AttachThreadInput(thisThread, targetThread, true);
        try
        {
            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, targetThread, false);
        }
    }

    private static bool IsOwnWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == GetCurrentProcessId();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // MOUSEINPUT is the largest union member; it MUST be present so that sizeof(INPUT) is the
        // 40 bytes Windows expects on x64. Without it, cbSize is too small and SendInput silently
        // no-ops (returns 0) — i.e. nothing ever gets typed.
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
