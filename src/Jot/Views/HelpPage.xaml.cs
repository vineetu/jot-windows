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

    // The Tours hub: each opens its per-feature tour on demand (re-runnable; showing marks it seen).
    private void OnTourShortcuts(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.Shortcuts);
    private void OnTourAi(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.Ai);
    private void OnTourRewrite(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.Rewrite);
    private void OnTourImport(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.Import);
    private void OnTourVocabulary(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.Vocabulary);
    private void OnTourAddToVocabulary(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.AddToVocabulary);
    private void OnTourFeedback(object sender, System.Windows.RoutedEventArgs e) => ShowTour(Controls.TourCatalog.Feedback);

    private static void ShowTour(Controls.Tour tour) => new Controls.QuickTourWindow(tour).Show();

    // Diagnostics pre-checked here: someone reaching for feedback from Help usually has a problem worth
    // the report (they can untick it — and the full text is previewed either way).
    private void OnSendFeedback(object sender, System.Windows.RoutedEventArgs e)
        => new Controls.FeedbackWindow(attachDiagnostics: true).Show();
}
