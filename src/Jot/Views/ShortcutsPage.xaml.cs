using System.Windows.Controls;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

/// <summary>
/// Jot's keyboard-shortcuts page, promoted to its own left-nav entry. Every row is an editable
/// <see cref="Jot.Controls.HotkeyBox"/> (click-to-capture): recording (toggle / stop &amp; save) plus the
/// AI &amp; text shortcuts (rewrite, rewrite-with-voice, paste-last). Binds to the shared
/// <see cref="SettingsViewModel"/>, whose chord setters persist and let App re-register on the
/// settings-changed signal. Physical-keyboard round-trip still wants a hands-on pass (fixit A5).
/// </summary>
public partial class ShortcutsPage : Page
{
    public ShortcutsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
