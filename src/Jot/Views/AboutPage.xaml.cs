using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Jot.Views;

public partial class AboutPage : Page
{
    public AboutPage() => InitializeComponent();

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
        => System.Windows.MessageBox.Show(
            "You're on the latest preview build.", "Check for updates",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnDonate(object sender, RoutedEventArgs e) => OpenUrl("https://jot-transcribe.com/donations/");

    private void OnSendFeedback(object sender, RoutedEventArgs e)
        => OpenUrl("mailto:jottranscribe@gmail.com?subject=Jot%20for%20Windows%20feedback");

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
