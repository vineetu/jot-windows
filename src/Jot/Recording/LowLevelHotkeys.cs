using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Jot.Recording;

/// <summary>
/// A global hotkey mechanism for BARE special keys (Apps/"menu", F13–F24, F-keys, NumLock/CapsLock/
/// ScrollLock) that <see cref="GlobalHotkey"/>'s <c>RegisterHotKey</c> can't cleanly own. RegisterHotKey
/// fires <c>WM_HOTKEY</c> but the key's native behaviour still leaks — e.g. binding Toggle-recording to
/// the Apps key made the right-click context menu pop up everywhere, because the menu is raised on the
/// key <b>up</b>, which RegisterHotKey doesn't swallow. This uses a <c>WH_KEYBOARD_LL</c> hook that
/// <b>consumes</b> both the down and the up (returns 1 instead of chaining), so the native action never
/// happens, and dispatches the bound action on the UI thread.
///
/// Only installed when at least one bare special key is actually bound — most users stay on modifier
/// chords (Alt+Space, Alt+/) which keep the lighter RegisterHotKey path. The callback does only a
/// dictionary lookup and an async dispatch, so it stays well under the hook timeout.
/// </summary>
public sealed class LowLevelHotkeys : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<uint, Action> _actions = new();
    private readonly HashSet<uint> _down = new(); // suppress auto-repeat: fire once per physical press

    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc; // must stay rooted for the hook's lifetime

    public LowLevelHotkeys(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>The bare keys this hook can own and suppress. A key here, pressed with NO modifiers, is a
    /// candidate; anything with a modifier stays on the RegisterHotKey path.</summary>
    public static bool IsSuppressableBareKey(HotkeyChord chord)
    {
        if (chord.Modifiers != GlobalHotkey.Modifiers.None || !chord.IsValid) return false;
        Key k = chord.Key;
        if (k is Key.Apps or Key.NumLock or Key.CapsLock or Key.Scroll) return true;
        return k >= Key.F1 && k <= Key.F24; // F1–F24 all carry native meaning (F1=help, etc.)
    }

    /// <summary>Replaces the current bindings. Installs the hook on first non-empty set; removes it when
    /// the set goes empty. Call from the UI thread (the hook lives on this thread's message loop).</summary>
    public void SetBindings(IEnumerable<(uint vk, Action action)> bindings)
    {
        _actions.Clear();
        _down.Clear();
        foreach (var (vk, action) in bindings)
            _actions[vk] = action;

        if (_actions.Count > 0) EnsureHook();
        else RemoveHook();
    }

    private void EnsureHook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback; // root the delegate
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            Services.JotLog.Warn($"LowLevelHotkeys: SetWindowsHookEx failed (err {Marshal.GetLastWin32Error()})");
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            uint vk = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode;
            if (_actions.TryGetValue(vk, out Action? action))
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (_down.Add(vk)) // first down of this press (ignore key-repeat)
                        _dispatcher.BeginInvoke(action);
                    return (IntPtr)1; // SUPPRESS — the app never sees the key
                }
                if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _down.Remove(vk);
                    return (IntPtr)1; // SUPPRESS the up too — this is what kills the Apps context menu
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => RemoveHook();

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
