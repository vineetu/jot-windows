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
    private const ushort SCAN_CONTROL = 0x1D;
    private const ushort SCAN_V = 0x2F;
    private const ushort SCAN_RETURN = 0x1C;

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

        // 3. Synthetic Ctrl+V into the focused app.
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
