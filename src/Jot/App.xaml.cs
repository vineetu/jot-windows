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
        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) => { LogCrash(ex.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => LogCrash(ex.ExceptionObject as Exception);

        if (e.Args.Contains("--dumpsymbols"))
        {
            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-symbols.txt"),
                Enum.GetNames(typeof(Wpf.Ui.Controls.SymbolRegular)));
            Shutdown();
            return;
        }

        // Dev affordance: `--transcribe <wav> [--dml]` runs the real engine headless and writes the
        // transcript to %TEMP%\jot-transcribe-result.txt. Used for automated end-to-end verification.
        int transcribeArg = Array.IndexOf(e.Args, "--transcribe");
        if (transcribeArg >= 0 && transcribeArg + 1 < e.Args.Length)
        {
            RunHeadlessTranscribe(e.Args[transcribeArg + 1], e.Args.Contains("--dml"));
            Shutdown();
            return;
        }

        // Dev affordance: `--installmodel <dir>` runs the first-run model download into <dir> headless
        // and writes the outcome to %TEMP%\jot-install-result.txt.
        int installArg = Array.IndexOf(e.Args, "--installmodel");
        if (installArg >= 0 && installArg + 1 < e.Args.Length)
        {
            RunHeadlessInstall(e.Args[installArg + 1]);
            Shutdown();
            return;
        }

        // Dev affordance: `--pasteselftest` exercises the REAL paste path (paste into whatever's
        // foreground, as in a live dictation) against a freshly-launched Notepad, then verifies via a
        // clipboard round-trip (select-all + copy). Result → %TEMP%\jot-pasteselftest.txt.
        if (e.Args.Contains("--pasteselftest"))
        {
            RunPasteSelfTest();
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
            "Press Alt+Space to start and stop dictation. Double-click the tray icon to open Jot.",
            Forms.ToolTipIcon.Info);

        // Dev affordance: `--show` surfaces the main window immediately (Jot normally boots to tray).
        if (e.Args.Contains("--show") || e.Args.Contains("--detail") || e.Args.Contains("--settings"))
            ShowMainWindow();
        if (e.Args.Contains("--settings"))
            Dispatcher.BeginInvoke(() => Services.GetRequiredService<INavigator>().Navigate(typeof(Views.SettingsPage)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        // Dev affordance: `--pilldemo` drives the pill with a speech-like envelope (no mic needed).
        if (e.Args.Contains("--pilldemo")) RunPillDemo();
        // Dev affordance: `--pickerdemo` shows the rewrite prompt-picker overlay (stays open for review).
        if (e.Args.Contains("--pickerdemo")) RunPickerDemo();
        // First-run setup wizard: on a normal (no-arg) launch, or forced with `--wizard`.
        bool firstRun = !Services.GetRequiredService<ISettingsStore>().Current.FirstRunComplete;
        if (e.Args.Contains("--wizard") || (e.Args.Length == 0 && firstRun)) ShowWizard();
        // Dev affordance: `--smoketest` constructs every page in turn so XAML-load errors hit crash.log.
        if (e.Args.Contains("--smoketest")) RunSmokeTest();
        // Dev affordance: `--detail` opens the first recording's detail view.
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

    // Runs the dispatcher for a spell so queued input (e.g. a synthetic WM_PASTE) is actually
    // processed — a plain Thread.Sleep would block the message pump and the paste would never land.
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
            // Defeat the foreground lock so an auto-launched process can pull its own window to the
            // front (SPI_SETFOREGROUNDLOCKTIMEOUT=0). Only needed for this headless self-test.
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
                    "Press Alt+Space to start dictating.", Forms.ToolTipIcon.Info));
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

    private void RunPillDemo()
    {
        bool expanded = System.Environment.GetCommandLineArgs().Contains("--expanded");
        var pill = new Controls.PillWindow();
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

    private void RunPickerDemo()
    {
        var vm = Services.GetRequiredService<PromptPickerViewModel>();
        var picker = new Controls.PromptPickerWindow(vm) { CloseOnDeactivate = false };
        picker.Show();
        picker.Activate();
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
        services.AddSingleton<AudioRecorder>();
        services.AddSingleton<Transcription.Onnx.OnnxSessionFactory>();
        services.AddSingleton<ParakeetModel>();
        services.AddSingleton<ParakeetModelInstaller>();
        services.AddSingleton<Transcription.Nemotron.NemotronModel>();
        services.AddSingleton<Transcription.Nemotron.NemotronModelInstaller>();
        services.AddSingleton<RetentionCleaner>();
        services.AddSingleton<HotkeyManager>();
        // Nemotron 3.5 (streaming RNNT) is the engine. The encoder execution provider comes from
        // settings (default CPU — correct everywhere); read once at construction.
        services.AddSingleton<ITranscriber>(sp => new Transcription.Nemotron.NemotronTranscriber(
            sp.GetRequiredService<Transcription.Nemotron.NemotronModel>(),
            sp.GetRequiredService<Transcription.Onnx.OnnxSessionFactory>(),
            ParseBackend(sp.GetRequiredService<ISettingsStore>().Current.TranscriptionDevice)));
        services.AddSingleton<RecorderController>();
        services.AddSingleton<Rewrite.RewriteController>();
        services.AddSingleton<Import.MediaImporter>();
        services.AddSingleton<PillController>();

        // Library + navigation + view-models
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
        var uri = new Uri("pack://application:,,,/Assets/jot.ico");
        using System.IO.Stream stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        return new Drawing.Icon(stream, size);
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = LoadAppIcon(Forms.SystemInformation.SmallIconSize),
            Visible = true,
            Text = "Jot — Alt+Space to dictate",
        };

        var menu = new Forms.ContextMenuStrip();
        _toggleItem = new Forms.ToolStripMenuItem("Start dictation\tAlt+Space", null, (_, _) => _recorder!.Toggle());
        _copyLastItem = new Forms.ToolStripMenuItem("Copy last transcription", null, (_, _) => CopyLast());
        _recentItem = new Forms.ToolStripMenuItem("Recent transcriptions");

        menu.Items.Add(_toggleItem);
        menu.Items.Add(_copyLastItem);
        menu.Items.Add(_recentItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Open Jot…", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Check for updates…", null, (_, _) =>
            Notify("Jot is up to date", "You're on the latest version.", Forms.ToolTipIcon.Info));
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
                ? "Stop dictation\tAlt+Space"
                : "Start dictation\tAlt+Space";

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
                RecorderState.Recording => "Jot — recording… (Alt+Space to stop)",
                RecorderState.Transcribing => "Jot — transcribing…",
                _ => "Jot — Alt+Space to dictate",
            };
        };
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon)
        => _tray?.ShowBalloonTip(2500, title, message, icon);

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:O}  {ex}\n\n");
        }
        catch { /* logging is best-effort */ }
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
