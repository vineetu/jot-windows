namespace Jot.Platform;

/// <summary>
/// Windows version gates. The one that matters for the UI: Mica and the DWM
/// system-backdrop APIs are Windows 11 (build 22000+) only, so every surface must
/// look correct on a solid background too — the dev box is Windows 10 (19044) today.
/// </summary>
public static class OsInfo
{
    /// <summary>Windows 11 21H2 or newer (build 22000+).</summary>
    public static bool IsWindows11OrGreater => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Mica / DWMWA_SYSTEMBACKDROP_TYPE are Win11-only; below this we render on a solid Fluent surface.</summary>
    public static bool SupportsMica => IsWindows11OrGreater;
}
