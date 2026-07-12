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
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    public static void PasteAtCursor(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

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

    private static void SendCtrlV()
    {
        var inputs = new[]
        {
            KeyInput(VK_CONTROL, false),
            KeyInput(VK_V, false),
            KeyInput(VK_V, true),
            KeyInput(VK_CONTROL, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

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
