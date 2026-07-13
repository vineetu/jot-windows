using System.Threading;
using System.Windows;
using Jot.Recording;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Shell;
using Jot.Transcription;
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

        if (!ClaimSingleInstance())
        {
            _showEvent?.Set(); // nudge the running instance to surface, then bow out
            Shutdown();
            return;
        }

        Services = BuildServices();
        _recorder = Services.GetRequiredService<RecorderController>();

        WireRecorderNotifications();
        SetupTray();
        SetupHotkey();

        Notify("Jot is running",
            "Press Alt+Space to start and stop dictation. Double-click the tray icon to open Jot.",
            Forms.ToolTipIcon.Info);

        // Dev affordance: `--show` surfaces the main window immediately (Jot normally boots to tray).
        if (e.Args.Contains("--show")) ShowMainWindow();
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

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<AudioRecorder>();
        services.AddSingleton<ITranscriber, StubTranscriber>();
        services.AddSingleton<RecorderController>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Jot — Alt+Space to dictate",
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Start / Stop dictation\tAlt+Space", null, (_, _) => _recorder!.Toggle());
        menu.Items.Add("Open Jot…", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Jot", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
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
        // Phase-1 feedback via tray tooltip + balloons; the Phase-2 status pill replaces this.
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
        _recorder.Failed += (title, message) => Notify(title, message, Forms.ToolTipIcon.Error);
        _recorder.NothingTranscribed += () =>
            Notify("Nothing transcribed", "No speech was detected.", Forms.ToolTipIcon.Warning);
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon)
        => _tray?.ShowBalloonTip(2500, title, message, icon);

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _recorder?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
