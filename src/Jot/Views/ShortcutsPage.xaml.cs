using System.Windows.Controls;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

/// <summary>
/// Keyboard-shortcuts page (own left-nav entry). Each row is an editable click-to-capture
/// <see cref="Jot.Controls.HotkeyBox"/>. Binds to the shared <see cref="SettingsViewModel"/>, whose
/// chord setters persist and let App re-register on the settings-changed signal.
/// </summary>
public partial class ShortcutsPage : Page
{
    public ShortcutsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
