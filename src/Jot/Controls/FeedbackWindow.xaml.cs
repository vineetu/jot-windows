using System.Windows;
using System.Windows.Media;
using Jot.Services;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// In-app feedback composer (worklist D3). Posts the message to the feedback API via
/// <see cref="FeedbackClient"/> — no mailto, no email client. Shows the server's own success id or
/// error message (e.g. a rate-limit notice) inline.
/// </summary>
public partial class FeedbackWindow : FluentWindow
{
    private readonly FeedbackClient _client = new();

    public FeedbackWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => FeedbackBox.Focus();
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        string message = FeedbackBox.Text.Trim();
        if (message.Length == 0)
        {
            ShowStatus("Please type a message first.", error: true);
            return;
        }

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
