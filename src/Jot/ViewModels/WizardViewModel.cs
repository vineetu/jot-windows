using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services;
using Jot.Services.Abstractions;
using NAudio.CoreAudioApi;

namespace Jot.ViewModels;

/// <summary>
/// Drives the first-run setup wizard (Welcome → Permissions → Language → Microphone → Shortcut →
/// Save location → Done). Skippable and re-runnable. Completing it marks first-run done, then requests
/// the window close. It intentionally leaves Advanced features off (the basic/advanced split protects
/// first-time users); the user opts into Advanced from Settings.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public const int StepCount = 7;

    private const int LanguageStep = 3;

    private readonly ISettingsStore _store;
    private readonly ModelDownload _download;

    public string[] Languages { get; } =
        ["English", "Spanish", "French", "German", "Italian", "Portuguese", "Japanese"];
    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private string _language = "English";
    [ObservableProperty] private AudioInputDevice? _selectedDevice;
    [ObservableProperty] private string _dataDirectory = "";

    public event Action? CloseRequested;

    public WizardViewModel(ISettingsStore store, ModelDownload download)
    {
        _store = store;
        _download = download;
        _download.Refresh();                       // reflect current on-disk state
        _language = store.Current.Language;
        _dataDirectory = JotPaths.DataDir(store.Current);
        LoadDevices();
        // The primary button doubles as the download trigger + progress readout, so re-evaluate it as the
        // shared download reports progress / finishes.
        _download.PropertyChanged += OnDownloadChanged;
    }

    private void OnDownloadChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(ShowSkip));
        NextCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Unsubscribe from the shared (singleton) download so this wizard VM — and its window — can be
    /// collected once closed. Without this the long-lived ModelDownload pins them for the app's lifetime.</summary>
    public void Detach() => _download.PropertyChanged -= OnDownloadChanged;

    /// <summary>Shared model-download state — the Language step binds its status + progress bar to this.</summary>
    public ModelDownload Download => _download;

    [RelayCommand]
    private void BrowseSaveLocation()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where Jot saves your recordings and transcripts",
            UseDescriptionForTitle = true,
            SelectedPath = DataDirectory,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
            return;
        DataDirectory = dlg.SelectedPath;
        _store.Current.DataDirectory = dlg.SelectedPath;
        _store.Save();
    }

    private void LoadDevices()
    {
        try
        {
            using var mm = new MMDeviceEnumerator();
            foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                InputDevices.Add(new AudioInputDevice(d.ID, d.FriendlyName));
        }
        catch { /* none available */ }
        SelectedDevice = InputDevices.FirstOrDefault(d => d.Id == _store.Current.InputDeviceId)
                         ?? InputDevices.FirstOrDefault();
    }

    public string StepTitle => StepIndex switch
    {
        0 => "Welcome to Jot",
        1 => "Microphone access",
        2 => "Where should Jot store its files?",
        3 => "What language will you speak?",
        4 => "Choose your microphone",
        5 => "Your dictation shortcut",
        _ => "You're all set",
    };

    public string StepSubtitle => StepIndex switch
    {
        0 => "Free, on-device dictation for Windows. Press a key, speak, and your words are pasted at the cursor.",
        1 => "Jot needs permission to use your microphone. Windows has no separate input-monitoring or accessibility prompt.",
        2 => "The speech model (~754 MB) and your recordings are stored here. Pick a drive with room — you can change it later in Settings.",
        3 => "One multilingual on-device model handles every language — pick the one you'll speak most. The next step downloads it here.",
        4 => "Pick the input device to record from. The meter below confirms Jot can hear you.",
        5 => $"Press {HotkeyLabel} anywhere to start and stop dictation. You can change this later in Settings.",
        _ => $"You can re-run this anytime from Settings. Press {HotkeyLabel} to try your first dictation.",
    };

    /// <summary>Toggle shortcut as a display label, so wizard copy never hardcodes the chord.</summary>
    public string HotkeyLabel => Jot.Recording.HotkeyChord.Display(_store.Current.ToggleRecordingHotkey);

    public bool IsFirstStep => StepIndex == 0;
    public bool IsLastStep => StepIndex == StepCount - 1;
    public double Progress => (StepIndex + 1) / (double)StepCount;

    partial void OnStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));
        OnPropertyChanged(nameof(IsFirstStep));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(ShowSkip));
    }

    partial void OnLanguageChanged(string value) { _store.Current.Language = value; _store.Save(); }
    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        _store.Current.InputDeviceId = value?.Id;
        _store.Save();
    }

    /// <summary>Primary-button label: normally "Next"/"Get started", but on the Language step (when the model
    /// isn't present yet) it becomes "Download &amp; continue", then a live "Downloading… N%", so one click both
    /// fetches the model and moves on. Uses the same shared downloader as Settings.</summary>
    public string PrimaryButtonText =>
        _download.IsDownloading ? $"Downloading… {_download.Progress:0}%"
        : IsLastStep ? "Get started"
        : (StepIndex == LanguageStep && !_download.IsInstalled) ? "Download & continue"
        : "Next";

    /// <summary>No Skip anywhere until the model is downloaded — the user can't skip past onboarding without
    /// it. Once it's present, Skip is fine on every step except the last (which has no Skip).</summary>
    public bool ShowSkip => !IsLastStep && _download.IsInstalled;

    private bool CanAdvance() => !_download.IsDownloading;

    [RelayCommand(CanExecute = nameof(CanAdvance))]
    private async Task Next()
    {
        // Gate the Language step on the model being present: download it (with progress shown on the button
        // and bar) on first click, then advance once it's ready. Same ModelDownload the Settings page uses.
        if (StepIndex == LanguageStep && !_download.IsInstalled)
        {
            bool ok = await _download.EnsureAsync();
            if (!ok) return;   // failed — stay on the step; the status line explains, the button retries
        }
        if (IsLastStep) Finish();
        else StepIndex++;
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
    }

    [RelayCommand]
    private void Skip() => Finish();

    [RelayCommand]
    private void OpenMicPrivacy()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
        catch { /* settings URI unavailable */ }
    }

    private void Finish()
    {
        _store.Current.FirstRunComplete = true;
        // Deliberately does NOT flip AdvancedFeatures on — the basic/advanced split exists so a first-time
        // user isn't dropped into a wall of options. Advanced stays off until the user opts in from Settings.
        _store.Save();
        CloseRequested?.Invoke();
    }
}
