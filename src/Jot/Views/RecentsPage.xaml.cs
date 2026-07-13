using System.Windows;
using System.Windows.Controls;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class RecentsPage : Page
{
    public RecentsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<RecentsViewModel>();
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an audio or video file",
            Filter = "Audio or video|*.wav;*.mp3;*.m4a;*.mp4;*.mov;*.webm;*.mkv;*.flac;*.aac;*.ogg;*.opus;*.wma;*.avi|All files|*.*",
        };
        if (dlg.ShowDialog() == true)
            await App.Services.GetRequiredService<Import.MediaImporter>().ImportAsync(dlg.FileName);
    }

    private void OnDropZoneDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            DropZone.Opacity = 0.75;
        }
    }

    private void OnDropZoneDragLeave(object sender, System.Windows.DragEventArgs e)
        => DropZone.Opacity = 1.0;

    private async void OnDropZoneDrop(object sender, System.Windows.DragEventArgs e)
    {
        DropZone.Opacity = 1.0;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var importer = App.Services.GetRequiredService<Import.MediaImporter>();
            foreach (string f in files) await importer.ImportAsync(f);
        }
    }
}
