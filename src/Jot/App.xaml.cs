using System.Threading;
using System.Windows;
using Jot.Recording;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Services.Ai;
using Jot.Services.Navigation;
using Jot.Shell;
using Jot.Transcription;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Velopack;
using Velopack.Sources;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Jot;

/// <summary>
/// Composition root: builds the DI container, owns the tray icon, the global hotkey, and the
/// single-instance guard. There is no <c>StartupUri</c> — Jot boots into the tray and the main
/// window is created lazily on demand. Exit is explicit (tray → Quit); closing the window hides it.
/// </summary>
public partial class App : System.Windows.Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private const string MutexName = "Jot.SingleInstance.Mutex";
    private const string ShowEventName = "Jot.SingleInstance.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private HotkeyManager? _hotkeys;
    private string _hotkeySignature = "";
    private RecorderController? _recorder;
    private Rewrite.RewriteController? _rewrite;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack must run first: it services the install/update/uninstall hooks Setup.exe invokes —
        // uninstall runs us with OnBeforeUninstallFastCallback, which wipes all Jot data. Normal launch
        // returns immediately. Skip for MSIX/Store builds (the Store owns the install lifecycle, so there's
        // no Velopack install to service and the uninstall hook would never legitimately fire). See
        // docs/plans/store-submission.md and fixit-worklist.md B3.
        if (!IsRunningAsPackagedApp())
        {
            VelopackApp.Build()
                .OnBeforeUninstallFastCallback(_ => WipeAllData())
                .Run();
        }

        base.OnStartup(e);

        // App-wide scrolling: one class handler so wheel-over-text works on every page (see PageScrolling).
        Controls.PageScrolling.Install();

        DispatcherUnhandledException += (_, ex) => { LogCrash(ex.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash(ex.ExceptionObject as Exception);

        if (e.Args.Contains("--dumpsymbols"))
        {
            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-symbols.txt"),
                Enum.GetNames(typeof(Wpf.Ui.Controls.SymbolRegular)));
            Shutdown();
            return;
        }

        // `--transcribe <wav> [--dml]` runs the real engine headless → %TEMP%\jot-transcribe-result.txt.
        int transcribeArg = Array.IndexOf(e.Args, "--transcribe");
        if (transcribeArg >= 0 && transcribeArg + 1 < e.Args.Length)
        {
            RunHeadlessTranscribe(e.Args[transcribeArg + 1], e.Args.Contains("--dml"));
            Shutdown();
            return;
        }

        // `--installmodel <dir>` runs the first-run model download into <dir> headless → %TEMP%\jot-install-result.txt.
        int installArg = Array.IndexOf(e.Args, "--installmodel");
        if (installArg >= 0 && installArg + 1 < e.Args.Length)
        {
            RunHeadlessInstall(e.Args[installArg + 1]);
            Shutdown();
            return;
        }

        // `--ffmpegtest <wav>` proves FfmpegInstaller's lazy download fetches a working ffmpeg.exe and
        // decodes a non-wav format end-to-end → %TEMP%\jot-ffmpegtest.txt.
        int ffmpegTestArg = Array.IndexOf(e.Args, "--ffmpegtest");
        if (ffmpegTestArg >= 0 && ffmpegTestArg + 1 < e.Args.Length)
        {
            RunFfmpegTest(e.Args[ffmpegTestArg + 1]);
            Shutdown();
            return;
        }

        // `--pasteselftest` exercises the real paste path against a freshly-launched Notepad, verified
        // via a clipboard round-trip → %TEMP%\jot-pasteselftest.txt.
        if (e.Args.Contains("--pasteselftest"))
        {
            RunPasteSelfTest();
            Shutdown();
            return;
        }

        // `--librarytest` proves the recordings library persists across restarts.
        if (e.Args.Contains("--librarytest"))
        {
            RunLibraryTest();
            Shutdown();
            return;
        }

        // `--nemotest <wav> [--dml]` runs the real Nemotron engine on CPU or DirectML.
        int nemoArg = Array.IndexOf(e.Args, "--nemotest");
        if (nemoArg >= 0 && nemoArg + 1 < e.Args.Length)
        {
            RunNemoTest(e.Args[nemoArg + 1], e.Args.Contains("--dml"));
            Shutdown();
            return;
        }

        // `--fp16test <wav> [--dml]` runs the real Nemotron FP16 engine on DirectML (or CPU) → %TEMP%\jot-fp16test.txt.
        int fp16Arg = Array.IndexOf(e.Args, "--fp16test");
        if (fp16Arg >= 0 && fp16Arg + 1 < e.Args.Length)
        {
            RunFp16Test(e.Args[fp16Arg + 1], e.Args.Contains("--dml"));
            Shutdown();
            return;
        }

        // `--hotkeytest` reports whether each chord can register as a global hotkey → %TEMP%\jot-hotkeytest.txt.
        if (e.Args.Contains("--hotkeytest"))
        {
            string outHk = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-hotkeytest.txt");
            var sb = new System.Text.StringBuilder();
            foreach (var (name, chord) in new[]
            {
                ("Escape", "Escape"), ("Alt+Space", "Alt+Space"), ("F13", "F13"),
                ("PasteLast", "Alt+OemComma"), ("Rewrite", "Alt+OemQuestion"), ("RewriteWithVoice", "Alt+OemPeriod"),
            })
            {
                try
                {
                    if (Recording.HotkeyChord.TryParse(chord, out var hc))
                    {
                        using var hk = new Recording.GlobalHotkey(hc.Modifiers, hc.VirtualKey, id: 50);
                        sb.AppendLine($"{name}: registered={hk.IsRegistered}");
                    }
                    else sb.AppendLine($"{name}: parse failed");
                }
                catch (Exception ex) { sb.AppendLine($"{name}: EXCEPTION {ex.Message}"); }
            }
            System.IO.File.WriteAllText(outHk, sb.ToString());
            Shutdown();
            return;
        }

        // `--hotkeyboxtest` proves the HotkeyBox click-to-capture → chord path works (worklist A5)
        // → %TEMP%\jot-hotkeyboxtest.txt.
        if (e.Args.Contains("--hotkeyboxtest"))
        {
            RunHotkeyBoxTest();
            return;
        }

        // `--specialkeytest` checks whether special keys (NumLock/CapsLock/Apps/F13/F1) can register as
        // bare hotkeys, and whether the OS's NumLock/CapsLock LED-toggle still fires alongside WM_HOTKEY
        // → %TEMP%\jot-specialkeytest.txt.
        if (e.Args.Contains("--specialkeytest"))
        {
            RunSpecialKeyTest();
            Shutdown();
            return;
        }

        // `--passivehooktest` proves a WH_KEYBOARD_LL hook detects a keypress (F1/NumLock) WITHOUT
        // consuming it — the key still reaches the focused window and NumLock's LED toggles normally
        // → %TEMP%\jot-passivehooktest.txt.
        if (e.Args.Contains("--passivehooktest"))
        {
            RunPassiveHookTest();
            return;
        }

        // `--suppresshooktest` proves the suppressing hook (Recording.LowLevelHotkeys) consumes a bound
        // bare special key (Apps) so its context menu can't fire, then releases it when unbound. Fix for
        // "Toggle bound to the Apps key opened right-click menus everywhere." → %TEMP%\jot-suppresshooktest.txt.
        if (e.Args.Contains("--suppresshooktest"))
        {
            RunSuppressHookTest();
            return;
        }

        // `--notepadselftest` drives real notepad.exe (a separate process) to find out why Alt+/ reports
        // "no text selected" in real use though --rewriteselftest passes in-process → %TEMP%\jot-notepadselftest.txt.
        if (e.Args.Contains("--notepadselftest"))
        {
            RunNotepadSelfTest();
            Shutdown();
            return;
        }

        // `--uiacapturetest` verifies the production selection path (UiaSelectionReader.TryReadSelection
        // — UI Automation, no keystroke/clipboard) against real notepad.exe → %TEMP%\jot-uiacapturetest.txt.
        if (e.Args.Contains("--uiacapturetest"))
        {
            RunUiaCaptureTest();
            Shutdown();
            return;
        }

        // `--rewriteselftest` exercises the real RewriteController pipeline (Ctrl+C selection capture →
        // AiClient.RewriteAsync → paste-back) against a self-owned textbox, via local Ollama
        // → %TEMP%\jot-rewriteselftest.txt.
        if (e.Args.Contains("--rewriteselftest"))
        {
            RunRewriteSelfTest();
            return;
        }

#if SONY
        // `--pfbselftest` verifies the two offline-checkable pieces of the PFB integration — per-model
        // request-body quirks (§5 of the integration guide) and JWT exp/sub parsing — without the Sony
        // gateway. Sony flavor only. → %TEMP%\jot-pfbselftest.txt.
        if (e.Args.Contains("--pfbselftest"))
        {
            RunPfbSelfTest();
            Shutdown();
            return;
        }
#endif

        // `--pillscrolltest` verifies the pill's transcript scroll is "sticky" — new text auto-follows to
        // the bottom only if the user was already there, never yanking them down from a scrolled-up read
        // → %TEMP%\jot-pillscrolltest.txt.
        if (e.Args.Contains("--pillscrolltest"))
        {
            RunPillScrollTest();
            return;
        }

        // `--dmldiag` builds the Nemotron encoder on DirectML with verbose ORT logging to find the
        // operator DirectML rejects.
        if (e.Args.Contains("--dmldiag"))
        {
            RunDmlDiag();
            Shutdown();
            return;
        }

        // `--streamtest <wav> [--dml]` feeds a wav through the live streaming path (OpenStream +
        // incremental Accept + Finish) — the exact path live dictation uses.
        int streamArg = Array.IndexOf(e.Args, "--streamtest");
        if (streamArg >= 0 && streamArg + 1 < e.Args.Length)
        {
            RunStreamTest(e.Args[streamArg + 1], e.Args.Contains("--dml"));
            Shutdown();
            return;
        }

        if (!ClaimSingleInstance())
        {
            _showEvent?.Set(); // nudge the running instance to surface, then bow out
            Shutdown();
            return;
        }

        Services = BuildServices();
        // Route all logging into the user's chosen data folder (D5) via the single activity log (D4).
        var settingsForLog = Services.GetRequiredService<ISettingsStore>();
        JotLog.Initialize(() => JotPaths.DataDir(settingsForLog.Current));
        JotLog.Info("Jot starting");
        _recorder = Services.GetRequiredService<RecorderController>();
        _rewrite = Services.GetRequiredService<Rewrite.RewriteController>();
        Services.GetRequiredService<PillController>().Attach(); // status pill now owns pipeline feedback

        // Warm up the model off the UI thread so the first dictation isn't a cold start.
        var transcriber = Services.GetRequiredService<ITranscriber>();
        var settings = Services.GetRequiredService<ISettingsStore>();
        SettingsViewModel.ApplyLanguage(transcriber, settings.Current.Language);
        if (transcriber.IsModelInstalled) _ = Task.Run(transcriber.WarmUp);

        // Enforce the retention window (delete old recordings) off the UI thread.
        _ = Task.Run(() => Services.GetRequiredService<RetentionCleaner>().Prune());

        WireRecorderNotifications();
        SetupTray();
        SetupHotkeys();

        Notify("Jot is running",
            $"Press {ToggleHotkeyLabel()} to start and stop dictation. Double-click the tray icon to open Jot.",
            Forms.ToolTipIcon.Info);

        // `--show` surfaces the main window immediately (Jot normally boots to tray).
        if (e.Args.Contains("--show") || e.Args.Contains("--detail") || e.Args.Contains("--settings")
            || e.Args.Contains("--shortcuts") || e.Args.Contains("--about"))
            ShowMainWindow();
        if (e.Args.Contains("--settings"))
            Dispatcher.BeginInvoke(() => Services.GetRequiredService<INavigator>().Navigate(typeof(Views.SettingsPage)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // `--shortcuts` opens the window on the Shortcuts page.
        if (e.Args.Contains("--shortcuts"))
            Dispatcher.BeginInvoke(() => Services.GetRequiredService<INavigator>().Navigate(typeof(Views.ShortcutsPage)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // `--about` opens the window on the About page.
        if (e.Args.Contains("--about"))
            Dispatcher.BeginInvoke(() => Services.GetRequiredService<INavigator>().Navigate(typeof(Views.AboutPage)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // `--pilldemo` drives the pill with a speech-like envelope (no mic needed).
        if (e.Args.Contains("--pilldemo")) RunPillDemo();
        // `--pickerdemo` shows the rewrite prompt-picker overlay (stays open for review).
        if (e.Args.Contains("--pickerdemo")) RunPickerDemo();
        // `--donatedemo` shows the donate popup (fetches the live donations summary).
        if (e.Args.Contains("--donatedemo")) { new Controls.DonationsWindow().Show(); }
        // `--feedbackdemo` shows the feedback composer (does not auto-send).
        if (e.Args.Contains("--feedbackdemo")) { new Controls.FeedbackWindow().Show(); }
        // First-run setup wizard: on a normal (no-arg) launch, or forced with `--wizard`.
        bool firstRun = !Services.GetRequiredService<ISettingsStore>().Current.FirstRunComplete;
        if (e.Args.Contains("--wizard") || (e.Args.Length == 0 && firstRun)) ShowWizard();
        // A normal / launch-at-login start (no args, already set up) opens the window instead of
        // booting silently to the tray — otherwise users think auto-start didn't work.
        else if (e.Args.Length == 0) ShowMainWindow();
        // `--smoketest` constructs every page in turn so XAML-load errors hit crash.log.
        if (e.Args.Contains("--smoketest")) RunSmokeTest();
        // `--detail` opens the first recording's detail view.
        if (e.Args.Contains("--detail"))
        {
            var store = Services.GetRequiredService<IRecordingStore>();
            var nav = Services.GetRequiredService<INavigator>();
            if (store.Items.Count > 0)
                Dispatcher.BeginInvoke(
                    () => nav.Navigate(typeof(Views.RecordingDetailPage), store.Items[0]),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    // Win32 APPMODEL_ERROR_NO_PACKAGE (15700): GetCurrentPackageFullName returns this when the process
    // has NO package identity (normal unpackaged/Velopack run). Any other value (typically
    // ERROR_INSUFFICIENT_BUFFER 122, since we pass a zero-length buffer) means it DOES — i.e. an
    // MSIX/Store package. Raw P/Invoke to avoid a WinRT dependency. See docs/plans/store-submission.md.
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

    private static bool IsRunningAsPackagedApp()
    {
        int length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result != APPMODEL_ERROR_NO_PACKAGE;
    }

    // Pumps the dispatcher so queued input (e.g. a synthetic WM_PASTE) is processed — a plain
    // Thread.Sleep would block the message pump and the paste would never land.
    private static void Pump(int ms)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(ms), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => frame.Continue = false, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static void RunSpecialKeyTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-specialkeytest.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            // (name, virtual-key code, isToggleKey — NumLock/CapsLock have an OS-tracked LED state).
            var keys = new (string Name, ushort Vk, bool IsToggle)[]
            {
                ("NumLock", 0x90, true),
                ("CapsLock", 0x14, true),
                ("ScrollLock", 0x91, true),
                ("Apps (Menu / \"right-click\" key)", 0x5D, false),
                ("F13", 0x7C, false),
                ("F1", 0x70, false), // registers fine, but grabs F1 system-wide from every app while held
            };

            foreach (var (name, vk, isToggle) in keys)
            {
                log.AppendLine($"--- {name} (vk=0x{vk:X2}) ---");
                bool toggleBefore = isToggle && (GetKeyState(vk) & 1) != 0;

                int pressed = 0;
                Recording.GlobalHotkey? hk = null;
                try
                {
                    hk = new Recording.GlobalHotkey(Recording.GlobalHotkey.Modifiers.None, vk, id: 90);
                    hk.Pressed += () => pressed++;
                    log.AppendLine($"registered={hk.IsRegistered}");
                }
                catch (Exception ex)
                {
                    log.AppendLine($"registerFailed: {ex.Message}");
                    continue;
                }

                Delivery.TextInjector.SendVirtualKeyPress(vk);
                Pump(300);
                log.AppendLine($"hotkeyFired={pressed > 0}");

                if (isToggle)
                {
                    bool toggleAfter = (GetKeyState(vk) & 1) != 0;
                    log.AppendLine($"toggleLedBefore={toggleBefore} after={toggleAfter} " +
                        $"UNWANTED_TOGGLE_SIDE_EFFECT={toggleBefore != toggleAfter}");
                    // Restore the user's real NumLock/CapsLock state if our test flipped it.
                    if (toggleBefore != toggleAfter)
                    {
                        Delivery.TextInjector.SendVirtualKeyPress(vk);
                        Pump(200);
                        log.AppendLine($"restoredLed={(GetKeyState(vk) & 1) != 0}");
                    }
                }

                hk.Dispose();
            }

            System.IO.File.WriteAllText(outPath, log.ToString());
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
    }

    // WH_KEYBOARD_LL: passive, non-consuming global keyboard hook — the alternative to RegisterHotKey's
    // exclusive grab. Used only by --passivehooktest; see fixit-worklist D12.
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private void RunPassiveHookTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-passivehooktest.txt");
        var log = new System.Text.StringBuilder();
        RewriteTestTarget? target = null;
        IntPtr hookHandle = IntPtr.Zero;
        try
        {
            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0); // defeat the foreground lock (headless self-test only)

            int hookSawF1 = 0, hookSawNumLock = 0;
            // The delegate must stay rooted for the hook's lifetime — a bare lambda passed inline
            // would be GC-eligible the moment this method returns its reference to native code.
            LowLevelKeyboardProc proc = (nCode, wParam, lParam) =>
            {
                if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN))
                {
                    var info = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (info.vkCode == 0x70) hookSawF1++;      // VK_F1
                    if (info.vkCode == 0x90) hookSawNumLock++; // VK_NUMLOCK
                }
                // ALWAYS pass through — never swallow.
                return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            };

            using System.Diagnostics.Process curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using System.Diagnostics.ProcessModule curModule = curProcess.MainModule!;
            hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            log.AppendLine($"hookInstalled={hookHandle != IntPtr.Zero}");

            target = new RewriteTestTarget("");
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            Pump(200);
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            int targetSawF1 = 0;
            target.CountKeyDown(System.Windows.Input.Key.F1, () => targetSawF1++);
            Pump(200);

            // F1: does it still reach the focused target window while our hook merely observes?
            Delivery.TextInjector.SendVirtualKeyPress(0x70); // F1
            Pump(300);
            log.AppendLine($"F1: hookSaw={hookSawF1 > 0}  targetWindowAlsoSawIt={targetSawF1 > 0}  " +
                "(both true = passive listen worked — we detected it AND normal delivery wasn't blocked)");

            // NumLock: does the LED still toggle normally while we merely observe?
            bool numLockBefore = (GetKeyState(0x90) & 1) != 0;
            Delivery.TextInjector.SendVirtualKeyPress(0x90); // NumLock
            Pump(300);
            bool numLockAfter = (GetKeyState(0x90) & 1) != 0;
            log.AppendLine($"NumLock: hookSaw={hookSawNumLock > 0}  ledToggledNormally={numLockBefore != numLockAfter}");
            if (numLockBefore != numLockAfter) { Delivery.TextInjector.SendVirtualKeyPress(0x90); Pump(200); } // restore

            bool pass = hookSawF1 > 0 && targetSawF1 > 0 && hookSawNumLock > 0 && numLockBefore != numLockAfter;
            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        finally
        {
            if (hookHandle != IntPtr.Zero) UnhookWindowsHookEx(hookHandle);
            target?.Dispose();
        }
        Shutdown();
    }

    /// <summary>Verifies the suppressing hotkey hook (<see cref="Recording.LowLevelHotkeys"/>) consumes a
    /// bound bare special key (Apps) so its context menu can't fire, then releases it when unbound.</summary>
    private void RunSuppressHookTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-suppresshooktest.txt");
        var log = new System.Text.StringBuilder();
        // Tested with F1, not Apps: both take the IDENTICAL suppression path (IsSuppressableBareKey →
        // _actions map → hook returns 1), but the Apps key interferes with WPF's PreviewKeyDown counter,
        // making "target saw it" useless — F1 gives a conclusive baseline→suppressed→restored sequence.
        const uint VK_F1 = 0x70, VK_APPS = 0x5D;
        RewriteTestTarget? target = null;
        Recording.LowLevelHotkeys? hook = null;
        try
        {
            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0); // defeat foreground lock (headless self-test only)

            int targetSawF1 = 0, hookFiredF1 = 0, hookFiredApps = 0;
            target = new RewriteTestTarget("");
            target.CountKeyDown(System.Windows.Input.Key.F1, () => Interlocked.Increment(ref targetSawF1));
            target.CountKeyDown(System.Windows.Input.Key.Apps, () => { /* counted elsewhere; Apps is unreliable */ });

            Delivery.TextInjector.FocusWindow(target.Hwnd);
            Pump(300);

            hook = new Recording.LowLevelHotkeys(Dispatcher);

            // The target window (own thread) drops the first few synthetic keystrokes while focus settles
            // (~3 presses observed). Retry F1 until one lands so the baseline isn't racing focus; bounded
            // so a genuine "never delivered" still fails.
            for (int i = 0; i < 8 && targetSawF1 == 0; i++)
            {
                Delivery.TextInjector.FocusWindow(target.Hwnd);
                Delivery.TextInjector.SendVirtualKeyPress((ushort)VK_F1);
                Pump(300);
            }
            log.AppendLine($"[warmup] presses until first delivery, targetSawF1={targetSawF1} (warm={targetSawF1 > 0})");
            targetSawF1 = 0; // discard the warm-up

            // Baseline (no hook): the focused target must see F1.
            int sawBaseline = targetSawF1;
            Delivery.TextInjector.SendVirtualKeyPress((ushort)VK_F1);
            Pump(400);
            bool baselineFlows = targetSawF1 > sawBaseline;
            log.AppendLine($"[baseline no-hook] targetSawF1={targetSawF1 - sawBaseline} (delivered={baselineFlows})");

            // Bound: F1 fires our action AND is swallowed (target sees nothing new).
            hook.SetBindings([(VK_F1, () => Interlocked.Increment(ref hookFiredF1))]);
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            Pump(150);
            int sawBeforeBound = targetSawF1;
            Delivery.TextInjector.SendVirtualKeyPress((ushort)VK_F1);
            Pump(400);
            bool actionFired = hookFiredF1 > 0;
            bool targetBlocked = targetSawF1 == sawBeforeBound;
            log.AppendLine($"[bound F1] hookFired={hookFiredF1} (actionFired={actionFired})  " +
                $"targetSawF1={targetSawF1 - sawBeforeBound} (suppressed={targetBlocked})");

            // Unbound: the hook releases F1; normal delivery must resume.
            hook.SetBindings([]);
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            Pump(150);
            int sawBeforeUnbound = targetSawF1;
            Delivery.TextInjector.SendVirtualKeyPress((ushort)VK_F1);
            Pump(400);
            bool flowsWhenUnbound = targetSawF1 > sawBeforeUnbound;
            log.AppendLine($"[unbound F1] targetSawF1={targetSawF1 - sawBeforeUnbound} (normalDeliveryRestored={flowsWhenUnbound})");

            // Apps (the user's actual key): the bound action fires + the hook consumes it.
            hook.SetBindings([(VK_APPS, () => Interlocked.Increment(ref hookFiredApps))]);
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            Pump(150);
            Delivery.TextInjector.SendVirtualKeyPress((ushort)VK_APPS);
            Pump(400);
            log.AppendLine($"[bound Apps] hookFired={hookFiredApps} (actionFired={hookFiredApps > 0}; " +
                "consumed via the same return-1 path as F1 above)");

            bool pass = baselineFlows && actionFired && targetBlocked && flowsWhenUnbound && hookFiredApps > 0;
            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        finally
        {
            hook?.Dispose();
            target?.Dispose();
        }
        Shutdown();
    }

    /// <summary>Drives real notepad.exe to diagnose why Alt+/ reports "no text selected" in actual use
    /// — runs production <see cref="Delivery.TextInjector.CaptureSelection"/> at varying budgets.</summary>
    /// <summary>Full document text via UI Automation TextPattern — doesn't touch the clipboard, so it
    /// pins down when/whether text gets corrupted.</summary>
    private static string ReadUiaDocumentText(IntPtr hwnd)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var textElement = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Document));
            if (textElement is null) return "<no document element>";
            if (!textElement.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out object patObj))
                return "<no TextPattern>";
            return ((System.Windows.Automation.TextPattern)patObj).DocumentRange.GetText(-1);
        }
        catch (Exception ex) { return $"<UIA threw: {ex.Message}>"; }
    }

    /// <summary>Finds the TabItem whose name starts with <paramref name="fileNamePrefix"/> and selects
    /// it via UIA's SelectionItemPattern — makes sure the tab we just opened is actually the active one,
    /// since Notepad's restored session tabs can otherwise stay focused instead.</summary>
    private static bool SelectTabByName(IntPtr hwnd, string fileNamePrefix)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var tabItem = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.AndCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.TabItem),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.NameProperty,
                        fileNamePrefix, System.Windows.Automation.PropertyConditionFlags.IgnoreCase)));
            // Exact-name match can fail (Notepad appends ". Unmodified."/". Modified." etc.) — fall back
            // to a manual prefix scan over every TabItem if the strict condition found nothing.
            if (tabItem is null)
            {
                foreach (System.Windows.Automation.AutomationElement el in root.FindAll(
                    System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.TabItem)))
                {
                    if (el.Current.Name.StartsWith(fileNamePrefix, StringComparison.OrdinalIgnoreCase))
                    { tabItem = el; break; }
                }
            }
            if (tabItem is null) return false;
            if (!tabItem.TryGetCurrentPattern(
                System.Windows.Automation.SelectionItemPattern.Pattern, out object patObj))
                return false;
            ((System.Windows.Automation.SelectionItemPattern)patObj).Select();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Finds the Document (text editor) element and sets UIA focus on it directly — needed
    /// because selecting a tab via <see cref="SelectTabByName"/> brings it to front without moving
    /// actual keyboard focus onto the text surface inside it.</summary>
    private static bool FocusUiaDocument(IntPtr hwnd)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var textElement = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Document));
            if (textElement is null) return false;
            textElement.SetFocus();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Dumps every descendant's ControlType/Name/AutomationId/ClassName — a one-off probe to
    /// find the right locator instead of guessing at a control type.</summary>
    private static string DumpUiaTree(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            foreach (System.Windows.Automation.AutomationElement el in root.FindAll(
                System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                try
                {
                    string patterns = string.Join(",", el.GetSupportedPatterns().Select(p => p.ProgrammaticName));
                    sb.AppendLine($"  ControlType={el.Current.ControlType.ProgrammaticName} Name=[{el.Current.Name}] " +
                        $"AutomationId=[{el.Current.AutomationId}] ClassName=[{el.Current.ClassName}] Patterns=[{patterns}]");
                }
                catch (Exception ex) { sb.AppendLine($"  <element threw: {ex.Message}>"); }
            }
        }
        catch (Exception ex) { sb.AppendLine($"<DumpUiaTree threw: {ex}>"); }
        return sb.ToString();
    }

    private static (bool foundPattern, string selectionText) ReadUiaSelection(IntPtr hwnd)
    {
        var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
        var textElement = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Document));
        if (textElement is null || !textElement.TryGetCurrentPattern(
            System.Windows.Automation.TextPattern.Pattern, out object patObj))
            return (false, "");
        var selection = ((System.Windows.Automation.TextPattern)patObj).GetSelection();
        return (true, selection.Length > 0 ? selection[0].GetText(-1) : "");
    }

    private static void RunNotepadSelfTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-notepadselftest.txt");
        var log = new System.Text.StringBuilder();
        const string marker = "JOT_NOTEPAD_SELFTEST_MARKER_8834 the cert thing is due friday";
        System.Diagnostics.Process? proc = null;
        // Open a PRE-WRITTEN file instead of typing into Notepad — packaged Notepad corrupted synthetic
        // typing (repeated "y"s), a red herring for what we're testing (the CAPTURE side). A file avoids it.
        string tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jot-notepadselftest-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(tmpFile, marker);
        try
        {
            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0); // defeat the foreground lock (headless self-test only)

            // Win11 Notepad is single-instance and tabbed: a leftover tab confuses the UIA document
            // lookup (stale background tab) and focus. Kill all instances for a clean single-tab slate.
            foreach (var stale in System.Diagnostics.Process.GetProcessesByName("Notepad"))
            { try { stale.Kill(); } catch { } }
            Pump(500);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{tmpFile}\"")
            { UseShellExecute = true });
            // The launched "notepad.exe" is a stub for Win11's packaged Notepad: it hands off to a
            // SEPARATE process (same name, different PID) that owns the window — the stub's own
            // MainWindowHandle is never populated. Poll by name for the one with a real window.
            IntPtr hwnd = IntPtr.Zero;
            var launchTimer = System.Diagnostics.Stopwatch.StartNew();
            while (launchTimer.ElapsedMilliseconds < 8000)
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Notepad"))
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; proc = p; break; }
                }
                if (hwnd != IntPtr.Zero) break;
                Pump(150);
            }
            log.AppendLine($"notepadHwnd={hwnd} pid={proc?.Id} (waited {launchTimer.ElapsedMilliseconds}ms)");
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Notepad never produced a main window.");

            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(500); // packaged/MSIX Notepad can be slow to finish its own startup after the window appears
            Delivery.TextInjector.FocusWindow(hwnd);
            log.AppendLine($"foregroundAfterFocus={GetForegroundWindow()} (match={GetForegroundWindow() == hwnd})");

            // Notepad restores its saved tabs across launches (not wiped — it's the user's real history).
            // A restored old tab can end up active instead of ours, giving stale reads. Select OUR tab by
            // filename instead of assuming it's active.
            bool tabSelected = false;
            var tabWait = System.Diagnostics.Stopwatch.StartNew();
            while (tabWait.ElapsedMilliseconds < 5000)
            {
                tabSelected = SelectTabByName(hwnd, System.IO.Path.GetFileName(tmpFile));
                if (tabSelected) break;
                Pump(250);
            }
            log.AppendLine($"tabExplicitlySelected={tabSelected} (waited {tabWait.ElapsedMilliseconds}ms)");
            Pump(300);

            // Selecting the tab brings it to front but doesn't give the text surface keyboard focus —
            // without this, Ctrl+A/Ctrl+C go nowhere. Focus the document element itself.
            bool docFocused = FocusUiaDocument(hwnd);
            log.AppendLine($"documentElementFocused={docFocused}");
            Pump(200);

            // Read the document before any synthetic key — pins down whether corruption happens on
            // file-open or only after input.
            log.AppendLine($"[pre] UIA text before any synthetic key=[{ReadUiaDocumentText(hwnd)}]");
            log.AppendLine("[pre] UIA tree dump:");
            log.AppendLine(DumpUiaTree(hwnd));

            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A); // Ctrl+A, select all
            Pump(300);
            log.AppendLine($"[post-selectall] UIA text after Ctrl+A=[{ReadUiaDocumentText(hwnd)}]");
            log.AppendLine($"foregroundAfterSelect={GetForegroundWindow()} (match={GetForegroundWindow() == hwnd})");

            // Step A: reproduce the production bug (real 600ms budget, real code path).
            var swA = System.Diagnostics.Stopwatch.StartNew();
            string capturedA = Delivery.TextInjector.CaptureSelection(pollBudgetMs: 600);
            swA.Stop();
            bool aCorrect = capturedA.Trim() == marker;
            log.AppendLine($"[A] production CaptureSelection(600ms): ms={swA.ElapsedMilliseconds} " +
                $"gotLength={capturedA.Length} correct={aCorrect} text=[{capturedA}]");
            log.AppendLine($"[post-A] UIA document text=[{ReadUiaDocumentText(hwnd)}]");

            // Step B: same call, much longer budget — isolates "too slow" vs "never happens".
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(200);
            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A); // re-select (capture consumed/cleared clipboard)
            Pump(300);
            log.AppendLine($"[pre-B] UIA document text=[{ReadUiaDocumentText(hwnd)}]");
            var swB = System.Diagnostics.Stopwatch.StartNew();
            string capturedB = Delivery.TextInjector.CaptureSelection(pollBudgetMs: 5000);
            swB.Stop();
            bool bCorrect = capturedB.Trim() == marker;
            log.AppendLine($"[B] production CaptureSelection(5000ms): ms={swB.ElapsedMilliseconds} " +
                $"gotLength={capturedB.Length} correct={bCorrect} text=[{capturedB}]");
            log.AppendLine($"[post-B] UIA document text=[{ReadUiaDocumentText(hwnd)}]");

            // Step C: bare Ctrl+C, generous wait, read clipboard directly — isolates whether synthetic
            // Ctrl+C reaches Notepad at all.
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(200);
            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A);
            Pump(300);
            log.AppendLine($"[pre-C] UIA document text=[{ReadUiaDocumentText(hwnd)}]");
            System.Windows.Clipboard.Clear();
            var swC = System.Diagnostics.Stopwatch.StartNew();
            Delivery.TextInjector.SendKeyChord(0x1D, 0x2E); // bare Ctrl+C
            Pump(2000);
            swC.Stop();
            string rawClip = "";
            try { if (System.Windows.Clipboard.ContainsText()) rawClip = System.Windows.Clipboard.GetText(); }
            catch (Exception ex) { log.AppendLine($"[C] clipboard read threw: {ex.Message}"); }
            log.AppendLine($"[C] bare Ctrl+C + 2s wait, raw clipboard read: ms={swC.ElapsedMilliseconds} " +
                $"gotLength={rawClip.Length} correct={rawClip.Trim() == marker} text=[{rawClip}]");
            log.AppendLine($"[post-C] UIA document text=[{ReadUiaDocumentText(hwnd)}]");
            log.AppendLine($"foregroundAtEnd={GetForegroundWindow()} (match={GetForegroundWindow() == hwnd})");

            // Step D: bypass clipboard/Ctrl+C entirely — read the selection via UIA TextPattern (the
            // worklist's flagged alternative to synthetic Ctrl+C).
            try
            {
                var (textPattern, selText) = ReadUiaSelection(hwnd);
                log.AppendLine($"[D] UIA: foundTextPattern={textPattern}  selectionText=[{selText}] " +
                    $"(matchesMarker={selText.Trim() == marker})");
            }
            catch (Exception ex)
            {
                log.AppendLine($"[D] UIA threw: {ex}");
            }

            // Step E: the real trigger condition A/B/C/D missed — Alt physically held down (key-down, no
            // key-up) at capture time, like a real Alt+/ press. SAFETY: the Alt-down is always released
            // synchronously in this block so a crash can't leave Alt stuck down.
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(200);
            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A);
            Pump(300);
            Delivery.TextInjector.SendScanKeyDown(Delivery.TextInjector.SCAN_ALT); // Alt down — NOT released yet
            string capturedE; long msE;
            try
            {
                Pump(50); // ~ the gap between a real WM_HOTKEY firing and CaptureContext() running
                var swE = System.Diagnostics.Stopwatch.StartNew();
                capturedE = Delivery.TextInjector.CaptureSelection(pollBudgetMs: 600);
                swE.Stop();
                msE = swE.ElapsedMilliseconds;
            }
            finally
            {
                Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT); // ALWAYS release real Alt
            }
            bool eCorrect = capturedE.Trim() == marker;
            log.AppendLine($"[E] FIXED CaptureSelection, Alt held THE WHOLE CALL (worst case — should " +
                $"gracefully report empty, not hang or crash): ms={msE} gotLength={capturedE.Length} " +
                $"correct={eCorrect} text=[{capturedE}]");

            // Step F: the realistic case — Alt held as capture starts (fast Alt+/ tap), released ~100ms
            // in by a background thread while CaptureSelection runs. The fix should detect the release
            // instead of racing a fixed 40ms sleep.
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(200);
            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A);
            Pump(300);
            Delivery.TextInjector.SendScanKeyDown(Delivery.TextInjector.SCAN_ALT);
            string capturedF; long msF;
            try
            {
                var releaseTask = Task.Run(async () =>
                {
                    await Task.Delay(100);
                    Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT);
                });
                var swF = System.Diagnostics.Stopwatch.StartNew();
                capturedF = Delivery.TextInjector.CaptureSelection(pollBudgetMs: 600);
                swF.Stop();
                msF = swF.ElapsedMilliseconds;
                releaseTask.Wait(1000); // make sure the release genuinely happened before moving on
            }
            finally
            {
                Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT); // safety net, idempotent
            }
            bool fCorrect = capturedF.Trim() == marker;
            log.AppendLine($"[F] FIXED CaptureSelection, Alt released ~100ms in (realistic fast-tap " +
                $"timing): ms={msF} gotLength={capturedF.Length} correct={fCorrect} text=[{capturedF}]");

            System.IO.File.WriteAllText(outPath, log.ToString());
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        finally
        {
            // Kill every "Notepad" process this test spawned (stub + real window owner), not just the
            // tracked one.
            try { foreach (var p in System.Diagnostics.Process.GetProcessesByName("Notepad")) { try { p.Kill(); } catch { } } }
            catch { /* best effort — avoid a save-changes prompt hanging around */ }
            try { System.IO.File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    /// <summary>Verifies UIA-first selection capture (<see cref="Delivery.UiaSelectionReader.TryReadSelection"/>)
    /// against real notepad.exe, including with Alt physically held (UIA sends no keys).</summary>
    private void RunUiaCaptureTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-uiacapturetest.txt");
        var log = new System.Text.StringBuilder();
        const string marker = "JOT_UIA_CAPTURE_MARKER_5521 rewrite this sentence please";
        string tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"jot-uiacapturetest-{Guid.NewGuid():N}.txt");
        System.IO.File.WriteAllText(tmpFile, marker);
        try
        {
            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0); // defeat foreground lock (headless self-test only)
            foreach (var stale in System.Diagnostics.Process.GetProcessesByName("Notepad"))
            { try { stale.Kill(); } catch { } }
            Pump(500);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{tmpFile}\"")
            { UseShellExecute = true });
            IntPtr hwnd = IntPtr.Zero;
            var launchTimer = System.Diagnostics.Stopwatch.StartNew();
            while (launchTimer.ElapsedMilliseconds < 8000)
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Notepad"))
                { p.Refresh(); if (p.MainWindowHandle != IntPtr.Zero) { hwnd = p.MainWindowHandle; break; } }
                if (hwnd != IntPtr.Zero) break;
                Pump(150);
            }
            log.AppendLine($"notepadHwnd={hwnd} (waited {launchTimer.ElapsedMilliseconds}ms)");
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Notepad never produced a main window.");

            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(500);
            Delivery.TextInjector.FocusWindow(hwnd);

            var tabWait = System.Diagnostics.Stopwatch.StartNew();
            bool tabSelected = false;
            while (tabWait.ElapsedMilliseconds < 5000)
            { tabSelected = SelectTabByName(hwnd, System.IO.Path.GetFileName(tmpFile)); if (tabSelected) break; Pump(250); }
            log.AppendLine($"tabSelected={tabSelected}");
            bool docFocused = FocusUiaDocument(hwnd);
            log.AppendLine($"documentFocused={docFocused}");
            Pump(200);

            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A); // Ctrl+A, select all
            Pump(300);
            log.AppendLine($"docTextAfterSelectAll=[{ReadUiaDocumentText(hwnd)}]");

            // The real production read: UI Automation, own MTA thread, no keystroke.
            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            string? got1 = Delivery.UiaSelectionReader.TryReadSelection();
            sw1.Stop();
            bool ok1 = (got1 ?? "").Trim() == marker;
            log.AppendLine($"[1] UiaSelectionReader.TryReadSelection(): ms={sw1.ElapsedMilliseconds} " +
                $"len={(got1 ?? "").Length} correct={ok1} text=[{got1}]");

            // Same read with Alt physically held — the condition that broke synthetic Ctrl+C. UIA sends
            // no keys, so this should match [1].
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(150);
            Delivery.TextInjector.SendKeyChord(0x1D, Delivery.TextInjector.SCAN_A);
            Pump(250);
            Delivery.TextInjector.SendScanKeyDown(Delivery.TextInjector.SCAN_ALT); // Alt DOWN, not released
            string? got2; long ms2;
            try
            {
                Pump(50);
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                got2 = Delivery.UiaSelectionReader.TryReadSelection();
                sw2.Stop();
                ms2 = sw2.ElapsedMilliseconds;
            }
            finally { Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT); } // ALWAYS release
            bool ok2 = (got2 ?? "").Trim() == marker;
            log.AppendLine($"[2] TryReadSelection() with Alt HELD the whole call: ms={ms2} " +
                $"len={(got2 ?? "").Length} correct={ok2} text=[{got2}]");

            bool pass = ok1 && ok2;
            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        finally
        {
            try { foreach (var p in System.Diagnostics.Process.GetProcessesByName("Notepad")) { try { p.Kill(); } catch { } } }
            catch { /* best effort */ }
            try { System.IO.File.Delete(tmpFile); } catch { /* best effort */ }
        }
    }

    private void RunPasteSelfTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-pasteselftest.txt");
        const string marker = "JOT_PASTE_OK_4213";
        var log = new System.Text.StringBuilder();
        try
        {
            var tb = new System.Windows.Controls.TextBox { AcceptsReturn = true, FontSize = 16 };
            var w = new Window
            {
                Width = 440, Height = 240, Title = "Jot paste self-test", Content = tb, Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            // Defeat the foreground lock (SPI_SETFOREGROUNDLOCKTIMEOUT=0) so this headless self-test can
            // pull its own window to the front.
            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0);

            w.Show();
            w.Activate();
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(200);
            Delivery.TextInjector.FocusWindow(hwnd); // second nudge now that the lock timeout is 0
            tb.Focus();
            System.Windows.Input.Keyboard.Focus(tb);
            Pump(600);

            log.AppendLine($"ourHwnd={hwnd}");
            log.AppendLine($"foreground={GetForegroundWindow()} (match={GetForegroundWindow() == hwnd})");
            log.AppendLine($"tbKeyboardFocused={tb.IsKeyboardFocused}");
            log.AppendLine($"focusedElement={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "null"}");

            bool clipSet = false;
            try { System.Windows.Clipboard.SetText(marker); clipSet = System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == marker; } catch (Exception ex) { log.AppendLine("clipErr=" + ex.Message); }
            log.AppendLine($"clipboardSet={clipSet}");

            // Directly send scan-coded Ctrl+V (bypass the focus dance — we've already forced foreground).
            Delivery.TextInjector.SendKeyChord(0x1D, 0x2F);
            Pump(800);

            string readback = tb.Text ?? "";
            log.AppendLine($"foregroundAfter={GetForegroundWindow()}");
            log.AppendLine($"tbText=[{readback.Replace("\r", "").Replace("\n", "\\n")}]");

            bool pass = readback.Contains(marker);
            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
            w.Close();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
    }

    private static void RunLibraryTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-librarytest.txt");
        const string marker = "LIBRARYTEST_MARKER_7731";
        try
        {
            var settings = new JsonSettingsStore();
            var store1 = new JsonRecordingStore(settings);
            int before = store1.Items.Count;
            store1.Add(new Models.RecordingItem
            {
                Kind = Models.RecordingKind.Dictation,
                CreatedAt = DateTime.Now,
                Title = marker,
                Transcript = "persistence self-test",
                Status = Models.RecordingStatus.Complete,
            });

            string libPath = Jot.Services.JotPaths.LibraryFile(settings.Current);
            bool fileWritten = System.IO.File.Exists(libPath);

            // Fresh store instance = simulates a restart; must load the marker back.
            var store2 = new JsonRecordingStore(settings);
            var loaded = store2.Items.FirstOrDefault(i => i.Title == marker);
            bool survived = loaded is not null;

            // Clean up the marker so we don't pollute the real library.
            if (loaded is not null) store2.Delete(loaded);

            bool pass = fileWritten && survived;
            System.IO.File.WriteAllText(outPath,
                $"{(pass ? "PASS" : "FAIL")}\nfileWritten={fileWritten}\nsurvivedRestart={survived}\n" +
                $"itemsBefore={before}\nlibraryPath={libPath}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n");
        }
    }

    private static void RunNemoTest(string wavPath, bool useDml)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-nemotest.txt");
        try
        {
            var backend = useDml ? Transcription.Onnx.ComputeBackend.DirectML : Transcription.Onnx.ComputeBackend.Cpu;
            var model = new Transcription.Nemotron.NemotronModel();
            if (!model.IsInstalled)
            {
                System.IO.File.WriteAllText(outPath, $"MODEL NOT INSTALLED at {model.Directory}\n");
                return;
            }
            var factory = new Transcription.Onnx.OnnxSessionFactory();
            string fallback = "";
            factory.BackendFallback += m => fallback += m + " | ";
            using var transcriber = new Transcription.Nemotron.NemotronTranscriber(model, factory, backend);
            float[] samples = WavAudio.ReadMono16k(wavPath);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string text = transcriber.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();
            sw.Stop();
            double seconds = samples.Length / (double)WavAudio.SampleRate;
            System.IO.File.WriteAllText(outPath,
                $"OK\nbackend={backend}\nDML_FELL_BACK_TO_CPU={(fallback.Length > 0)}\nfallbackMsg={fallback}\n" +
                $"audio_s={seconds:0.0}\nms={sw.ElapsedMilliseconds}\nTEXT={text}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR backend={(useDml ? "DirectML" : "CPU")}\n{ex}\n");
        }
    }

    private static void RunFp16Test(string wavPath, bool useDml)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-fp16test.txt");
        try
        {
            var backend = useDml ? Transcription.Onnx.ComputeBackend.DirectML : Transcription.Onnx.ComputeBackend.Cpu;
            var model = new Transcription.Nemotron.NemotronFp16Model();
            if (!model.IsInstalled)
            {
                System.IO.File.WriteAllText(outPath, $"MODEL NOT INSTALLED at {model.Directory}\n");
                return;
            }
            var factory = new Transcription.Onnx.OnnxSessionFactory();
            string fallback = "";
            factory.BackendFallback += m => fallback += m + " | ";
            using var transcriber = new Transcription.Nemotron.NemotronFp16Transcriber(model, factory, backend);
            float[] samples = WavAudio.ReadMono16k(wavPath);

            // First pass warms the model (session load + graph optimisation + kernel priming).
            var loadTimer = System.Diagnostics.Stopwatch.StartNew();
            string text = transcriber.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();
            loadTimer.Stop();

            // Second pass is the true warm-inference cost.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            text = transcriber.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();
            sw.Stop();

            double seconds = samples.Length / (double)WavAudio.SampleRate;
            System.IO.File.WriteAllText(outPath,
                $"OK\nbackend={backend}\nDML_FELL_BACK_TO_CPU={(fallback.Length > 0)}\nfallbackMsg={fallback}\n" +
                $"audio_s={seconds:0.00}\ncold_ms={loadTimer.ElapsedMilliseconds}\nwarm_ms={sw.ElapsedMilliseconds}\n" +
                $"TEXT={text}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR backend={(useDml ? "DirectML" : "CPU")}\n{ex}\n");
        }
    }

    private static void RunDmlDiag()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-dmldiag.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var model = new Transcription.Nemotron.NemotronModel();
            log.AppendLine("encoder=" + model.Encoder + " exists=" + System.IO.File.Exists(model.Encoder));

            // Verbose ORT logging → the DML EP prints each node it assigns; the last one before the
            // failure is the culprit. Logs go to stderr; run with stderr redirected to capture them.
            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = Microsoft.ML.OnnxRuntime.ExecutionMode.ORT_SEQUENTIAL,
                EnableMemoryPattern = false,
                LogSeverityLevel = Microsoft.ML.OnnxRuntime.OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE,
                LogVerbosityLevel = 1,
            };
            opts.AppendExecutionProvider_DML(0);
            try
            {
                using var s = new Microsoft.ML.OnnxRuntime.InferenceSession(model.Encoder, opts);
                log.AppendLine("ENCODER DML SESSION CREATED OK (no failure!)");
            }
            catch (Exception ex)
            {
                log.AppendLine("ENCODER DML FAILED:");
                log.AppendLine(ex.ToString());
            }
        }
        catch (Exception ex) { log.AppendLine("OUTER ERROR: " + ex); }
        System.IO.File.WriteAllText(outPath, log.ToString());
    }

    private static void RunStreamTest(string wavPath, bool useDml)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-streamtest.txt");
        try
        {
            var backend = useDml ? Transcription.Onnx.ComputeBackend.DirectML : Transcription.Onnx.ComputeBackend.Cpu;
            var model = new Transcription.Nemotron.NemotronModel();
            if (!model.IsInstalled) { System.IO.File.WriteAllText(outPath, "MODEL NOT INSTALLED\n"); return; }
            using var transcriber = new Transcription.Nemotron.NemotronTranscriber(
                model, new Transcription.Onnx.OnnxSessionFactory(), backend);
            float[] samples = WavAudio.ReadMono16k(wavPath);

            // Simulate live dictation: open a session and feed ~300ms chunks incrementally.
            var session = transcriber.OpenStream();
            int chunk = WavAudio.SampleRate * 300 / 1000;
            string lastPartial = "";
            int partials = 0;
            for (int i = 0; i < samples.Length; i += chunk)
            {
                int n = Math.Min(chunk, samples.Length - i);
                var slice = new float[n];
                Array.Copy(samples, i, slice, 0, n);
                string p = session.Accept(slice);
                if (p.Length > 0 && p != lastPartial) { lastPartial = p; partials++; }
            }
            string final = session.Finish().Trim();
            System.IO.File.WriteAllText(outPath,
                $"OK\nbackend={backend}\npartialsSeen={partials}\nlastPartialLen={lastPartial.Length}\n" +
                $"finalLen={final.Length}\nFINAL={final}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n");
        }
    }

    private static void RunHeadlessInstall(string dir)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-install-result.txt");
        try
        {
            var model = new Transcription.Nemotron.NemotronModel(dir);
            var installer = new Transcription.Nemotron.NemotronModelInstaller(model);
            Task.Run(() => installer.EnsureInstalledAsync()).GetAwaiter().GetResult();
            System.IO.File.WriteAllText(outPath, $"OK installed={model.IsInstalled}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n");
        }
    }

    private static void RunFfmpegTest(string wavPath)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-ffmpegtest.txt");
        var sb = new System.Text.StringBuilder();
        try
        {
            // Force a real download: wipe any previously-installed copy first.
            if (System.IO.Directory.Exists(Import.FfmpegInstaller.InstallDir))
                System.IO.Directory.Delete(Import.FfmpegInstaller.InstallDir, recursive: true);
            sb.AppendLine($"Pre-check: IsInstalled={Import.FfmpegInstaller.IsInstalled} (expect False)");

            var installer = new Import.FfmpegInstaller();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task.Run(() => installer.EnsureInstalledAsync()).GetAwaiter().GetResult();
            sw.Stop();
            bool installedNow = Import.FfmpegInstaller.IsInstalled;
            long size = installedNow ? new System.IO.FileInfo(Import.FfmpegInstaller.ExePath).Length : 0;
            sb.AppendLine($"Downloaded in {sw.ElapsedMilliseconds} ms, IsInstalled={installedNow}, size={size} bytes");

            // Transcode the given WAV to MP3 with the freshly-downloaded ffmpeg (proves it's a real
            // working binary, not just a byte-count match), then decode that MP3 the same way
            // MediaImporter.Decode does — a non-wav format, end to end, post-download.
            string mp3Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-ffmpegtest-source.mp3");
            RunFfmpeg(Import.FfmpegInstaller.ExePath, $"-y -hide_banner -loglevel error -i \"{wavPath}\" \"{mp3Path}\"");
            long mp3Size = new System.IO.FileInfo(mp3Path).Length;
            sb.AppendLine($"Transcoded WAV->MP3: {mp3Size} bytes");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Import.FfmpegInstaller.ExePath,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-i", mp3Path,
                "-ac", "1", "-ar", "16000", "-f", "f32le", "-" }) psi.ArgumentList.Add(a);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            using var ms = new System.IO.MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(ms);
            proc.WaitForExit();
            byte[] pcm = ms.ToArray();
            sb.AppendLine($"Decoded MP3->PCM: exitCode={proc.ExitCode}, {pcm.Length} bytes ({pcm.Length / 4} float samples)");
            sb.AppendLine(pcm.Length > 1000 && proc.ExitCode == 0 ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex)
        {
            sb.AppendLine("RESULT: FAIL (exception)");
            sb.AppendLine(ex.ToString());
        }
        System.IO.File.WriteAllText(outPath, sb.ToString());
    }

    private static void RunFfmpeg(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    private async Task EnsureSpeechModelReadyAsync(ParakeetTranscriber transcriber)
    {
        try
        {
            var installer = Services.GetRequiredService<ParakeetModelInstaller>();
            if (!installer.IsInstalled)
            {
                Dispatcher.Invoke(() => Notify("Downloading speech model",
                    "Jot is fetching the on-device speech model (~630 MB, one time). Dictation will be ready shortly.",
                    Forms.ToolTipIcon.Info));
                await installer.EnsureInstalledAsync();
                Dispatcher.Invoke(() => Notify("Speech model ready",
                    $"Press {ToggleHotkeyLabel()} to start dictating.", Forms.ToolTipIcon.Info));
            }

            await Task.Run(transcriber.WarmUp);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            Dispatcher.Invoke(() => Notify("Speech model unavailable",
                "Jot couldn't prepare the speech model. Check your connection and restart. " + ex.Message,
                Forms.ToolTipIcon.Warning));
        }
    }

    private static void RunHeadlessTranscribe(string wavPath, bool useDml)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-transcribe-result.txt");
        try
        {
            var backend = useDml
                ? Transcription.Onnx.ComputeBackend.DirectML
                : Transcription.Onnx.ComputeBackend.Cpu;
            using var transcriber = new ParakeetTranscriber(
                new ParakeetModel(), new Transcription.Onnx.OnnxSessionFactory(), backend);

            float[] samples = WavAudio.ReadMono16k(wavPath);

            // First pass warms the model (session load + ORT graph optimisation + kernel priming).
            var loadTimer = System.Diagnostics.Stopwatch.StartNew();
            string text = transcriber.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();
            loadTimer.Stop();

            // Second pass is the true warm-inference cost.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            text = transcriber.TranscribeAsync(samples, WavAudio.SampleRate).GetAwaiter().GetResult();
            sw.Stop();

            double seconds = samples.Length / (double)WavAudio.SampleRate;
            double rtx = sw.Elapsed.TotalSeconds > 0 ? seconds / sw.Elapsed.TotalSeconds : 0;
            System.IO.File.WriteAllText(outPath,
                $"OK\nbackend={backend}\naudio_s={seconds:0.00}\ncold_ms={loadTimer.ElapsedMilliseconds}\n" +
                $"warm_infer_ms={sw.ElapsedMilliseconds}\nwarm_RTx={rtx:0.0}\nTEXT={text}\n");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n");
        }
    }

    private void RunHotkeyBoxTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-hotkeyboxtest.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var box = new Controls.HotkeyBox { Chord = "Alt+Space" };
            int rawKeysSeen = 0;
            var w = new Window
            {
                Width = 320, Height = 130, Topmost = true, Title = "HotkeyBox test",
                Content = box, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            // Observe every key that reaches the box, even ones its own handler marks handled.
            box.AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                new System.Windows.Input.KeyEventHandler((_, ke) => rawKeysSeen++), handledEventsToo: true);

            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0); // defeat the foreground lock

            w.Show(); w.Activate();
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
            Delivery.TextInjector.FocusWindow(hwnd);
            Pump(300);

            // Simulate a real CLICK on the box (no programmatic focus) to exercise the exact path the
            // user reported broken: click -> keyboard focus -> capture starts.
            box.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = System.Windows.UIElement.PreviewMouseLeftButtonDownEvent });
            Pump(300);

            string focused = System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "null";
            bool foreground = GetForegroundWindow() == hwnd;
            string before = box.Chord;

            // Foreground-independent capture check: raise a routed PreviewKeyDown for 'J' directly on the
            // box (synthetic OS input can't be routed to a non-foreground window in this environment). No
            // modifier is physically held, so the expected captured chord is just "J".
            var src = System.Windows.PresentationSource.FromVisual(box);
            if (src is not null)
            {
                var args = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice, src, 0, System.Windows.Input.Key.J)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                box.RaiseEvent(args);
            }
            Pump(250);

            string after = box.Chord;
            bool pass = focused == "HotkeyBox" && after == "J";
            log.AppendLine($"focusedAfterClick={focused}  (expected HotkeyBox — this is the click->focus fix)");
            log.AppendLine($"foregroundMatch={foreground}");
            log.AppendLine($"rawKeysSeenByBox={rawKeysSeen}");
            log.AppendLine($"presentationSource={(src is not null)}");
            log.AppendLine($"before=[{before}]");
            log.AppendLine($"after=[{after}]  (expected J)");
            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
            w.Close();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        Shutdown();
    }

    // Minimal in-memory IRecordingStore so the rewrite self-test never touches the real library.json.
    private sealed class FakeRecordingStore : Services.Abstractions.IRecordingStore
    {
        public System.Collections.ObjectModel.ObservableCollection<Models.RecordingItem> Items { get; } = new();
        public void Add(Models.RecordingItem item) => Items.Insert(0, item);
        public void Delete(Models.RecordingItem item) => Items.Remove(item);
        public void Rename(Models.RecordingItem item, string title) => item.Title = title;
        public IReadOnlyList<string> AllTags() => Array.Empty<string>();
    }

    /// <summary>Rewrite self-test target: a WPF window on its OWN thread/message pump, standing in for an
    /// external app. Needed because <see cref="Delivery.TextInjector.CaptureSelection"/> blocks the calling
    /// thread with <c>Thread.Sleep</c> — a same-thread target would freeze its own pump and never receive
    /// the Ctrl+C, giving a false negative.</summary>
    private sealed class RewriteTestTarget : IDisposable
    {
        public IntPtr Hwnd { get; }
        public System.Windows.Threading.Dispatcher Dispatcher { get; }
        private readonly System.Windows.Controls.TextBox _tb;
        private readonly Window _window;
        private readonly Thread _thread;

        public RewriteTestTarget(string initialText)
        {
            using var ready = new ManualResetEventSlim(false);
            System.Windows.Threading.Dispatcher? dispatcher = null;
            Window? window = null;
            System.Windows.Controls.TextBox? tb = null;
            IntPtr hwnd = IntPtr.Zero;

            _thread = new Thread(() =>
            {
                tb = new System.Windows.Controls.TextBox { AcceptsReturn = true, FontSize = 16, Text = initialText };
                window = new Window
                {
                    Width = 440, Height = 240, Title = "Jot rewrite self-test target", Content = tb,
                    Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                window.Show();
                hwnd = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                ready.Set();
                System.Windows.Threading.Dispatcher.Run(); // this thread now owns the target's own message pump
            })
            { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));

            Dispatcher = dispatcher!;
            _window = window!;
            _tb = tb!;
            Hwnd = hwnd;
        }

        public void FocusAndSelectAll() => Dispatcher.Invoke(() =>
        {
            _tb.Focus();
            System.Windows.Input.Keyboard.Focus(_tb);
            _tb.SelectAll();
        });

        public int SelectionLengthNow() => Dispatcher.Invoke(() => _tb.SelectionLength);
        public bool TextBoxHasKeyboardFocus() => Dispatcher.Invoke(() => _tb.IsKeyboardFocused);
        public string FocusedElementTypeName() => Dispatcher.Invoke(() =>
            System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "null");
        public string TextNow() => Dispatcher.Invoke(() => _tb.Text);

        /// <summary>Increments a counter when the target window sees a KeyDown for <paramref name="key"/>
        /// — proves a passive hook let the key through instead of consuming it.</summary>
        public void CountKeyDown(System.Windows.Input.Key key, Action onSeen) => Dispatcher.Invoke(() =>
        {
            _window.AddHandler(System.Windows.UIElement.PreviewKeyDownEvent,
                new System.Windows.Input.KeyEventHandler((_, ke) =>
                {
                    if (ke.Key == key || (ke.Key == System.Windows.Input.Key.System && ke.SystemKey == key))
                        onSeen();
                }), handledEventsToo: true);
        });

        public void Dispose()
        {
            try { Dispatcher.Invoke(() => _window.Close()); } catch { /* best effort */ }
            try { Dispatcher.InvokeShutdown(); } catch { /* best effort */ }
        }
    }

#if SONY
    // Offline verification of the PFB integration's two correctness-critical pieces. Everything else
    // (the actual gateway call) needs the Sony network and is verified by a colleague on-network.
    private void RunPfbSelfTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-pfbselftest.txt");
        var log = new System.Text.StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (ok) pass++; else fail++;
        }

        try
        {
            // 1) Per-model body quirks (§5). Get one wrong → HTTP 400 every time.
            foreach ((string label, string id) in global::Jot.Services.Ai.PfbGateway.Catalog)
            {
                string json = global::Jot.Services.Ai.PfbGateway.SerializeBody(id, "sys", "user", 0.3, stream: false);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                bool hasTemp = root.TryGetProperty("temperature", out _);
                bool hasReasoning = root.TryGetProperty("reasoning_effort", out var re);
                bool hasMaxCompletion = root.TryGetProperty("max_completion_tokens", out _);
                bool hasMaxTokens = root.TryGetProperty("max_tokens", out _);

                log.AppendLine($"  [{label}] {id}");
                log.AppendLine($"    {json}");
                // No token-limit field on EITHER family (the model runs uncapped).
                Check($"{label}: no token-limit field", !hasMaxCompletion && !hasMaxTokens);
                if (global::Jot.Services.Ai.PfbGateway.IsGpt5(id))
                {
                    Check($"{label}: OMITS temperature", !hasTemp);
                    Check($"{label}: reasoning_effort == none", hasReasoning && re.GetString() == "none");
                }
                else
                {
                    Check($"{label}: sends temperature", hasTemp);
                    Check($"{label}: no reasoning_effort", !hasReasoning);
                }
            }

            // 2) JWT parse: build a synthetic token (exp 2h out, a subject) and round-trip it.
            long exp = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
            static string B64Url(string s)
            {
                string b = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));
                return b.TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            string jwt = $"{B64Url("{\"alg\":\"none\"}")}.{B64Url($"{{\"exp\":{exp},\"sub\":\"tester@sony.com\"}}")}.sig";
            bool parsed = global::Jot.Services.Ai.PfbAuth.TryParse(jwt, out var session);
            Check("JWT parses", parsed && session is not null);
            Check("JWT exp read (valid, ~2h out)", session?.IsValid == true
                && session!.Remaining > TimeSpan.FromMinutes(115) && session.Remaining < TimeSpan.FromMinutes(125));
            Check("JWT sub read", session?.Subject == "tester@sony.com");

            // An already-expired token must be rejected as invalid.
            long past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            string expiredJwt = $"{B64Url("{\"alg\":\"none\"}")}.{B64Url($"{{\"exp\":{past}}}")}.sig";
            bool parsedExpired = global::Jot.Services.Ai.PfbAuth.TryParse(expiredJwt, out var expiredSession);
            Check("expired JWT parses but is invalid", parsedExpired && expiredSession?.IsValid == false);

            // Garbage must not throw and must not parse.
            Check("garbage token rejected", !global::Jot.Services.Ai.PfbAuth.TryParse("not-a-jwt", out _));

            // 3) Sanity on the constants.
            Check("4 models in catalog", global::Jot.Services.Ai.PfbGateway.Catalog.Length == 4);
            Check("base URL is the prod gateway",
                global::Jot.Services.Ai.PfbGateway.ProdBaseUrl == "https://ai-gateway.dspprod.bis.sie.sony.com/pfb/common/v1");
        }
        catch (Exception ex)
        {
            log.AppendLine("EXCEPTION: " + ex);
            fail++;
        }

        log.Insert(0, $"PFB self-test — {pass} passed, {fail} failed\n\n");
        System.IO.File.WriteAllText(outPath, log.ToString());
    }
#endif

    private void RunRewriteSelfTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-rewriteselftest.txt");
        var log = new System.Text.StringBuilder();
        const string original = "um so yeah i think we should uh ship this on friday you know";
        RewriteTestTarget? target = null;
        try
        {
            SystemParametersInfo(0x2001, 0, IntPtr.Zero, 0); // defeat the foreground lock (headless self-test only)

            target = new RewriteTestTarget(original);
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            Pump(200);
            Delivery.TextInjector.FocusWindow(target.Hwnd);
            target.FocusAndSelectAll();
            Pump(300);

            log.AppendLine($"foreground={GetForegroundWindow()} (match={GetForegroundWindow() == target.Hwnd})");
            log.AppendLine($"targetSelectionLength={target.SelectionLengthNow()}");

            // In-memory settings at local Ollama — never saved, so real settings.json is untouched. Uses a
            // fast model so the AI leg finishes inside AiClient's 30s timeout, isolating capture/paste
            // mechanics from any slow-model timeout.
            var settings = new JsonSettingsStore();
            settings.Current.AiProvider = "Ollama";
            settings.Current.AiModel = "qwen3:4b-instruct";
            settings.Current.AiBaseUrl = null;
            settings.Current.KeepInClipboard = false;

            var store = new FakeRecordingStore();
            var ai = new AiClient();
            var credentials = new AiCredentials(settings);
            var sound = new SoundService(settings);
            var recorder = new AudioRecorder();
            var transcriber = new StubTranscriber();
            var rewrite = new Rewrite.RewriteController(transcriber, recorder, settings, store, ai, credentials, sound);

            bool done = false;
            string? succeeded = null;
            string? failedTitle = null, failedMsg = null;
            bool nothingSelected = false;
            rewrite.Succeeded += r => { succeeded = r; done = true; };
            rewrite.Failed += (t, m) => { failedTitle = t; failedMsg = m; done = true; };
            rewrite.NothingSelected += () => { nothingSelected = true; done = true; };

            // Simulate the real Alt+/ trigger: Alt still physically held when WM_HOTKEY fires, released
            // ~100ms later by a background thread (a normal fast tap) — the condition CaptureContext must
            // cope with. Earlier versions called it cold with no key pressed (see --notepadselftest,
            // which found a held Alt defeated the old synthetic-release-only fix).
            log.AppendLine($"focusBeforeAltTap: hasFocus={target.TextBoxHasKeyboardFocus()} " +
                $"element={target.FocusedElementTypeName()}");

            // Isolate the theory: a bare Alt down+up (what the target sees for a real Alt+/, since
            // RegisterHotKey swallows "/") may trigger WPF's menu-mnemonic focus handling, moving focus
            // off the TextBox before Ctrl+C is sent. Tested here with no capture involved.
            Delivery.TextInjector.SendScanKeyDown(Delivery.TextInjector.SCAN_ALT);
            Pump(50);
            Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT);
            Pump(150);
            log.AppendLine($"focusAfterBareAltTap: hasFocus={target.TextBoxHasKeyboardFocus()} " +
                $"element={target.FocusedElementTypeName()}  <-- isolates whether a bare Alt tap alone " +
                "steals focus, independent of anything CaptureSelection does");

            // Re-establish focus/selection as a real user would, then run the end-to-end capture with
            // Alt held ~100ms in.
            target.FocusAndSelectAll();
            Pump(200);
            Delivery.TextInjector.SendScanKeyDown(Delivery.TextInjector.SCAN_ALT);
            var altReleaseTask = Task.Run(async () => { await Task.Delay(100); Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT); });
            bool captured;
            try
            {
                captured = rewrite.CaptureContext(); // real synthetic-Ctrl+C selection capture
            }
            finally
            {
                Delivery.TextInjector.SendScanKeyUp(Delivery.TextInjector.SCAN_ALT); // safety net, idempotent
                altReleaseTask.Wait(1000);
            }
            log.AppendLine($"captureContext(selectionNonEmpty)={captured}  [with real Alt held ~100ms into the call]");
            log.AppendLine($"focusAfterCaptureAttempt: hasFocus={target.TextBoxHasKeyboardFocus()} " +
                $"element={target.FocusedElementTypeName()}");

            if (captured)
            {
                rewrite.RunRewrite("Fix grammar and remove filler words; keep the meaning and every fact.");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!done && sw.ElapsedMilliseconds < 30000) Pump(100);
                log.AppendLine($"timedOut={!done}");
            }
            else
            {
                nothingSelected = true;
            }

            Pump(400); // let the async paste-back (and clipboard restore) land
            string finalText = target.TextNow();

            bool pasteLanded = succeeded is not null && finalText.Trim() == succeeded.Trim();
            bool storeGotRewrite = store.Items.Count == 1 && store.Items[0].Kind == Models.RecordingKind.Rewrite;

            bool pass = captured && succeeded is not null && pasteLanded && storeGotRewrite;

            log.AppendLine($"succeeded={(succeeded is null ? "null" : $"[{succeeded}]")}");
            log.AppendLine($"failed={(failedTitle is null ? "null" : $"{failedTitle}: {failedMsg}")}");
            log.AppendLine($"nothingSelected={nothingSelected}");
            log.AppendLine($"finalTargetText=[{finalText}]");
            log.AppendLine($"pasteLanded={pasteLanded}");
            log.AppendLine($"storeGotRewriteRow={storeGotRewrite}");
            log.AppendLine($"originalText=[{original}]");

            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        finally
        {
            target?.Dispose();
        }
        Shutdown();
    }

    private void RunPillDemo()
    {
        bool expanded = System.Environment.GetCommandLineArgs().Contains("--expanded");
        var pill = new Controls.PillWindow();
        pill.SetKeyHints("Alt + Space", "Esc");       // same API the live PillController path uses
        pill.SetState(Controls.PillState.Recording); // anchors bottom-center on the active monitor

        // A growing caption to exercise the live-text line (and the click-to-expand panel).
        string[] words = ("this is a live caption streaming into the status pill while you keep " +
                          "talking and the words scroll along the bottom edge of the screen").Split(' ');
        int spoken = 0;

        var rnd = new Random(1);
        double t = 0;
        int frames = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        timer.Tick += (_, _) =>
        {
            t += 0.15;
            double envelope = 0.5 + 0.5 * Math.Sin(t * 0.7);     // speech comes in bursts
            pill.PushLevel(envelope * (0.35 + 0.65 * rnd.NextDouble()));
            frames++;
            pill.SetElapsed(TimeSpan.FromSeconds(frames / 30.0).ToString(@"m\:ss"));

            if (frames % 12 == 0 && spoken < words.Length)       // ~1 word every 0.4s
            {
                spoken++;
                pill.SetLiveText(string.Join(' ', words.Take(spoken)));
                if (expanded) pill.ExpandForDemo();
            }
        };
        timer.Start();
    }

    private void RunPillScrollTest()
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-pillscrolltest.txt");
        var log = new System.Text.StringBuilder();
        try
        {
            var pill = new Controls.PillWindow();
            pill.SetKeyHints("Alt + Space", "Esc");
            pill.SetState(Controls.PillState.Recording);
            pill.Show(); // must be shown for a real layout/measure pass — ScrollableHeight stays 0 otherwise
            Pump(200);

            var scroll = pill.TranscriptScroll;

            // Feed enough text to exceed the panel's MaxHeight=140 so it's actually scrollable.
            // ExpandForDemo() is a no-op on empty text (mirrors the real click-to-expand gating), so it
            // must run AFTER the first SetLiveText call, not before.
            string[] words = Enumerable.Range(1, 400).Select(i => $"word{i}").ToArray();
            bool first = true;
            for (int n = 50; n <= 200; n += 50)
            {
                pill.SetLiveText(string.Join(' ', words.Take(n)));
                if (first) { pill.ExpandForDemo(); first = false; }
                Pump(80);
            }
            log.AppendLine($"transcriptTextActualHeight={pill.TranscriptText.ActualHeight:0.0}");
            log.AppendLine($"scrollViewportHeight={scroll.ViewportHeight:0.0}");
            log.AppendLine($"scrollableHeight={scroll.ScrollableHeight:0.0}");
            log.AppendLine($"offsetAfterInitialStream={scroll.VerticalOffset:0.0} " +
                $"(expected ≈ scrollableHeight — still-at-bottom auto-follow, the normal case)");
            bool followedWhileAtBottom =
                scroll.ScrollableHeight > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 4;

            // User scrolls up to read earlier text.
            scroll.ScrollToVerticalOffset(0);
            Pump(150);
            log.AppendLine($"offsetAfterManualScrollUp={scroll.VerticalOffset:0.0} (expected ≈ 0)");

            // More text streams in — THE BUG: this used to force ScrollToEnd() unconditionally, so the
            // view would jump back to the bottom even though the user just scrolled up to read.
            for (int n = 250; n <= 400; n += 50)
            {
                pill.SetLiveText(string.Join(' ', words.Take(n)));
                Pump(80);
            }
            log.AppendLine($"offsetAfterMoreTextWhileScrolledUp={scroll.VerticalOffset:0.0} " +
                "(expected ≈ 0 — must NOT have jumped back to the bottom)");
            bool stayedPutWhileScrolledUp = scroll.VerticalOffset < 4;

            // User scrolls back to the bottom themselves — sticky-follow should resume from there.
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            Pump(150);
            pill.SetLiveText(string.Join(' ', words.Take(400)) + " oneMoreWord");
            Pump(150);
            log.AppendLine($"offsetAfterReturningToBottomThenMoreText={scroll.VerticalOffset:0.0} " +
                $"(expected ≈ scrollableHeight={scroll.ScrollableHeight:0.0} — resumes following)");
            bool resumedFollowingAfterReturningToBottom =
                scroll.VerticalOffset >= scroll.ScrollableHeight - 4;

            bool pass = followedWhileAtBottom && stayedPutWhileScrolledUp && resumedFollowingAfterReturningToBottom;
            log.AppendLine($"followedWhileAtBottom={followedWhileAtBottom}");
            log.AppendLine($"stayedPutWhileScrolledUp={stayedPutWhileScrolledUp}");
            log.AppendLine($"resumedFollowingAfterReturningToBottom={resumedFollowingAfterReturningToBottom}");

            System.IO.File.WriteAllText(outPath, $"{(pass ? "PASS" : "FAIL")}\n{log}");
            pill.Close();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(outPath, $"ERROR\n{ex}\n{log}");
        }
        Shutdown();
    }

    private void RunPickerDemo()
    {
        var vm = Services.GetRequiredService<PromptPickerViewModel>();
        var picker = new Controls.PromptPickerWindow(vm) { CloseOnDeactivate = false };
        picker.Show();
        picker.Activate();
    }

    /// <summary>Relaunch Jot cleanly. Releases the single-instance mutex FIRST so the child can claim it
    /// — otherwise the child bows out as a duplicate and we shut down too, leaving nothing running (the
    /// "Restart quits" bug).</summary>
    public static void RestartApp()
    {
        string? exe = Environment.ProcessPath;
        if (Current is App app)
        {
            // Closing our only handle frees the named mutex so the child's ClaimSingleInstance succeeds.
            try { app._instanceMutex?.Dispose(); } catch { /* ignore */ }
            app._instanceMutex = null;
        }
        try
        {
            if (exe is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { /* if relaunch fails we still shut down; the user can reopen from the Start menu */ }
        Current.Shutdown();
    }

    private bool ClaimSingleInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (createdNew)
        {
            // We're the primary instance: listen for a later launch asking us to show the window.
            ThreadPool.RegisterWaitForSingleObject(
                _showEvent, (_, _) => Dispatcher.Invoke(ShowMainWindow), null, -1, executeOnlyOnce: false);
        }
        return createdNew;
    }

    private static Transcription.Onnx.ComputeBackend ParseBackend(string? device)
        => device is not null && device.Contains("GPU", StringComparison.OrdinalIgnoreCase)
            ? Transcription.Onnx.ComputeBackend.DirectML
            : Transcription.Onnx.ComputeBackend.Cpu;

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<ISoundService, SoundService>();
        services.AddSingleton<IAiClient, AiClient>();
        services.AddSingleton<AiCredentials>();
        services.AddSingleton<PfbAuth>();
        services.AddSingleton<AudioRecorder>();
        services.AddSingleton<Transcription.Onnx.OnnxSessionFactory>();
        services.AddSingleton<ParakeetModel>();
        services.AddSingleton<ParakeetModelInstaller>();
        services.AddSingleton<Transcription.Nemotron.NemotronModel>(sp =>
            new Transcription.Nemotron.NemotronModel(settings: sp.GetRequiredService<Services.Abstractions.ISettingsStore>()));
        services.AddSingleton<Transcription.Nemotron.NemotronFp16Model>();
        services.AddSingleton<Transcription.Nemotron.NemotronModelInstaller>();
        services.AddSingleton<ModelDownload>();   // shared model-download state (wizard + settings)
        services.AddSingleton<RetentionCleaner>();
        services.AddSingleton<UsageStats>();
        services.AddSingleton<HotkeyManager>();
        // Nemotron 3.5 (streaming RNNT). int4 export is CPU-only (no DirectML kernels); FP16 export runs
        // on DirectML. Use FP16/DML when the user picks GPU and the FP16 model is installed; otherwise
        // int4/CPU (default, correct everywhere). Backend read once at construction.
        services.AddSingleton<ITranscriber>(sp =>
        {
            string? device = sp.GetRequiredService<ISettingsStore>().Current.TranscriptionDevice;
            var fp16Model = sp.GetRequiredService<Transcription.Nemotron.NemotronFp16Model>();
            bool wantsGpu = device is not null && device.Contains("GPU", StringComparison.OrdinalIgnoreCase);
            if (wantsGpu && fp16Model.IsInstalled)
                return new Transcription.Nemotron.NemotronFp16Transcriber(
                    fp16Model,
                    sp.GetRequiredService<Transcription.Onnx.OnnxSessionFactory>(),
                    Transcription.Onnx.ComputeBackend.DirectML);
            return new Transcription.Nemotron.NemotronTranscriber(
                sp.GetRequiredService<Transcription.Nemotron.NemotronModel>(),
                sp.GetRequiredService<Transcription.Onnx.OnnxSessionFactory>(),
                ParseBackend(device));
        });
        services.AddSingleton<RecorderController>();
        services.AddSingleton<Rewrite.RewriteController>();
        services.AddSingleton<Import.FfmpegInstaller>();
        services.AddSingleton<Import.MediaImporter>();
        services.AddSingleton<PillController>();

        services.AddSingleton<IRecordingStore, JsonRecordingStore>();
        services.AddSingleton<Navigator>();
        services.AddSingleton<INavigator>(sp => sp.GetRequiredService<Navigator>());
        services.AddSingleton<RecentsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AskJotViewModel>();
        services.AddSingleton<PromptCatalog>();
        services.AddSingleton<PromptsViewModel>();
        services.AddTransient<PromptPickerViewModel>(); // fresh palette per open; shares the singleton catalog

        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private Forms.ToolStripMenuItem? _toggleItem;
    private Forms.ToolStripMenuItem? _copyLastItem;
    private Forms.ToolStripMenuItem? _recentItem;

    /// <summary>Loads the embedded app icon (WPF resource) at the requested size — used by the tray.</summary>
    private static Drawing.Icon LoadAppIcon(Drawing.Size size)
    {
        // Defensive: a null/failed icon load must never crash startup (it took down the whole app once
        // when a packaging change dropped the embedded pack resource). Fall back to the icon baked into
        // Jot.exe, then to a stock system icon, so the tray always gets *some* icon.
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/jot.ico");
            System.Windows.Resources.StreamResourceInfo? info = System.Windows.Application.GetResourceStream(uri);
            if (info?.Stream is { } stream)
            {
                using (stream) return new Drawing.Icon(stream, size);
            }
        }
        catch (Exception ex) { JotLog.Warn($"LoadAppIcon: embedded icon failed, using fallback ({ex.Message})"); }

        try
        {
            string exe = Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()!.Location;
            if (Drawing.Icon.ExtractAssociatedIcon(exe) is { } fromExe) return fromExe;
        }
        catch { /* fall through to the system icon */ }

        return Drawing.SystemIcons.Application;
    }

    /// <summary>The current toggle-recording shortcut as a display label ("Alt + Space", "Apps", …),
    /// read live from settings — so every surface shows the user's real binding, never a hardcoded one.</summary>
    private string ToggleHotkeyLabel() =>
        Recording.HotkeyChord.Display(Services.GetRequiredService<ISettingsStore>().Current.ToggleRecordingHotkey);

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(Forms.SystemInformation.SmallIconSize),
            Visible = true,
            Text = $"Jot — {ToggleHotkeyLabel()} to dictate",
        };

        var menu = new Forms.ContextMenuStrip();
        _toggleItem = new Forms.ToolStripMenuItem($"Start dictation\t{ToggleHotkeyLabel()}", null, (_, _) => _recorder!.Toggle());
        _copyLastItem = new Forms.ToolStripMenuItem("Copy last transcription", null, (_, _) => CopyLast());
        _recentItem = new Forms.ToolStripMenuItem("Recent transcriptions");

        menu.Items.Add(_toggleItem);
        menu.Items.Add(_copyLastItem);
        menu.Items.Add(_recentItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open Jot…", null, (_, _) => ShowMainWindow());
        // "Check for updates…" hidden until real: no network check yet (canned "up to date").
        // Restore when Velopack auto-update lands. See docs/plans/fixit-worklist.md (B3).
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Jot", null, (_, _) => Shutdown());

        // Rebuild the state-dependent items each time the menu opens, off the live store.
        menu.Opening += (_, _) => RefreshTrayMenu();

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    private void RefreshTrayMenu()
    {
        if (_toggleItem is not null)
            _toggleItem.Text = _recorder!.State == RecorderState.Recording
                ? $"Stop dictation\t{ToggleHotkeyLabel()}"
                : $"Start dictation\t{ToggleHotkeyLabel()}";

        var store = Services.GetRequiredService<IRecordingStore>();
        var completed = store.Items
            .Where(i => i.Status == Models.RecordingStatus.Complete && !string.IsNullOrWhiteSpace(i.Transcript))
            .Take(10).ToList();

        if (_copyLastItem is not null) _copyLastItem.Enabled = completed.Count > 0;

        if (_recentItem is not null)
        {
            _recentItem.DropDownItems.Clear();
            _recentItem.Enabled = completed.Count > 0;
            foreach (Models.RecordingItem item in completed)
            {
                string label = item.Title.Length > 40 ? item.Title[..38] + "…" : item.Title;
                _recentItem.DropDownItems.Add(label, null, (_, _) => CopyText(item.Transcript));
            }
        }
    }

    private void CopyLast()
    {
        var store = Services.GetRequiredService<IRecordingStore>();
        var last = store.Items.FirstOrDefault(
            i => i.Status == Models.RecordingStatus.Complete && !string.IsNullOrWhiteSpace(i.Transcript));
        if (last is not null) CopyText(last.Transcript);
    }

    private static void CopyText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    private void SetupHotkeys()
    {
        var settings = Services.GetRequiredService<ISettingsStore>();
        _hotkeys = Services.GetRequiredService<HotkeyManager>();
        _hotkeys.ToggleRecording += () => _recorder!.Toggle();
        _hotkeys.PasteLast += PasteLastTranscript;
        _hotkeys.Rewrite += () => _rewrite!.BeginRewrite(OpenRewritePicker);       // default prompt, or pick one
        _hotkeys.RewriteWithVoice += () => _rewrite!.ToggleVoiceRewrite();          // speak the instruction
        _hotkeys.RegistrationFailed += (label, reason) =>
            Notify($"Shortcut unavailable: {label}", reason, Forms.ToolTipIcon.Warning);
        _hotkeys.Rebuild();
        _hotkeySignature = HotkeySignature(settings.Current);

        // Rebinding in Settings persists then raises Changed; re-register when a shortcut actually moved.
        settings.Changed += (_, _) => Dispatcher.Invoke(() =>
        {
            string sig = HotkeySignature(settings.Current);
            if (sig == _hotkeySignature) return;
            _hotkeySignature = sig;
            _hotkeys.Rebuild();
        });
    }

    private static string HotkeySignature(JotSettings s) => string.Join("|",
        s.AdvancedFeatures, s.ToggleRecordingHotkey, s.PasteLastHotkey, s.RewriteHotkey, s.RewriteWithVoiceHotkey);

    private void PasteLastTranscript()
    {
        var store = Services.GetRequiredService<IRecordingStore>();
        var last = store.Items.FirstOrDefault(
            i => i.Status == Models.RecordingStatus.Complete && !string.IsNullOrWhiteSpace(i.Transcript));
        if (last is null) return;
        var s = Services.GetRequiredService<ISettingsStore>().Current;
        Delivery.TextInjector.PasteAtCursor(last.Transcript, IntPtr.Zero, s.KeepInClipboard, s.AutoEnter);
    }

    private void OpenRewritePicker()
    {
        // The selection was already captured by BeginRewrite; running a picked prompt rewrites it.
        var vm = Services.GetRequiredService<PromptPickerViewModel>();
        var picker = new Controls.PromptPickerWindow(vm)
        {
            PromptChosen = item => _rewrite!.RunRewrite(item.Body),
        };
        picker.Show();
        picker.Activate();
    }

    private void RunSmokeTest()
    {
        ShowMainWindow();
        var nav = Services.GetRequiredService<INavigator>();
        var store = Services.GetRequiredService<IRecordingStore>();
        var steps = new List<Action>
        {
            () => nav.Navigate(typeof(Views.AskJotPage)),
            () => nav.Navigate(typeof(Views.PromptsPage)),
            () => nav.Navigate(typeof(Views.ShortcutsPage)),
            () => nav.Navigate(typeof(Views.HelpPage)),
            () => nav.Navigate(typeof(Views.AboutPage)),
            () => nav.Navigate(typeof(Views.SettingsPage)),
            () => { if (store.Items.Count > 0) nav.Navigate(typeof(Views.RecordingDetailPage), store.Items[0]); },
            () => nav.Navigate(typeof(Views.RecentsPage)),
        };
        int i = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (i >= steps.Count) { timer.Stop(); return; }
            steps[i++]();
        };
        timer.Start();
    }

    private void ShowWizard()
    {
        var wizard = new Views.SetupWizardWindow();
        wizard.Show();
        wizard.Activate();
    }

    private void ShowMainWindow()
    {
        _mainWindow ??= Services.GetRequiredService<MainWindow>();
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;   // reliably bring to front...
        _mainWindow.Topmost = false;  // ...without pinning it there
        _mainWindow.Focus();
    }

    private void WireRecorderNotifications()
    {
        // The status pill owns success/error/nothing feedback now; the tray just reflects state
        // in its tooltip for a quick hover-glance.
        _recorder!.StateChanged += state =>
        {
            if (_tray is null) return;
            _tray.Text = state switch
            {
                RecorderState.Recording => $"Jot — recording… ({ToggleHotkeyLabel()} to stop)",
                RecorderState.Transcribing => "Jot — transcribing…",
                _ => $"Jot — {ToggleHotkeyLabel()} to dictate",
            };
        };

        // After a dictation, check whether it's time for the one-time "you've saved ~1h" donation nudge.
        _recorder.TranscriptReady += _ => MaybeShowDonationNudge();
    }

    private bool _donationNudgeShown; // once per app session, so an un-dismissed nudge doesn't re-pop each dictation

    // Show the donation nudge the first time time-saved crosses ~1h. Once snoozed ("maybe later") it only
    // re-asks at a much higher bar much later; "don't ask again"/donate makes it terminal. All on-device.
    private void MaybeShowDonationNudge()
    {
        if (_donationNudgeShown) return;
        var s = Services.GetRequiredService<ISettingsStore>().Current;
        if (s.DonationNudgeDone) return;

        double minsSaved = Services.GetRequiredService<UsageStats>().MinutesSaved;
        if (minsSaved < 60) return;                                   // first fire: ~1h saved
        if (s.DonationNudgeSnoozedAt is DateTime snoozed              // snoozed: only re-ask at 5h+ and 30+ days on
            && !(minsSaved >= 300 && (DateTime.UtcNow - snoozed).TotalDays >= 30)) return;

        _donationNudgeShown = true;
        Dispatcher.BeginInvoke(() =>
        {
            try { new Controls.DonationNudgeWindow().Show(); }
            catch (Exception ex) { JotLog.Error("donation nudge failed to show", ex); }
        });
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon)
        => _tray?.ShowBalloonTip(2500, title, message, icon);

    private static void LogCrash(Exception? ex)
        => JotLog.Error("Unhandled exception", ex);

    /// <summary>Called by the Velopack uninstaller: removes Jot's data and launch-at-login entry.
    /// Targets known Jot artifacts rather than nuking the data folder (the save location may be a shared
    /// directory). Best-effort — never throw out of an uninstall hook.</summary>
    private static void WipeAllData()
    {
        // Remove the per-user "launch at login" Run entry so nothing starts after removal.
        try
        {
            using Microsoft.Win32.RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("Jot", throwOnMissingValue: false);
        }
        catch { /* best effort */ }

        // Delete Jot's data under the chosen save location.
        try
        {
            var settings = new JsonSettingsStore();
            JotSettings s = settings.Current;
            string dataDir = JotPaths.DataDir(s);

            // Only recursively remove the named subfolders (recordings/models/logs) if this really is a
            // Jot data dir — guards a user who pointed the save location at a shared folder that happens
            // to contain its own recordings/ etc. The marker: a library.json here, or a "…\Jot" basename.
            bool looksLikeJotDir = System.IO.File.Exists(JotPaths.LibraryFile(s))
                || string.Equals(System.IO.Path.GetFileName(dataDir.TrimEnd('\\', '/')), "Jot",
                                 StringComparison.OrdinalIgnoreCase);

            // Always safe: Jot's own files.
            TryDeleteFile(JotPaths.LibraryFile(s));
            TryDeleteFile(System.IO.Path.Combine(dataDir, "aikey.dat"));
            TryDeleteFile(System.IO.Path.Combine(dataDir, "stats.json"));

            if (looksLikeJotDir)
            {
                TryDeleteDir(JotPaths.RecordingsDir(s));
                TryDeleteDir(JotPaths.ModelsDir(s));
                TryDeleteDir(System.IO.Path.Combine(dataDir, "logs"));
                // Remove the data folder itself only if it's now empty.
                try
                {
                    if (System.IO.Directory.Exists(dataDir) &&
                        !System.IO.Directory.EnumerateFileSystemEntries(dataDir).Any())
                        System.IO.Directory.Delete(dataDir);
                }
                catch { /* leave a non-empty folder in place */ }
            }
        }
        catch { /* best effort */ }

        // Delete the app-config folder in LocalAppData (settings.json + any legacy logs).
        try
        {
            string local = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
            if (System.IO.Directory.Exists(local)) System.IO.Directory.Delete(local, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        // Disposes DI singletons — the recorder (mic) and the transcriber (native ONNX sessions).
        (Services as IDisposable)?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
