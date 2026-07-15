using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jot.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        var stats = App.Services.GetRequiredService<UsageStats>();
        StatsText.Text = stats.TotalDictations == 0
            ? "Dictate something and Jot will start tracking how much time you've saved versus typing."
            : $"You've dictated {stats.TotalWords:N0} words across {stats.TotalDictations:N0} recordings — " +
              $"about {stats.MinutesSaved:N0} minutes saved versus typing. Jot is free; if it saves you time, " +
              $"consider donating to charity below.";
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
        => System.Windows.MessageBox.Show(
            "You're on the latest preview build.", "Check for updates",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnDonate(object sender, RoutedEventArgs e)
    {
        var win = new Controls.DonationsWindow { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    private void OnSendFeedback(object sender, RoutedEventArgs e)
    {
        var win = new Controls.FeedbackWindow { Owner = Window.GetWindow(this) };
        win.ShowDialog();
    }

    // Troubleshooting: relaunch the app cleanly (a fresh process, then shut this one down).
    private void OnRestart(object sender, RoutedEventArgs e)
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    private void OnViewLog(object sender, RoutedEventArgs e)
    {
        string log = Jot.Services.JotLog.LogFilePath;
        if (File.Exists(log))
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        else
            System.Windows.MessageBox.Show("No log entries yet.", "View log",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler */ }
    }
}
