using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Transcription;
using Jot.Transcription.Nemotron;

namespace Jot.Services;

/// <summary>
/// The one observable driver for the on-device model download, shared by BOTH the setup wizard and the
/// Settings page — so there is a single download path and a single progress/status surface, never two
/// copies to drift. Backed by an <see cref="IModelInstaller"/> (the actual downloader); registered as
/// a singleton so a download started in one place is reflected wherever it's bound. The base class is
/// the required int4 model; <see cref="GpuModelDownload"/> derives for the optional fp16 GPU model.
/// </summary>
public partial class ModelDownload : ObservableObject
{
    public const string InstalledText = "Nemotron 3.5 · Installed";
    public const string NotInstalledText = "Not installed (~754 MB)";

    private readonly IModelInstaller _installer;
    private readonly string _installedText;
    private readonly string _notInstalledText;

    public ModelDownload(NemotronModelInstaller installer)
        : this(installer, InstalledText, NotInstalledText) { }

    protected ModelDownload(IModelInstaller installer, string installedText, string notInstalledText)
    {
        _installer = installer;
        _installedText = installedText;
        _notInstalledText = notInstalledText;
        _isInstalled = installer.IsInstalled;
        _statusText = _isInstalled ? _installedText : _notInstalledText;
    }

    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;      // 0..100, for a bound ProgressBar
    [ObservableProperty] private string _statusText = "";

    /// <summary>Show a "Download" affordance only when it makes sense: not present and not already running.</summary>
    public bool ShowButton => !IsInstalled && !IsDownloading;
    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ShowButton));
    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(ShowButton));

    /// <summary>Re-check disk (e.g. when a screen opens) in case the model appeared or was removed elsewhere.</summary>
    public void Refresh()
    {
        if (IsDownloading) return;
        IsInstalled = _installer.IsInstalled;
        StatusText = IsInstalled ? _installedText : _notInstalledText;
    }

    [RelayCommand]
    private Task Ensure() => EnsureAsync();

    /// <summary>
    /// Downloads the model if it's missing, reporting into the observable state. Idempotent and resumable —
    /// safe to call again to retry. Returns true when the model is present at the end.
    /// </summary>
    public async Task<bool> EnsureAsync()
    {
        if (IsInstalled || IsDownloading) return IsInstalled;
        IsDownloading = true;
        Progress = 0;
        StatusText = _installer.Manifest.DescribeProgress(0);
        try
        {
            var progress = new Progress<double>(f =>
            {
                Progress = f * 100;
                StatusText = _installer.Manifest.DescribeProgress(f);
            });
            await _installer.EnsureInstalledAsync(progress);
            IsInstalled = true;
            StatusText = _installedText;
        }
        catch (Exception ex)
        {
            StatusText = "Download failed — " + ex.Message;
        }
        finally { IsDownloading = false; }
        return IsInstalled;
    }
}
