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
            "Reset preferences and shortcut bindings to defaults? Your recordings and models are kept. Jot will restart.",
            "Reset settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        App.Services.GetRequiredService<Services.Abstractions.ISettingsStore>().Reset();
        Restart();
    }

    private void OnEraseData(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Erase all recordings, transcripts, the downloaded model, and settings? This can't be undone. Jot will restart.",
            "Erase all data", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        var settings = App.Services.GetRequiredService<Services.Abstractions.ISettingsStore>();
        App.Services.GetRequiredService<IRecordingStore>().Items.Clear(); // writes an empty library

        // Delete user data: recordings, library, prompts, model, encrypted key, and settings.
        var s = settings.Current;
        TryDeleteDir(Services.JotPaths.RecordingsDir(s));
        TryDeleteDir(Services.JotPaths.ModelsDir(s));
        TryDeleteFile(Services.JotPaths.LibraryFile(s));
        string appData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");
        TryDeleteFile(System.IO.Path.Combine(appData, "prompts.json"));
        TryDeleteFile(System.IO.Path.Combine(appData, "aikey.dat"));
        TryDeleteFile(System.IO.Path.Combine(appData, "settings.json")); // last: back to first-run defaults
        Restart();
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); } catch { }
    }

    private static void TryDeleteFile(string file)
    {
        try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch { }
    }

    private static void Restart()
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null) System.Diagnostics.Process.Start(exe);
        System.Windows.Application.Current.Shutdown();
    }
}
