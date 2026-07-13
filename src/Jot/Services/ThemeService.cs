using System.Windows;
using Jot.Platform;
using Jot.Services.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Jot.Services;

/// <summary>
/// Applies light/dark plus the window backdrop. On Windows 11 that backdrop is Mica; on
/// Windows 10 (no DWM system-backdrop) we fall back to <c>None</c> and paint the shell with a
/// solid Fluent surface brush so the app never renders as a bare translucent slab. Persists the
/// chosen mode and, in System mode, follows OS theme changes live via <see cref="SystemThemeWatcher"/>.
/// </summary>
public sealed class ThemeService : IThemeService
{
    private readonly ISettingsStore _settings;
    private Window? _window;

    public ThemeService(ISettingsStore settings) => _settings = settings;

    public AppThemeMode Mode => _settings.Current.Theme;

    private static WindowBackdropType Backdrop =>
        OsInfo.SupportsMica ? WindowBackdropType.Mica : WindowBackdropType.None;

    public void ApplyTheme() => Apply(Mode);

    public void Initialize(Window mainWindow)
    {
        _window = mainWindow;
        Apply(Mode);
        if (Mode == AppThemeMode.System)
            SystemThemeWatcher.Watch(mainWindow, Backdrop, true);
    }

    public void SetMode(AppThemeMode mode)
    {
        _settings.Current.Theme = mode;
        _settings.Save();
        Apply(mode);

        if (_window is not null)
        {
            SystemThemeWatcher.UnWatch(_window);
            if (mode == AppThemeMode.System)
                SystemThemeWatcher.Watch(_window, Backdrop, true);
        }
    }

    private void Apply(AppThemeMode mode)
    {
        ApplicationTheme theme = mode switch
        {
            AppThemeMode.Light => ApplicationTheme.Light,
            AppThemeMode.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light
                ? ApplicationTheme.Light
                : ApplicationTheme.Dark,
        };

        ApplicationThemeManager.Apply(theme, Backdrop, true);

        // Win10 has no Mica: give the shell an explicit opaque surface instead of see-through.
        if (!OsInfo.SupportsMica && _window is not null)
            _window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
    }
}
