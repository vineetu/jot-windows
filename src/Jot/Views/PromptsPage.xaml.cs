using System.Windows.Controls;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class PromptsPage : Page
{
    public PromptsPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<PromptsViewModel>();
    }
}
