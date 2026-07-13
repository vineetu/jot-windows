using System.Windows;
using System.Windows.Controls;
using Jot.Services.Abstractions;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }

    private void OnRunWizard(object sender, RoutedEventArgs e)
        => System.Windows.MessageBox.Show(
            "The guided setup wizard opens here.", "Setup wizard",
            MessageBoxButton.OK, MessageBoxImage.Information);

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Reset preferences and shortcut bindings? Your recordings and models are kept.",
            "Reset settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        // Stub: a real reset rewrites defaults and relaunches. Preview build no-ops safely.
    }

    private void OnEraseData(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Erase all recordings, downloaded models, and settings? This can't be undone.",
            "Erase all data", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        App.Services.GetRequiredService<IRecordingStore>().Items.Clear();
    }
}
