using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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
        // WPF-UI's NavigationView measures hosted pages with infinite height, so a page-level
        // ScrollViewer never scrolls. Cap it to the NavigationView's (bounded) height so it does.
        Loaded += (_, _) =>
        {
            DependencyObject? d = this;
            while (d is not null and not Wpf.Ui.Controls.NavigationView)
                d = VisualTreeHelper.GetParent(d);
            if (d is FrameworkElement host)
                RootScroll.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = host });
        };
    }

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        // WPF password controls don't support data binding (by design), so push the value manually.
        if (DataContext is SettingsViewModel vm && sender is Wpf.Ui.Controls.PasswordBox box)
            vm.AiApiKey = box.Password;
    }

    private void OnRunWizard(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
    }

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
