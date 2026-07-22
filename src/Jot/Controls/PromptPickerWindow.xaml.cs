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

    /// <summary>Invoked with the chosen prompt (and, for needs-input prompts, the typed/spoken detail —
    /// null otherwise) just before the overlay closes — the rewrite pipeline hooks this to run the rewrite.
    /// Null for demos.</summary>
    public Action<PromptItem, string?>? PromptChosen { get; set; }

    public PromptPickerWindow(PromptPickerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _vm.Picked += OnPicked;
        _vm.PropertyChanged += OnVmPropertyChanged; // shift focus when the augment step opens/closes

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (CloseOnDeactivate) Close(); };
        List.PreviewMouseLeftButtonUp += OnListClick; // click a row to commit, mouse parity with Enter
        ((System.Collections.Specialized.INotifyCollectionChanged)List.Items).CollectionChanged += (_, _) => UpdateCount();
    }

    /// <summary>Single-click a prompt row to commit it — the mouse equivalent of pressing Enter.</summary>
    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        // Walk up from whatever was hit to the row container, then commit that row's prompt.
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not System.Windows.Controls.ListViewItem)
            d = VisualTreeHelper.GetParent(d);
        if (d is System.Windows.Controls.ListViewItem { DataContext: PromptItem item })
        {
            List.SelectedItem = item;
            _vm.PickCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CenterOnActiveMonitor();
        if (List.Items.Count > 0) List.SelectedIndex = 0;
        UpdateCount();
        Search.Focus(); // land in the search box so the user types immediately
    }

    private void OnPicked(PromptItem item, string? detail)
    {
        Action<PromptItem, string?>? chosen = PromptChosen;
        PromptChosen = null;            // fire once
        CloseOnDeactivate = false;      // closing steals focus back; don't double-fire via Deactivated
        Close();
        chosen?.Invoke(item, detail);
    }

    // Entering the augment step lands focus in the input field (ready to type); leaving it returns to search.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PromptPickerViewModel.IsAugmenting)) return;
        if (_vm.IsAugmenting)
            // Defer to after the panel's Visibility binding + layout apply — focusing a still-collapsed box
            // silently no-ops (why the caret didn't land in the field, forcing a manual click before typing).
            Dispatcher.BeginInvoke(() => { AugmentBox.Focus(); Keyboard.Focus(AugmentBox); AugmentBox.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
        else Search.Focus();
    }

    // If the palette is dismissed (Esc / click-away) while the augment mic is still recording, stop it —
    // otherwise the recorder would keep running with no one to call StopAsync.
    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_vm.IsListening) _vm.CancelAugmentCommand.Execute(null);
        base.OnClosed(e);
    }

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // In the augment step the palette list is hidden; Enter runs, Esc stops the mic (then backs out on a
        // second press). Other keys fall through to the editable field, whose first keystroke hands the field
        // from the mic to the user (typing-takeover, handled in the VM). No list navigation here.
        if (_vm.IsAugmenting)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    _vm.ConfirmAugmentCommand.Execute(null); // stops the mic first, then runs
                    e.Handled = true;
                    break;
                case Key.Escape:
                    // Listening → stop but stay (review/edit); already stopped → back to the list.
                    if (_vm.IsListening) _vm.StopSpeakingCommand.Execute(null);
                    else _vm.CancelAugmentCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
            base.OnPreviewKeyDown(e);
            return;
        }

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
            case Key.D when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _vm.SetDefaultCommand.Execute(List.SelectedItem);
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

    // positioning: centered on the active window's monitor

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
