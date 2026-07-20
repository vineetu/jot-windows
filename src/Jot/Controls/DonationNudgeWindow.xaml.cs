using System.Windows;
using Jot.Services;
using Jot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// One-time "you've saved ~1h — pass it forward" prompt, shown once time saved crosses the threshold
/// (see App.MaybeShowDonationNudge). "Donate to charity" opens the full <see cref="DonationsWindow"/>;
/// "Maybe later" snoozes it; "Don't ask again" silences it forever. State lives in settings.
/// </summary>
public partial class DonationNudgeWindow : FluentWindow
{
    private readonly ISettingsStore _settings;

    public DonationNudgeWindow()
    {
        InitializeComponent();
        _settings = App.Services.GetRequiredService<ISettingsStore>();

        double mins = App.Services.GetRequiredService<UsageStats>().MinutesSaved;
        string dur = mins >= 60
            ? $"about {(mins / 60.0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}h"
            : $"about {mins.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} min";
        HeadlineText.Text = $"Jot has saved you {dur} so far.";
    }

    private void OnDonate(object sender, RoutedEventArgs e)
    {
        MarkDone(); // they engaged — don't nudge again
        Close();
        new DonationsWindow().Show();
    }

    private void OnMaybeLater(object sender, RoutedEventArgs e)
    {
        _settings.Current.DonationNudgeSnoozedAt = System.DateTime.UtcNow;
        _settings.Save();
        Close();
    }

    private void OnDontAsk(object sender, RoutedEventArgs e)
    {
        MarkDone();
        Close();
    }

    private void MarkDone()
    {
        _settings.Current.DonationNudgeDone = true;
        _settings.Save();
    }
}
