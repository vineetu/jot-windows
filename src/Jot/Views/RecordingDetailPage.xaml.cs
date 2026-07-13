using System.Windows.Controls;
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
}
