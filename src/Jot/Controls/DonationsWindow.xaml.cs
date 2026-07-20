using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using Jot.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// In-app "Donate to charity" popup (worklist D1). Fetches the live donations summary from
/// <see cref="DonationsService"/> and lists the charities (each links out to its Every.org
/// fundraiser); the actual donating happens on Every.org, so Jot never touches money or PII.
/// </summary>
public partial class DonationsWindow : FluentWindow
{
    private const string DonationsPageUrl = "https://jot-transcribe.com/donations/";
    private readonly DonationsService _service = new();

    // The loaded charities, kept so the search box can filter without re-fetching.
    private List<DonationCharity> _all = new();

    public DonationsWindow()
    {
        InitializeComponent();
        ShowTimeSaved();
        Loaded += async (_, _) => await LoadAsync();
    }

    // Headline: the on-device "time saved" figure (same as About), in the Mac's "about 90h so far"
    // phrasing. Falls back to "Jot is free…" before there's any usage to report.
    private void ShowTimeSaved()
    {
        var stats = App.Services.GetRequiredService<UsageStats>();
        if (stats.TotalDictations == 0) { TimeSavedText.Text = "Jot is free, and always will be."; return; }
        double mins = stats.MinutesSaved;
        string dur = mins >= 60
            ? $"about {(mins / 60.0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}h"
            : $"about {mins.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} min";
        TimeSavedText.Text = $"Jot has saved you {dur} so far.";
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        Spinner.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;

        DonationsSummary? summary;
        try
        {
            summary = await _service.FetchSummaryAsync();
        }
        catch
        {
            summary = null; // FetchSummaryAsync already swallows, but never let the async-void Loaded throw
        }

        Spinner.Visibility = Visibility.Collapsed;

        if (summary is null)
        {
            ErrorText.Text = "Couldn't reach the donations service. Check your connection, or use " +
                             "\"Browse all on the web\" below.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // Invariant culture for the amount so a non-US locale doesn't render "$1.234" with a $ sign.
        string amount = summary.TotalRaisedUsd.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        string count = summary.TotalDonations.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        CharityLine.Text = summary.TotalDonations > 0
            ? $"100% of donations go to charity via Every.org — ${amount} raised across {count} donations so far."
            : "100% of donations go to charity via Every.org — be the first to donate.";

        // Show charities that have a fundraiser link, biggest supporters first, then alphabetical.
        // per_charity can arrive as an explicit null (System.Text.Json overwrites the initializer), so guard.
        _all = (summary.PerCharity ?? new List<DonationCharity>())
            .Where(c => !string.IsNullOrWhiteSpace(c.FundraiserUrl))
            .OrderByDescending(c => c.TotalRaisedUsd)
            .ThenBy(c => c.Name, System.StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        CharityList.ItemsSource = _all;
        SearchBox.Visibility = _all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Filter the loaded charities by name/description as the user types (no re-fetch).
    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        string q = (SearchBox.Text ?? "").Trim();
        CharityList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(c => Contains(c.Name, q) || Contains(c.Description, q)).ToList();
    }

    private static bool Contains(string? s, string q) =>
        !string.IsNullOrEmpty(s) && s.Contains(q, System.StringComparison.OrdinalIgnoreCase);

    // A $2/$10 pill → every.org's pre-filled donate page for that charity (falls back to the fundraiser
    // link if the slug is missing). The actual donating happens on every.org; Jot never touches money.
    private void OnDonateAmount(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string amount, DataContext: DonationCharity c }) return;
        string url = !string.IsNullOrWhiteSpace(c.Slug)
            ? $"https://www.every.org/{c.Slug}/donate?amount={amount}"
            : c.FundraiserUrl ?? "";
        if (!string.IsNullOrWhiteSpace(url)) OpenUrl(url);
    }

    private void OnBrowseAll(object sender, RoutedEventArgs e) => OpenUrl(DonationsPageUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler */ }
    }
}
