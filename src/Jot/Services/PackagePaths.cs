using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Jot.Services;

/// <summary>
/// MSIX package identity + the per-package data container. Under the Store (MSIX) build Windows deletes
/// the whole package container on uninstall, so anything Jot stores there is auto-cleaned — the only way
/// to get "uninstall removes everything," since MSIX runs NO app code on uninstall (unlike a classic
/// EXE/MSI uninstaller). Unpackaged (dev / Velopack) runs have no container: everything falls back to the
/// real <c>%LOCALAPPDATA%\Jot</c>.
/// </summary>
public static class PackagePaths
{
    // GetCurrentPackageFullName returns APPMODEL_ERROR_NO_PACKAGE (15700) when the process has NO package
    // identity (unpackaged). Anything else (typically ERROR_INSUFFICIENT_BUFFER, since we pass no buffer)
    // means it's packaged. Raw P/Invoke to avoid a WinRT dependency (matches App's existing check).
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    private const int ERROR_SUCCESS = 0;

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, StringBuilder? packageFamilyName);

    /// <summary>True when running as an MSIX/Store package (identity present).</summary>
    public static bool IsPackaged { get; } = ComputeIsPackaged();

    /// <summary>The package family name (e.g. <c>Vineetsriram.JotTranscribe_xhkaqb0regjwm</c>), or null unpackaged.</summary>
    public static string? PackageFamilyName { get; } = ComputePackageFamilyName();

    /// <summary>
    /// The package's per-user local data container (<c>%LOCALAPPDATA%\Packages\&lt;PFN&gt;\LocalCache</c>) —
    /// the folder Windows removes on uninstall. Null when unpackaged. LocalCache (not LocalState) because
    /// it isn't roamed or backed up: right home for a ~0.75 GB model. A full-trust packaged app still sees
    /// the REAL %LOCALAPPDATA% from GetFolderPath (writes aren't virtualized), so this path is built from it.
    /// </summary>
    public static string? ContainerRoot { get; } = ComputeContainerRoot();

    private static bool ComputeIsPackaged()
    {
        int length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    private static string? ComputePackageFamilyName()
    {
        int length = 0;
        // First call sizes the buffer (returns ERROR_INSUFFICIENT_BUFFER when packaged); NO_PACKAGE = unpackaged.
        int probe = GetCurrentPackageFamilyName(ref length, null);
        if (probe == APPMODEL_ERROR_NO_PACKAGE || length <= 0) return null;
        var sb = new StringBuilder(length);
        return GetCurrentPackageFamilyName(ref length, sb) == ERROR_SUCCESS ? sb.ToString() : null;
    }

    private static string? ComputeContainerRoot()
    {
        if (PackageFamilyName is not { Length: > 0 } pfn) return null;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Packages", pfn, "LocalCache");
    }
}
