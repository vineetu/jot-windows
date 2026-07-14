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
    /// Default data folder. To keep the system drive free, this picks the fixed drive with the most
    /// free space that ISN'T the system drive (e.g. D:), falling back to %LOCALAPPDATA% when no such
    /// roomy drive exists (so it stays portable on single-drive PCs).
    /// </summary>
    public static string DefaultDataDir
    {
        get
        {
            try
            {
                string? systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
                DriveInfo? best = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady
                        && !string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.AvailableFreeSpace)
                    .FirstOrDefault();
                if (best is not null && best.AvailableFreeSpace > 5L * 1024 * 1024 * 1024) // >5 GB free
                    return Path.Combine(best.RootDirectory.FullName, "Jot");
            }
            catch { /* fall through to LocalAppData */ }
            return LocalAppDataDir;
        }
    }

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
