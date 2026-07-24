using System.Windows;
using System.Windows.Media;
using Jot.Services;
using Jot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// In-app feedback composer (worklist D3). Posts the message to the feedback API via
/// <see cref="FeedbackClient"/> — no mailto, no email client. Shows the server's own success id or
/// error message (e.g. a rate-limit notice) inline. Diagnostics (hardware + scrubbed log tail, built by
/// <see cref="FeedbackReport"/>) are OPT-IN with a full preview — the user sees the exact text that
/// leaves the machine. Error/slow-transcription prompts open this with diagnostics pre-checked.
/// </summary>
public partial class FeedbackWindow : FluentWindow
{
    private readonly FeedbackClient _client = new();

    public FeedbackWindow(bool attachDiagnostics = false)
    {
        InitializeComponent();
        Loaded += (_, _) => FeedbackBox.Focus();
        if (attachDiagnostics) AttachDiagnostics.IsChecked = true; // triggers OnAttachToggled → preview
    }

    private void OnAttachToggled(object sender, RoutedEventArgs e)
    {
        bool on = AttachDiagnostics.IsChecked == true;
        if (on) DiagnosticsPreview.Text = BuildReport();
        DiagnosticsPreview.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string BuildReport()
    {
        try
        {
            var settings = App.Services.GetRequiredService<ISettingsStore>();
            return FeedbackReport.Build(settings.Current, userNote: null);
        }
        catch (System.Exception ex)
        {
            return $"(couldn't build diagnostics: {ex.Message})";
        }
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        string message = FeedbackBox.Text.Trim();
        if (message.Length == 0)
        {
            ShowStatus("Please type a message first.", error: true);
            return;
        }
        // Send EXACTLY what's previewed — never rebuild after the user has reviewed it.
        if (AttachDiagnostics.IsChecked == true)
            message = $"{message}\n\n{DiagnosticsPreview.Text}";

        SendButton.IsEnabled = false;
        FeedbackBox.IsEnabled = false;
        ShowStatus("Sending…", error: false);

        try
        {
            await _client.SendAsync(message);
            ShowStatus("Thanks! Your feedback was sent.", error: false);
            SendButton.Visibility = Visibility.Collapsed;
            CloseButton.Content = "Done";
        }
        catch (System.Exception ex)
        {
            // FeedbackException carries the server's own message; anything else is a transport fault.
            ShowStatus(ex is FeedbackException ? ex.Message : "Couldn't send — check your connection and try again.",
                       error: true);
            SendButton.IsEnabled = true;
            FeedbackBox.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.Foreground = error
            ? (Brush?)TryFindResource("SystemFillColorCriticalBrush") ?? Brushes.IndianRed
            : (Brush?)TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
