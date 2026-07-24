using System.Windows.Controls;
using Jot.Recording;
using Jot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class HelpPage : Page
{
    public HelpPage()
    {
        InitializeComponent();
        // Show the user's actual toggle shortcut, not a hardcoded chord. On Loaded (not just the ctor)
        // so a cached/reused page instance always reflects the current binding after a rebind.
        Loaded += (_, _) =>
        {
            var settings = App.Services.GetRequiredService<ISettingsStore>();
            DictateChord.Text = HotkeyChord.Display(settings.Current.ToggleRecordingHotkey);
        };
    }

    // Re-open the first-run quick tour on demand — doesn't reset the one-time flag, just shows it again.
    private void OnShowTour(object sender, System.Windows.RoutedEventArgs e) => new Controls.QuickTourWindow().Show();

    // Diagnostics pre-checked here: someone reaching for feedback from Help usually has a problem worth
    // the report (they can untick it — and the full text is previewed either way).
    private void OnSendFeedback(object sender, System.Windows.RoutedEventArgs e)
        => new Controls.FeedbackWindow(attachDiagnostics: true).Show();
}
