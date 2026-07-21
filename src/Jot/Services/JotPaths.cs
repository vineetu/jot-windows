using System.IO;
using Jot.Services.Abstractions;

namespace Jot.Services;

/// <summary>
/// Resolves where Jot stores user data. Recordings + the transcript library live under the
/// user-chosen <see cref="JotSettings.DataDirectory"/> (default <c>%LOCALAPPDATA%\Jot</c>); the
/// small app config (settings.json) always stays in the default location.
/// </summary>
public static class JotPaths
{
    private static string LocalAppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jot");

    /// <summary>
    /// Default data folder: the standard Windows per-user location, <c>%LOCALAPPDATA%\Jot</c>. Always
    /// present and writable for the current user — no drive guessing. A previous build auto-picked the
    /// roomiest non-system drive (e.g. D:), which put the model somewhere unpredictable and could land on
    /// a volume that isn't writable on an unfamiliar machine (a Store-cert failure vector). Users who want
    /// a different drive set it explicitly via <see cref="JotSettings.DataDirectory"/> (Settings / wizard).
    /// </summary>
    public static string DefaultDataDir => LocalAppDataDir;

    /// <summary>The effective data folder (user-chosen, or the default).</summary>
    public static string DataDir(JotSettings s) =>
        string.IsNullOrWhiteSpace(s.DataDirectory) ? DefaultDataDir : s.DataDirectory!;

    public static string RecordingsDir(JotSettings s) => Path.Combine(DataDir(s), "recordings");

    public static string LibraryFile(JotSettings s) => Path.Combine(DataDir(s), "library.json");

    /// <summary>Where on-device models live (under the data folder, so they stay off the system drive).</summary>
    public static string ModelsDir(JotSettings s) => Path.Combine(DataDir(s), "models");

    /// <summary>Models dir resolved from the default location (used before settings are available).</summary>
    public static string DefaultModelsDir => Path.Combine(DefaultDataDir, "models");
}
