using System.ComponentModel;
using Jot.Services.Abstractions;
using Jot.Views;
using Wpf.Ui.Controls;

namespace Jot.Shell;

/// <summary>
/// The single main window: a Fluent NavigationView shell. Owns theme init (Mica on Win11,
/// solid on Win10), Alt+←/→ back/forward, and the "close hides to tray" contract — the app
/// only quits from the tray's Quit command (App uses <c>ShutdownMode=OnExplicitShutdown</c>).
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow(IThemeService theme)
    {
        InitializeComponent();
        theme.Initialize(this);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(RecentsPage));
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // Windows-native back through nav history (Alt+Left).
        if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Alt
            && e.SystemKey == System.Windows.Input.Key.Left)
        {
            RootNavigation.GoBack();
            e.Handled = true;
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
