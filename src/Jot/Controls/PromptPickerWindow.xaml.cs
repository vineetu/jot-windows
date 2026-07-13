using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Jot.Models;
using Jot.ViewModels;

namespace Jot.Controls;

/// <summary>
/// The rewrite prompt-picker overlay: a keyboard-first command palette (think PowerToys Run) shown
/// at rewrite time. Unlike the status pill it is <em>activatable</em> — it takes keyboard focus so the
/// user can type to filter. Centered on the active window's monitor; Enter commits, Esc / click-away
/// cancels. Sibling surface to <see cref="PillWindow"/>; shares its dark translucent look.
/// </summary>
public partial class PromptPickerWindow : Window
{
    private readonly PromptPickerViewModel _vm;

    /// <summary>Dismiss when focus leaves (command-palette convention). Off for demos/screenshots.</summary>
    public bool CloseOnDeactivate { get; set; } = true;

    public PromptPickerWindow(PromptPickerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.Picked += OnPicked;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (CloseOnDeactivate) Close(); };
        ((System.Collections.Specialized.INotifyCollectionChanged)List.Items).CollectionChanged += (_, _) => UpdateCount();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CenterOnActiveMonitor();
        if (List.Items.Count > 0) List.SelectedIndex = 0;
        UpdateCount();
        Search.Focus(); // land in the search box so the user types immediately
    }

    private void OnPicked(PromptItem item)
    {
        // Real build: hand the instruction to the rewrite pipeline. This phase is UI-only, so just close.
        Close();
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(+1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter:
                _vm.PickCommand.Execute(List.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.P when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _vm.TogglePinCommand.Execute(List.SelectedItem);
                e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    private void Move(int delta)
    {
        int count = List.Items.Count;
        if (count == 0) return;
        int i = List.SelectedIndex < 0 ? 0 : List.SelectedIndex;
        i = Math.Clamp(i + delta, 0, count - 1);
        List.SelectedIndex = i;
        List.ScrollIntoView(List.SelectedItem);
    }

    private void UpdateCount()
    {
        int n = List.Items.Count;
        CountText.Text = n == 1 ? "1 prompt" : $"{n} prompts";
    }

    // ---------------- positioning: centered on the active window's monitor ----------------

    private void CenterOnActiveMonitor()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        IntPtr fg = GetForegroundWindow();
        IntPtr mon = MonitorFromWindow(fg != IntPtr.Zero ? fg : handle, MONITOR_DEFAULTTONEAREST);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double workLeft = mi.rcWork.Left / dpi.DpiScaleX;
        double workTop = mi.rcWork.Top / dpi.DpiScaleY;
        double workRight = mi.rcWork.Right / dpi.DpiScaleX;
        double workBottom = mi.rcWork.Bottom / dpi.DpiScaleY;

        Left = workLeft + ((workRight - workLeft) - Width) / 2.0;
        Top = workTop + ((workBottom - workTop) - Height) * 0.38; // slightly above dead-center reads better
    }

    // Tool window so the palette stays out of Alt+Tab; still activatable (no NOACTIVATE) for typing.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
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
