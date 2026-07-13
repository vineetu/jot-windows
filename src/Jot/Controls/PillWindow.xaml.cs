using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jot.Controls;

/// <summary>
/// The floating status pill. Borderless, topmost, and — crucially — <c>WS_EX_NOACTIVATE</c> so
/// clicking or dragging it never steals focus from the app the user is dictating into (which
/// would change the paste target). Anchored bottom-center on the active window's monitor, above
/// the taskbar; the user can drag it (session-only) or click to expand the transcript. States are
/// driven imperatively via <see cref="SetState"/> from <see cref="PillController"/>.
/// </summary>
public partial class PillWindow : Window
{
    private bool _userMoved;
    private bool _dragging;
    private Point _dragStart;

    public PillWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition();
        Capsule.MouseLeftButtonDown += OnCapsuleDown;
        Capsule.MouseMove += OnCapsuleMove;
        Capsule.MouseLeftButtonUp += OnCapsuleUp;
    }

    // ---------------- public API (all called on the UI thread) ----------------

    public void PushLevel(double level) => Wave.PushLevel(level);

    public void SetElapsed(string text) => Elapsed.Text = text;

    public void SetState(PillState state, string? text = null)
    {
        if (state == PillState.Hidden) { HidePill(); return; }

        // Reset per-state visibility, then set the specifics.
        Wave.Visibility = Visibility.Collapsed;
        Wave.Active = false;
        LineText.Visibility = Visibility.Collapsed;
        Elapsed.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case PillState.Recording:
                Dot.Fill = Res("JotRecordingBrush", Color.FromRgb(0xE8, 0x43, 0x3B));
                Wave.LineBrush = Brushes.White;
                Wave.Visibility = Visibility.Visible;
                Wave.Active = true;
                Elapsed.Visibility = Visibility.Visible;
                Collapse();
                AutomationProperties.SetName(this, "Recording");
                break;

            case PillState.Transcribing:
            case PillState.CleaningUp:
            case PillState.Rewriting:
                Dot.Fill = Res("AccentFillColorDefaultBrush", Color.FromRgb(0x4C, 0x8B, 0xF5));
                LineText.Visibility = Visibility.Visible;
                LineText.Text = state switch
                {
                    PillState.Transcribing => "Transcribing…",
                    PillState.CleaningUp => "Cleaning up…",
                    _ => "Rewriting…",
                };
                Collapse();
                AutomationProperties.SetName(this, LineText.Text);
                break;

            case PillState.Success:
                Dot.Fill = Res("JotSuccessBrush", Color.FromRgb(0x3F, 0xB9, 0x50));
                LineText.Visibility = Visibility.Visible;
                LineText.Text = OneLine(text) ?? "Done";
                TranscriptText.Text = text ?? "";
                AutomationProperties.SetName(this, "Transcription ready");
                break;

            case PillState.Notice:
                Dot.Fill = Res("JotWarningBrush", Color.FromRgb(0xD2, 0x99, 0x22));
                LineText.Visibility = Visibility.Visible;
                LineText.Text = text ?? "";
                TranscriptText.Text = text ?? "";
                Collapse();
                AutomationProperties.SetName(this, text ?? "Notice");
                break;

            case PillState.Error:
                Dot.Fill = Res("JotRecordingBrush", Color.FromRgb(0xE8, 0x43, 0x3B));
                LineText.Visibility = Visibility.Visible;
                LineText.Text = OneLine(text) ?? "Something went wrong";
                TranscriptText.Text = text ?? "";
                AutomationProperties.SetName(this, "Error");
                break;
        }

        ShowPill();
    }

    // ---------------- show / hide / position ----------------

    private void ShowPill()
    {
        if (!IsVisible) Show();
        Reposition();
    }

    private void HidePill()
    {
        Wave.Active = false;
        Collapse();
        Hide();
    }

    private void Reposition()
    {
        if (_userMoved || ActualWidth <= 0) return;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        IntPtr fg = GetForegroundWindow();
        IntPtr mon = MonitorFromWindow(fg != IntPtr.Zero ? fg : handle, MONITOR_DEFAULTTONEAREST);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double workLeft = mi.rcWork.Left / dpi.DpiScaleX;
        double workRight = mi.rcWork.Right / dpi.DpiScaleX;
        double workBottom = mi.rcWork.Bottom / dpi.DpiScaleY; // excludes a docked taskbar

        Left = workLeft + ((workRight - workLeft) - ActualWidth) / 2.0;
        Top = workBottom - ActualHeight - 24;
    }

    // ---------------- drag vs. click ----------------

    private void OnCapsuleDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragStart = e.GetPosition(this);
        Capsule.CaptureMouse();
    }

    private void OnCapsuleMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point p = e.GetPosition(this);
        if (!_dragging && (Math.Abs(p.X - _dragStart.X) > 4 || Math.Abs(p.Y - _dragStart.Y) > 4))
        {
            _dragging = true;
            _userMoved = true;      // session-only: stop auto-anchoring once the user grabs it
            Capsule.ReleaseMouseCapture();
            try { DragMove(); } catch { /* mouse already up */ }
        }
    }

    private void OnCapsuleUp(object sender, MouseButtonEventArgs e)
    {
        Capsule.ReleaseMouseCapture();
        if (!_dragging) ToggleExpand(); // a click (not a drag) expands/collapses
    }

    private void ToggleExpand()
    {
        bool canExpand = !string.IsNullOrEmpty(TranscriptText.Text);
        ExpandPanel.Visibility = canExpand && ExpandPanel.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Collapse() => ExpandPanel.Visibility = Visibility.Collapsed;

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(TranscriptText.Text); } catch { /* clipboard busy */ }
    }

    // ---------------- helpers ----------------

    private Brush Res(string key, Color fallback)
        => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ReplaceLineEndings(" ");
        return text.Length > 60 ? text[..57] + "…" : text;
    }

    // ---------------- Win32: no-activate tool window + monitor work area ----------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
