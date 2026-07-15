using System.Diagnostics;
using System.Linq;
using System.Windows;
using Jot.Services;
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

    public DonationsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        Spinner.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;

        DonationsSummary? summary = await _service.FetchSummaryAsync();

        Spinner.Visibility = Visibility.Collapsed;

        if (summary is null)
        {
            ErrorText.Text = "Couldn't reach the donations service. Check your connection, or use " +
                             "\"Browse all on the web\" below.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        SummaryText.Text = summary.TotalDonations > 0
            ? $"${summary.TotalRaisedUsd:N0} raised for charity across {summary.TotalDonations:N0} donations."
            : "Be the first to donate — every dollar goes to charity.";

        // Show charities that have a fundraiser link, biggest supporters first, then alphabetical.
        CharityList.ItemsSource = summary.PerCharity
            .Where(c => !string.IsNullOrWhiteSpace(c.FundraiserUrl))
            .OrderByDescending(c => c.TotalRaisedUsd)
            .ThenBy(c => c.Name, System.StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void OnDonateCharity(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrWhiteSpace(url))
            OpenUrl(url);
    }

    private void OnBrowseAll(object sender, RoutedEventArgs e) => OpenUrl(DonationsPageUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler */ }
    }
}
