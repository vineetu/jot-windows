using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Jot.Models;
using Jot.Services.Abstractions;
using Jot.Services.Navigation;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class RecordingDetailPage : Page
{
    public RecordingDetailPage()
    {
        InitializeComponent();

        // The selected item is handed over as the navigator's one-shot parameter.
        var nav = App.Services.GetRequiredService<INavigator>();
        if (nav.Parameter is RecordingItem item)
        {
            var store = App.Services.GetRequiredService<IRecordingStore>();
            DataContext = new RecordingDetailViewModel(item, store, nav);
        }
    }

    // The overflow ("…") button previously only responded to right-click (its ContextMenu). Open the
    // same menu on a normal left-click, anchored under the button, so it behaves like a real menu button.
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu is System.Windows.Controls.ContextMenu menu)
        {
            menu.PlacementTarget = fe;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // Ctrl+F opens Find & Replace for a dictation and focuses the Find box. (Wired in code-behind
    // because a KeyBinding's Command doesn't reliably inherit the page DataContext.)
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
            && DataContext is RecordingDetailViewModel { IsDictation: true } vm)
        {
            vm.OpenFindReplaceCommand.Execute(null);
            Dispatcher.BeginInvoke(new Action(() => FindBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
