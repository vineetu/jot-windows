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
            var vm = new RecordingDetailViewModel(item, store, nav,
                App.Services.GetRequiredService<VocabularyServices>());
            // The "flash" after a review pick is the WPF selection highlight — a read-only TextBox
            // cannot render styled runs at all, and Select() scrolls the span into view for free.
            if (vm.Review is not null) vm.Review.FlashRequested += FlashTranscript;
            DataContext = vm;
        }
    }

    private void FlashTranscript(int start, int length)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (start < 0 || start + length > TranscriptBox.Text.Length) return;
            TranscriptBox.Focus();
            TranscriptBox.Select(start, length);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // The menu item stays PRESENT and goes disabled when the selection isn't a plausible term — a
    // missing item reads as a bug, a disabled one reads as a rule.
    private void OnTranscriptMenuOpened(object sender, RoutedEventArgs e)
    {
        AddToVocabularyItem.IsEnabled =
            DataContext is RecordingDetailViewModel vm && vm.CanAddToVocabulary(TranscriptBox.SelectedText);
    }

    // Selection offsets live on the control, not the VM, so this hop is code-behind by necessity.
    private void OnAddToVocabulary(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RecordingDetailViewModel vm) return;

        int start = TranscriptBox.SelectionStart;
        int length = TranscriptBox.SelectionLength;
        string heard = TranscriptBox.SelectedText;
        if (!vm.CanAddToVocabulary(heard)) return;

        var dialog = new Jot.Controls.AddToVocabularyWindow(
            Jot.Vocabulary.VocabularyStore.SanitizeTerm(heard), vm.AddToVocabularyStatus(), canAdd: true)
        {
            Owner = System.Windows.Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true) return;

        vm.AddToVocabulary(start, length, dialog.Heard, dialog.Term);
    }

    // Open the overflow ("…") button's ContextMenu on left-click too, anchored under it, so it acts
    // like a real menu button rather than right-click only.
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
