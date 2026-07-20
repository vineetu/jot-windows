using System.Windows.Controls;
using Jot.Recording;
using Jot.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class HelpPage : Page
{
    public HelpPage()
    {
        InitializeComponent();
        // Show the user's actual toggle shortcut, not a hardcoded chord. On Loaded (not just the ctor)
        // so a cached/reused page instance always reflects the current binding after a rebind.
        Loaded += (_, _) =>
        {
            var settings = App.Services.GetRequiredService<ISettingsStore>();
            DictateChord.Text = HotkeyChord.Display(settings.Current.ToggleRecordingHotkey);
        };
    }
}
