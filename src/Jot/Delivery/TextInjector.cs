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

    /// <summary>The foreground window right now — capture this when recording starts so the
    /// transcript can be delivered back to the app the user was in, even if focus drifts.</summary>
    public static IntPtr CaptureForegroundWindow() => GetForegroundWindow();

    public static void PasteAtCursor(string text, IntPtr restoreTo = default)
    {
        if (string.IsNullOrEmpty(text)) return;

        // 0. Return focus to the window the user started dictating in, so the paste lands there
        //    (not on the Jot window they may have glanced at). Never re-target our own window.
        if (restoreTo != IntPtr.Zero && !IsOwnWindow(restoreTo))
        {
            ForceForeground(restoreTo);
            Thread.Sleep(40); // let the focus change settle before synthesising Ctrl+V
        }

        // 1. Save whatever the user had on the clipboard (text only for now).
        string? saved = null;
        try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { /* clipboard busy */ }

        // 2. Put our transcript on the clipboard.
        SetClipboardText(text);

        // 3. Synthetic Ctrl+V into the focused app.
        SendCtrlV();

        // 4. Restore the original clipboard after the paste has landed.
        //    A short delay avoids racing the target app's paste handler.
        Task.Delay(150).ContinueWith(_ =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (saved is not null) SetClipboardText(saved);
                else TryClear();
            });
        });
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
    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            ScanInput(SCAN_CONTROL, false),
            ScanInput(SCAN_V, false),
            ScanInput(SCAN_V, true),
            ScanInput(SCAN_CONTROL, true),
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
        [FieldOffset(0)] public KEYBDINPUT ki;
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
