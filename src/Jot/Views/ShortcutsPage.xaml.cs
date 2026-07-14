using System.Windows.Controls;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

/// <summary>
/// Read-only view of Jot's keyboard shortcuts, promoted to its own left-nav page. Rebinding is
/// intentionally not offered yet — click-to-capture doesn't work reliably on Windows 11
/// (tracked in docs/plans/fixit-worklist.md, A5). Binds to the shared <see cref="SettingsViewModel"/>
/// so the displayed chords reflect whatever is currently configured.
/// </summary>
public partial class ShortcutsPage : Page
{
    public ShortcutsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
