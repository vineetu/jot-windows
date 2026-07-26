using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class VocabularyPage : Page
{
    public VocabularyPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<VocabularyViewModel>();

        // The view-model is a SINGLETON, so its rows — and the per-row "Jot can't listen for this"
        // warning baked into them — can be older than the spotter's ability to answer (the checkpoint
        // is an optional download that lands mid-session). Re-check on every arrival; the VM rebuilds
        // only if a warning actually changed.
        Loaded += (_, _) => (DataContext as VocabularyViewModel)?.Revalidate();

        // Bulk paste import (ux §2.5). The Term field is single-line, so a pasted list would land as
        // one absurd term. Intercepting the real PASTE — not watching the Text property — is what
        // keeps typing "a, b" from flipping the form into import mode mid-keystroke.
        System.Windows.DataObject.AddPastingHandler(TermBox, OnTermPaste);
    }

    private void OnTermPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (DataContext is not VocabularyViewModel vm) return;
        if (!e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText, true)) return;
        string pasted = (string)e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText, true);
        if (vm.TryBeginImport(pasted)) e.CancelCommand();   // the VM owns the text now
    }

    // Delete on a selected row removes it — the keyboard equivalent of the row's trash button,
    // undo included (the status bar's Undo).
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (DataContext is VocabularyViewModel vm && TermList.SelectedItem is VocabularyTermRow row)
        {
            vm.DeleteTermCommand.Execute(row);
            e.Handled = true;
        }
    }
}
