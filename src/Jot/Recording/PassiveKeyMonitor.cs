using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Jot.Recording;

/// <summary>
/// A NON-consuming WH_KEYBOARD_LL watcher for exactly one virtual key — the push-to-talk release edge
/// that <c>RegisterHotKey</c> can't deliver (it only fires WM_HOTKEY on the down). Unlike
/// <see cref="LowLevelHotkeys"/> this hook ALWAYS chains (never returns 1), so the key's normal
/// behaviour is untouched: a Ctrl+Shift+Space hold still types nothing extra, a bare Right-Ctrl hold
/// still works as Ctrl for everyone else. Two modes:
/// <list type="bullet">
/// <item><b>UpOnly</b> — modifier-chord holds: the press comes via RegisterHotKey, we only watch the
///   chord's MAIN key go up (modifier-release order is deliberately ignored).</item>
/// <item><b>DownAndUp</b> — bare-modifier holds (Right Ctrl): both edges come from here,
///   auto-repeat suppressed.</item>
/// </list>
/// The callback is one uint compare + a BeginInvoke — far under the LL-hook timeout. If Windows ever
/// drops the hook, worst case the release never fires: recording simply continues and Esc/toggle stops
/// it (RecorderController.HoldActive guards against any double-stop).
/// </summary>
public sealed class PassiveKeyMonitor : IDisposable
{
    public enum Mode { UpOnly, DownAndUp }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly Dispatcher _dispatcher;
    private readonly uint _vk;
    private readonly Mode _mode;
    private readonly Action? _down;
    private readonly Action _up;
    private bool _isDown; // repeat suppression for DownAndUp

    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc; // must stay rooted for the hook's lifetime

    public PassiveKeyMonitor(Dispatcher dispatcher, uint vk, Mode mode, Action? down, Action up)
    {
        _dispatcher = dispatcher;
        _vk = vk;
        _mode = mode;
        _down = down;
        _up = up;

        _proc = HookCallback;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            Services.JotLog.Warn($"PassiveKeyMonitor: SetWindowsHookEx failed (err {Marshal.GetLastWin32Error()})");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode == _vk)
        {
            int msg = wParam.ToInt32();
            if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && _mode == Mode.DownAndUp)
            {
                if (!_isDown) { _isDown = true; if (_down is not null) _dispatcher.BeginInvoke(_down); }
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                _isDown = false;
                _dispatcher.BeginInvoke(_up);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam); // ALWAYS pass through — never swallow
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }

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
