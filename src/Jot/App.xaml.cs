using System.IO;
using System.Windows;
using Jot.Delivery;
using Jot.Recording;
using Jot.Transcription;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Jot;

/// <summary>
/// Application entry point and top-level orchestrator (the Mac app's "App" layer).
/// Owns the tray icon, the global hotkey, and the record → transcribe → paste
/// state machine. There is no main window: Jot lives in the tray.
/// </summary>
public partial class App : System.Windows.Application
{
    private enum State { Idle, Recording, Transcribing }

    private Forms.NotifyIcon? _tray;
    private GlobalHotkey? _hotkey;
    private readonly AudioRecorder _recorder = new();
    private readonly ITranscriber _transcriber = new StubTranscriber();
    private State _state = State.Idle;

    private static readonly string RecordingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot", "recordings");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _tray = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Jot — Alt+Space to dictate",
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Start / Stop dictation\tAlt+Space", null, (_, _) => OnHotkey());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Jot", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OnHotkey();

        try
        {
            // Alt+Space (VK_SPACE = 0x20), matching the Mac default hotkey.
            _hotkey = new GlobalHotkey(GlobalHotkey.Modifiers.Alt, 0x20);
            _hotkey.Pressed += OnHotkey;
        }
        catch (Exception ex)
        {
            Notify("Hotkey unavailable", ex.Message, Forms.ToolTipIcon.Warning);
        }

        Notify("Jot is running", "Press Alt+Space to start and stop dictation.", Forms.ToolTipIcon.Info);
    }

    private async void OnHotkey()
    {
        switch (_state)
        {
            case State.Idle:
                StartRecording();
                break;
            case State.Recording:
                await StopAndDeliverAsync();
                break;
            case State.Transcribing:
                break; // busy; ignore
        }
    }

    private void StartRecording()
    {
        try
        {
            _recorder.Start();
            SetState(State.Recording, "Jot — recording… (Alt+Space to stop)");
        }
        catch (Exception ex)
        {
            Notify("Couldn't start recording", ex.Message, Forms.ToolTipIcon.Error);
        }
    }

    private async Task StopAndDeliverAsync()
    {
        SetState(State.Transcribing, "Jot — transcribing…");
        try
        {
            string wav = Path.Combine(RecordingsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}.wav");

            // Stop + resample + transcribe off the UI thread; resume here for the paste.
            RecordingResult result = await Task.Run(() => _recorder.Stop(wav));
            string text = await _transcriber.TranscribeAsync(result.Samples, result.SampleRate);

            if (string.IsNullOrWhiteSpace(text))
                Notify("Nothing transcribed", "No speech was detected.", Forms.ToolTipIcon.Warning);
            else
                TextInjector.PasteAtCursor(text); // back on the UI (STA) thread after the awaits
        }
        catch (Exception ex)
        {
            Notify("Transcription failed", ex.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            SetState(State.Idle, "Jot — Alt+Space to dictate");
        }
    }

    private void SetState(State state, string tooltip)
    {
        _state = state;
        if (_tray is not null) _tray.Text = tooltip;
    }

    private void Notify(string title, string message, Forms.ToolTipIcon icon)
    {
        _tray?.ShowBalloonTip(2500, title, message, icon);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _recorder.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnExit(e);
    }
}
