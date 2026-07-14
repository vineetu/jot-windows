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
    private bool _expanded;             // is the transcript panel open?
    private bool _lineWhenCollapsed;    // does the current state have a one-line summary to show while collapsed?
    private Action? _onStop;            // set while a recording is in flight; null hides the Stop button
    private const double RecordingWidth = 460; // fixed pill width while dictating, so streaming text doesn't resize it

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

    /// <summary>
    /// Live-caption update shown while recording: the running transcript's tail on the pill line,
    /// with the full text available on click-to-expand. No-op for empty text so the elapsed timer
    /// stays until the first words arrive.
    /// </summary>
    public void SetLiveText(string? text)
    {
        string full = (text ?? "").Trim();
        if (full.Length == 0) return;

        TranscriptText.Text = full;      // full running transcript, shown in the expand panel
        LineText.Text = FitTail(full);   // newest words that fit the (fixed-width) pill line
        _lineWhenCollapsed = true;
        ApplyExpansion();                // respects whether the user has expanded the pill
        TranscriptScroll.ScrollToEnd();  // keep the newest words in view as the caption streams
        Reposition();
    }

    /// <summary>
    /// Shows/hides the Stop button and wires its action. Called by <see cref="Services.PillController"/>
    /// with a non-null callback while a dictation or voice-rewrite recording is in flight, and null
    /// once it stops being interruptible (transcribing, or nothing recording at all).
    /// </summary>
    public void SetStopAction(Action? onStop)
    {
        _onStop = onStop;
        StopButton.Visibility = onStop is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // Longest word-aligned suffix of the caption that fits the pill line, prefixed with an ellipsis.
    // Captions grow left-to-right, so we keep the tail (newest words) and clip from the front —
    // WPF's TextAlignment can't do this itself (it clips the end once the text overflows).
    private string FitTail(string text)
    {
        text = text.ReplaceLineEndings(" ");
        double avail = (LineText.ActualWidth > 10 ? LineText.ActualWidth : 190) - 6;

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Width(string s) => new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight, typeface, LineText.FontSize, Brushes.White, dpi)
            .WidthIncludingTrailingWhitespace;

        if (Width(text) <= avail) return text;

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string best = "";
        for (int i = words.Length - 1; i >= 0; i--)
        {
            string candidate = "…" + string.Join(' ', words[i..]);
            if (Width(candidate) > avail) break;
            best = candidate;
        }
        if (best.Length == 0) // even the final word overflows — trim it character-by-character
        {
            string last = words.Length > 0 ? words[^1] : text;
            while (last.Length > 1 && Width("…" + last) > avail) last = last[1..];
            best = "…" + last;
        }
        return best;
    }

    /// <summary>Dev/testing hook: open the transcript panel (as a click would) for the pill demo.</summary>
    public void ExpandForDemo()
    {
        if (!_expanded) ToggleExpand();
    }

    /// <summary>Dev/testing hook: pin the pill at a fixed screen point and stop auto-anchoring.</summary>
    public void PinAt(double left, double top)
    {
        _userMoved = true;
        Left = left;
        Top = top;
    }

    public void SetState(PillState state, string? text = null)
    {
        if (state == PillState.Hidden) { HidePill(); return; }

        // Reset per-state visibility, then set the specifics. Line/panel visibility is applied once
        // at the end via ApplyExpansion so the collapsed line and the expand panel never both show.
        Wave.Visibility = Visibility.Collapsed;
        Wave.Active = false;
        Elapsed.Visibility = Visibility.Collapsed;
        Capsule.Width = double.NaN;   // auto-size by default; only Recording pins a fixed width
        _expanded = false;
        _lineWhenCollapsed = false;

        switch (state)
        {
            case PillState.Recording:
                Dot.Fill = Res("JotRecordingBrush", Color.FromRgb(0xE8, 0x43, 0x3B));
                Wave.LineBrush = Brushes.White;
                Wave.Visibility = Visibility.Visible;
                Wave.Active = true;
                Elapsed.Visibility = Visibility.Visible;
                Capsule.Width = RecordingWidth;   // stationary pill; the live caption streams within it
                TranscriptText.Text = ""; // clear any prior session's caption before new partials arrive
                LineText.Text = "";       // no caption yet — just waveform + timer until words arrive
                AutomationProperties.SetName(this, "Recording");
                break;

            case PillState.Transcribing:
            case PillState.CleaningUp:
            case PillState.Rewriting:
                Dot.Fill = Res("AccentFillColorDefaultBrush", Color.FromRgb(0x4C, 0x8B, 0xF5));
                LineText.Text = state switch
                {
                    PillState.Transcribing => "Transcribing…",
                    PillState.CleaningUp => "Cleaning up…",
                    _ => "Rewriting…",
                };
                _lineWhenCollapsed = true;
                AutomationProperties.SetName(this, LineText.Text);
                break;

            case PillState.Success:
                Dot.Fill = Res("JotSuccessBrush", Color.FromRgb(0x3F, 0xB9, 0x50));
                LineText.Text = OneLine(text) ?? "Done";
                TranscriptText.Text = text ?? "";
                _lineWhenCollapsed = true;
                AutomationProperties.SetName(this, "Transcription ready");
                break;

            case PillState.Notice:
                Dot.Fill = Res("JotWarningBrush", Color.FromRgb(0xD2, 0x99, 0x22));
                LineText.Text = text ?? "";
                TranscriptText.Text = text ?? "";
                _lineWhenCollapsed = true;
                AutomationProperties.SetName(this, text ?? "Notice");
                break;

            case PillState.Error:
                Dot.Fill = Res("JotRecordingBrush", Color.FromRgb(0xE8, 0x43, 0x3B));
                LineText.Text = OneLine(text) ?? "Something went wrong";
                TranscriptText.Text = text ?? "";
                _lineWhenCollapsed = true;
                AutomationProperties.SetName(this, "Error");
                break;
        }

        TranscriptScroll.ScrollToHome(); // start at the top for a freshly-entered state (not mid-scroll from a prior one)
        ApplyExpansion();
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

    /// <summary>
    /// Applies the current expand/collapse state so the caption is only ever shown once — mirroring
    /// the macOS pill: expanded → full transcript in the panel with the pill line hidden; collapsed →
    /// the one-line tail on the pill with the panel hidden.
    /// </summary>
    private void ApplyExpansion()
    {
        bool canExpand = !string.IsNullOrEmpty(TranscriptText.Text);
        if (_expanded && canExpand)
        {
            LineText.Visibility = Visibility.Collapsed;   // panel already shows the full text
            ExpandPanel.Visibility = Visibility.Visible;
        }
        else
        {
            _expanded = false;
            LineText.Visibility = _lineWhenCollapsed ? Visibility.Visible : Visibility.Collapsed;
            ExpandPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleExpand()
    {
        if (string.IsNullOrEmpty(TranscriptText.Text)) return; // nothing to expand into
        _expanded = !_expanded;
        ApplyExpansion();
    }

    private void Collapse()
    {
        _expanded = false;
        ApplyExpansion();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(TranscriptText.Text); } catch { /* clipboard busy */ }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _onStop?.Invoke();

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
