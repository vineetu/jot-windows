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

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        // File import decode is stubbed until the STT milestone; picking a file is a no-op for now.
        new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an audio or video file",
            Filter = "Audio or video|*.wav;*.mp3;*.m4a;*.mp4;*.mov;*.webm;*.mkv;*.flac;*.aac|All files|*.*",
        }.ShowDialog();
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

    private void OnDropZoneDrop(object sender, System.Windows.DragEventArgs e)
        => DropZone.Opacity = 1.0; // decoding wired in the STT milestone
}
