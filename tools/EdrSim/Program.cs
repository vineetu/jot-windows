using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EdrSim;

/// <summary>
/// Bench instrument: reproduces what corporate anti-keylogger / EDR products do to synthetic input.
///
/// Those products install a WH_KEYBOARD_LL hook and, for any event whose KBDLLHOOKSTRUCT.flags carries
/// LLKHF_INJECTED, return 1 instead of calling CallNextHookEx — the event is swallowed before it reaches
/// any application. The caller's SendInput still returns success, which is exactly why a blocked paste is
/// so hard to diagnose from inside the app.
///
/// SAFETY — this hook can only ever swallow INJECTED input:
///  - real hardware keystrokes are passed straight through, so the keyboard never stops working;
///  - it self-expires (default 120 s) so it cannot be left armed by accident;
///  - the window is topmost with a Stop button, and closing the process removes the hook.
/// It WILL break anything that types for you while armed (AutoHotkey, password managers, remote control,
/// on-screen keyboard, and this repo's own paste self-tests — that last one is the entire point).
/// </summary>
internal static class Program
{
    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_LOWER_IL_INJECTED = 0x02; // injected by a lower-integrity process
    private const int HardCapSeconds = 900;

    // The delegate must outlive the hook: if it is collected, the callback address dangles and Windows
    // silently stops delivering (or crashes the hooked message loop).
    private static HookProc? _proc;
    private static IntPtr _hook;
    private static int _blocked;
    private static int _passed;

    [STAThread]
    private static void Main(string[] args)
    {
        int seconds = ArgValue(args, "--seconds") is string s && int.TryParse(s, out int n) ? n : 120;
        seconds = Math.Clamp(seconds, 1, HardCapSeconds);
        bool headless = args.Contains("--headless");

        _proc = HookCallback;
        using var self = Process.GetCurrentProcess();
        using var mod = self.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        if (_hook == IntPtr.Zero)
        {
            Report(seconds, $"FAILED to install hook (error {Marshal.GetLastWin32Error()})");
            return;
        }

        ApplicationConfiguration.Initialize();

        var status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11) };
        var stop = new Button { Dock = DockStyle.Bottom, Height = 44, Text = "Stop (disarm)", Font = new Font("Segoe UI", 11) };
        var form = new Form
        {
            Text = "EDR simulator — ARMED",
            Width = 460,
            Height = 220,
            TopMost = true,
            StartPosition = FormStartPosition.CenterScreen,
            // No minimise/maximise: this is a modal-ish instrument you arm and disarm, and a minimised
            // armed hook is precisely the "left it on by accident" case the expiry guards against.
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.White,
        };
        stop.Click += (_, _) => form.Close();
        form.Controls.Add(status);
        form.Controls.Add(stop);

        var deadline = Stopwatch.StartNew();
        var tick = new System.Windows.Forms.Timer { Interval = 200 };
        tick.Tick += (_, _) =>
        {
            int left = seconds - (int)deadline.Elapsed.TotalSeconds;
            if (left <= 0) { form.Close(); return; }
            status.Text = $"ARMED — dropping injected keystrokes\n\n"
                        + $"blocked: {_blocked}     passed through (real keys): {_passed}\n\n"
                        + $"auto-disarms in {left}s";
        };
        tick.Start();

        if (headless) form.Opacity = 0; // scripted run: no window in the way, same hook behaviour
        Application.Run(form);

        tick.Stop();
        UnhookWindowsHookEx(_hook);
        Report(seconds, "OK");
    }

    private static void Report(int seconds, string outcome)
    {
        string path = Path.Combine(Path.GetTempPath(), "edrsim-result.txt");
        File.WriteAllText(path,
            $"{outcome}\narmedSeconds={seconds}\nblockedInjected={_blocked}\npassedReal={_passed}\n");
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0)
            {
                _blocked++;
                return 1; // swallow — never reaches the foreground app, exactly like the real filter
            }
            _passed++;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

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
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
