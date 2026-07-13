using System.Windows;

namespace Jot.Services.Abstractions;

/// <summary>
/// Owns light/dark application + the Win11 Mica backdrop (with a solid Win10 fallback).
/// Persists the choice through <see cref="ISettingsStore"/> so it survives restart, and
/// follows the OS theme live when <see cref="AppThemeMode.System"/> is selected.
/// </summary>
public interface IThemeService
{
    AppThemeMode Mode { get; }

    /// <summary>Apply light/dark globally, independent of any window. Called at startup so surfaces
    /// shown before the main window (setup wizard, overlays) render in the right theme, not the
    /// App.xaml default.</summary>
    void ApplyTheme();

    /// <summary>Apply the backdrop + initial theme to the shell window and start watching the OS theme.</summary>
    void Initialize(Window mainWindow);

    /// <summary>Switch theme mode, persist it, and re-apply immediately.</summary>
    void SetMode(AppThemeMode mode);
}
