using System.Windows;
using Jot.ViewModels;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// The right-click "Add to Vocabulary…" modal (ux §2.7) — the flagship discovery gesture, because it
/// is the only entry point that appears at the moment the user is annoyed.
///
/// Deliberately dumb: it collects two strings and reports a status line. Everything that HAPPENS on
/// Add (fix this transcript, create the term, record the +1) belongs to
/// <see cref="RecordingDetailViewModel"/>, so the three effects can be tested without a window.
///
/// Modal mechanics copied from <see cref="PromptPickerWindow"/>: initial focus on the "Spell it as"
/// field (the one the user has an opinion about), Esc cancels, Enter is the default button.
/// </summary>
public partial class AddToVocabularyWindow : FluentWindow
{
    /// <summary>What the transcriber wrote — pre-filled from the selection.</summary>
    public string Heard => HeardBox.Text;

    /// <summary>How the user wants it spelled.</summary>
    public string Term => TermBox.Text;

    public AddToVocabularyWindow(string heard, string status, bool canAdd)
    {
        InitializeComponent();
        HeardBox.Text = heard;
        TermBox.Text = heard;
        StatusText.Text = status;
        AddButton.IsEnabled = canAdd;
        Loaded += (_, _) => { TermBox.Focus(); TermBox.SelectAll(); };
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
