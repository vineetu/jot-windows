using System.Threading;
using System.Windows;
using Jot.Recording;
using Jot.Services;
using Jot.Services.Abstractions;
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
    private GlobalHotkey? _hotkey;
    private RecorderController? _recorder;
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

        if (!ClaimSingleInstance())
        {
            _showEvent?.Set(); // nudge the running instance to surface, then bow out
            Shutdown();
            return;
        }

        Services = BuildServices();
        _recorder = Services.GetRequiredService<RecorderController>();
        Services.GetRequiredService<PillController>().Attach(); // status pill now owns pipeline feedback

        // Ensure the on-device speech model is present (downloaded on first run), then warm it up
        // off the UI thread so the first dictation isn't a cold start.
        if (Services.GetRequiredService<ITranscriber>() is ParakeetTranscriber parakeet)
            _ = EnsureSpeechModelReadyAsync(parakeet);

        WireRecorderNotifications();
        SetupTray();
        SetupHotkey();

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

    private static void RunHeadlessInstall(string dir)
    {
        string outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jot-install-result.txt");
        try
        {
            var model = new ParakeetModel(dir);
            var installer = new ParakeetModelInstaller(model);
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
        var pill = new Controls.PillWindow();
        pill.SetState(Controls.PillState.Recording); // anchors bottom-center on the active monitor

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
            pill.SetElapsed($"0:{frames / 30:00}");
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
        services.AddSingleton<AudioRecorder>();
        services.AddSingleton<Transcription.Onnx.OnnxSessionFactory>();
        services.AddSingleton<ParakeetModel>();
        services.AddSingleton<ParakeetModelInstaller>();
        // The encoder execution provider comes from settings (default CPU — correct everywhere).
        // Read once at construction; a device change applies on the next launch.
        services.AddSingleton<ITranscriber>(sp => new ParakeetTranscriber(
            sp.GetRequiredService<ParakeetModel>(),
            sp.GetRequiredService<Transcription.Onnx.OnnxSessionFactory>(),
            ParseBackend(sp.GetRequiredService<ISettingsStore>().Current.TranscriptionDevice)));
        services.AddSingleton<RecorderController>();
        services.AddSingleton<PillController>();

        // Library + navigation + view-models
        services.AddSingleton<IRecordingStore, MockRecordingStore>();
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

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
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

    private void SetupHotkey()
    {
        try
        {
            _hotkey = new GlobalHotkey(GlobalHotkey.Modifiers.Alt, 0x20); // Alt+Space, matching the Mac default
            _hotkey.Pressed += () => _recorder!.Toggle();
        }
        catch (Exception ex)
        {
            Notify("Hotkey unavailable", ex.Message, Forms.ToolTipIcon.Warning);
        }
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
        _hotkey?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        // Disposes DI singletons — the recorder (mic) and the transcriber (native ONNX sessions).
        (Services as IDisposable)?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
