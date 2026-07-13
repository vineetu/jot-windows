using Wpf.Ui.Controls;

namespace Jot.Services.Navigation;

/// <summary>
/// Thin wrapper over the shell's <see cref="INavigationView"/> so view-models can navigate without
/// referencing the window. Pages are created by the NavigationView (parameterless), so a one-shot
/// <see cref="Parameter"/> carries runtime data (e.g. the selected recording) to the target page's
/// constructor, which reads it via the DI-resolved singleton.
/// </summary>
public interface INavigator
{
    object? Parameter { get; }
    void Navigate(Type pageType, object? parameter = null);
    void GoBack();
}

public sealed class Navigator : INavigator
{
    public INavigationView? View { get; set; }

    public object? Parameter { get; private set; }

    public void Navigate(Type pageType, object? parameter = null)
    {
        Parameter = parameter;
        View?.Navigate(pageType);
    }

    public void GoBack() => View?.GoBack();
}
