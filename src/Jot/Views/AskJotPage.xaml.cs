using System.Windows.Controls;
using Jot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Jot.Views;

public partial class AskJotPage : Page
{
    public AskJotPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<AskJotViewModel>();
    }
}
