using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Jot.Services;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;

namespace Jot.Views;

public partial class AboutPage : Page
{
    private const string IPhoneAppStoreUrl = "https://apps.apple.com/us/app/jot-transcribe/id6766447330";

    public AboutPage()
    {
        InitializeComponent();
        var stats = App.Services.GetRequiredService<UsageStats>();
        StatsText.Text = stats.TotalDictations == 0
            ? "Dictate something and Jot will start tracking how much time you've saved versus typing."
            : $"You've dictated {stats.TotalWords:N0} words across {stats.TotalDictations:N0} recordings — " +
              $"about {stats.MinutesSaved:N0} minutes saved versus typing. Jot is free; if it saves you time, " +
              $"consider donating to charity below.";
        ShowIPhoneQr();
    }

    // Render the App Store link as a QR entirely on-device (no network) so the phone camera can scan it.
    private void ShowIPhoneQr()
    {
        try
        {
            using var gen = new QRCodeGenerator();
            QRCodeData data = gen.CreateQrCode(IPhoneAppStoreUrl, QRCodeGenerator.ECCLevel.Q);
            byte[] png = new PngByteQRCode(data).GetGraphic(10);

            var bmp = new BitmapImage();
            using var ms = new MemoryStream(png);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            IPhoneQr.Source = bmp;
        }
        catch (System.Exception ex) { JotLog.Error("iPhone QR generation failed", ex); }
    }

    private void OnCopyIPhoneLink(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(IPhoneAppStoreUrl); } catch { /* clipboard busy */ }
        if (sender is Wpf.Ui.Controls.Button b)
        {
            b.Content = "Copied!";
            var t = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { b.Content = "Copy link"; t.Stop(); };
            t.Start();
        }
    }

    private void OnOpenIPhoneAppStore(object sender, RoutedEventArgs e) => OpenUrl(IPhoneAppStoreUrl);

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

    // Relaunch. App.RestartApp releases the single-instance mutex first so the new process isn't
    // rejected as a duplicate.
    private void OnRestart(object sender, RoutedEventArgs e) => App.RestartApp();

    private void OnViewLog(object sender, RoutedEventArgs e)
    {
        string log = Jot.Services.JotLog.LogFilePath;
        if (File.Exists(log))
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        else
            System.Windows.MessageBox.Show("No log entries yet.", "View log",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenPrivacyPolicy(object sender, RoutedEventArgs e)
        => OpenUrl("https://sites.simple-host.app/jot-transcribe/jot-windows-privacy/");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler */ }
    }
}
