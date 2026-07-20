using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Jot.Controls;
using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.Views;
using Wpf.Ui.Controls;

namespace Jot.Shell;

/// <summary>
/// The single main window: a Fluent NavigationView shell. Owns theme init, Alt+←/→ back/forward, and
/// the "close hides to tray" contract (app only quits from tray Quit; <c>ShutdownMode=OnExplicitShutdown</c>).
/// Scrolling is handled here once for every page — see <see cref="NavContentHost"/> for the bounded-height
/// mechanism; keyboard paging is driven below and wheel-over-text by <see cref="PageScrolling"/>.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private FrameworkElement? _currentPageRoot;

    public MainWindow(IThemeService theme, Navigator navigator)
    {
        InitializeComponent();
        navigator.View = RootNavigation; // view-models navigate through this
        theme.Initialize(this);
        RootNavigation.Navigated += OnNavigated;
        Loaded += (_, _) => RootNavigation.Navigate(typeof(RecentsPage));
    }

    // Cap each page root's height to the NavigationView so its scroller gets a bounded viewport
    // (WPF-UI's infinite-height measure quirk — see NavContentHost).
    private void OnNavigated(NavigationView sender, NavigatedEventArgs e)
    {
        _currentPageRoot = (e.Page as Page)?.Content as FrameworkElement;
        if (_currentPageRoot is not null)
            NavContentHost.SetFillHeight(_currentPageRoot, true);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Alt+Left / Alt+Right: back/forward through nav history.
        if (e.KeyboardDevice.Modifiers == ModifierKeys.Alt)
        {
            if (e.SystemKey == Key.Left) { RootNavigation.GoBack(); e.Handled = true; }
            else if (e.SystemKey == Key.Right) { RootNavigation.GoForward(); e.Handled = true; }
        }

        // PageUp/PageDown/Ctrl+Home/Ctrl+End scrolling. On bubble, so a focused control that wants these
        // keys (text box caret, a list's own paging) handles them first and we leave it alone.
        if (!e.Handled && Keyboard.FocusedElement is not TextBoxBase
            && PageScrolling.FindScrollViewer(_currentPageRoot) is ScrollViewer sv)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (e.Key)
            {
                case Key.PageDown: sv.PageDown(); e.Handled = true; break;
                case Key.PageUp: sv.PageUp(); e.Handled = true; break;
                case Key.Home when ctrl: sv.ScrollToHome(); e.Handled = true; break;
                case Key.End when ctrl: sv.ScrollToEnd(); e.Handled = true; break;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true; // hide to tray instead of quitting
        Hide();
        base.OnClosing(e);
    }
}
