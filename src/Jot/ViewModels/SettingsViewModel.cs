using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services.Abstractions;
using Jot.Transcription;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Jot.ViewModels;

public sealed record AudioInputDevice(string Id, string Name);

/// <summary>
/// Backs the single Settings page. Wraps <see cref="JotSettings"/> — each property persists on change
/// and applies live side effects (theme switch, launch-at-login registry). Engine/network-bound rows
/// (model download, Test Connection) are stubbed; the toggles and pickers are real and saved.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IThemeService _theme;
    private readonly ParakeetModel _model;
    private readonly ParakeetModelInstaller _installer;
    private JotSettings S => _store.Current;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Jot";

    public Array ThemeModes { get; } = Enum.GetValues(typeof(AppThemeMode));
    public string[] Languages { get; } =
        ["English", "Spanish", "French", "German", "Italian", "Portuguese", "Japanese"];
    public string[] Providers { get; } = ["None", "OpenAI", "Anthropic", "Gemini", "Ollama"];
    public string[] RetentionOptions { get; } = ["Forever", "7 days", "30 days", "90 days"];
    public string[] TranscriptionDevices { get; } = ["CPU", "GPU (DirectML)"];

    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty] private AppThemeMode _themeMode;
    [ObservableProperty] private bool _advancedFeatures;
    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private string _retention = "7 days";
    [ObservableProperty] private bool _semanticSearch;
    [ObservableProperty] private bool _returnToOrigin;
    [ObservableProperty] private AudioInputDevice? _selectedDevice;

    [ObservableProperty] private string _language = "English";
    [ObservableProperty] private string _transcriptionDevice = "CPU";
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _autoEnter;
    [ObservableProperty] private bool _keepInClipboard;

    // On-device model download state (backed by ParakeetModelInstaller)
    [ObservableProperty] private bool _isModelInstalled;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _modelDownloadProgress; // 0..100
    [ObservableProperty] private string _modelStatusText = "";

    public bool ShowDownloadButton => !IsModelInstalled && !IsDownloadingModel;
    partial void OnIsModelInstalledChanged(bool value) => OnPropertyChanged(nameof(ShowDownloadButton));
    partial void OnIsDownloadingModelChanged(bool value) => OnPropertyChanged(nameof(ShowDownloadButton));

    [ObservableProperty] private string _aiProvider = "None";
    [ObservableProperty] private string _aiBaseUrl = "";
    [ObservableProperty] private string _aiModel = "";
    [ObservableProperty] private string _aiApiKey = ""; // in-memory only; a real build uses the Windows Credential Locker / DPAPI
    [ObservableProperty] private bool _cleanupEnabled;
    [ObservableProperty] private string _testConnectionResult = "";

    [ObservableProperty] private bool _soundStart;
    [ObservableProperty] private bool _soundStop;
    [ObservableProperty] private bool _soundCancel;
    [ObservableProperty] private bool _soundSuccess;
    [ObservableProperty] private bool _soundError;

    public bool AiConfigured => AiProvider != "None";

    public SettingsViewModel(ISettingsStore store, IThemeService theme,
        ParakeetModel model, ParakeetModelInstaller installer)
    {
        _store = store;
        _theme = theme;
        _model = model;
        _installer = installer;

        // Seed backing fields directly so wiring the UI doesn't trigger a save storm.
        _themeMode = S.Theme;
        _advancedFeatures = S.AdvancedFeatures;
        _launchAtLogin = S.LaunchAtLogin;
        _retention = DaysToLabel(S.RetentionDays);
        _semanticSearch = S.SemanticSearch;
        _returnToOrigin = S.ReturnToOrigin;
        _language = S.Language;
        _transcriptionDevice = S.TranscriptionDevice;
        _autoPaste = S.AutoPaste;
        _autoEnter = S.AutoEnter;
        _keepInClipboard = S.KeepInClipboard;
        _aiProvider = S.AiProvider;
        _aiBaseUrl = S.AiBaseUrl ?? "";
        _aiModel = S.AiModel ?? "";
        _cleanupEnabled = S.CleanupEnabled;
        _soundStart = S.SoundStart;
        _soundStop = S.SoundStop;
        _soundCancel = S.SoundCancel;
        _soundSuccess = S.SoundSuccess;
        _soundError = S.SoundError;

        LoadDevices();
        RefreshModelStatus();
    }

    private void RefreshModelStatus()
    {
        IsModelInstalled = _model.IsInstalled;
        if (IsModelInstalled)
            ModelStatusText = "Installed";
        else if (!IsDownloadingModel)
            ModelStatusText = "Not installed (~630 MB)";
    }

    private void LoadDevices()
    {
        InputDevices.Clear();
        try
        {
            using var mm = new MMDeviceEnumerator();
            foreach (MMDevice d in mm.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                InputDevices.Add(new AudioInputDevice(d.ID, d.FriendlyName));
        }
        catch { /* no devices / access issue — leave list empty */ }

        SelectedDevice = InputDevices.FirstOrDefault(d => d.Id == S.InputDeviceId)
                         ?? InputDevices.FirstOrDefault();
    }

    // ---- persistence hooks ----

    partial void OnThemeModeChanged(AppThemeMode value) { _theme.SetMode(value); } // SetMode persists
    partial void OnAdvancedFeaturesChanged(bool value) { S.AdvancedFeatures = value; Save(); }
    partial void OnSemanticSearchChanged(bool value) { S.SemanticSearch = value; Save(); }
    partial void OnReturnToOriginChanged(bool value) { S.ReturnToOrigin = value; Save(); }
    partial void OnRetentionChanged(string value) { S.RetentionDays = LabelToDays(value); Save(); }
    partial void OnLanguageChanged(string value) { S.Language = value; Save(); }
    partial void OnTranscriptionDeviceChanged(string value) { S.TranscriptionDevice = value; Save(); }
    partial void OnAutoPasteChanged(bool value) { S.AutoPaste = value; Save(); }
    partial void OnAutoEnterChanged(bool value) { S.AutoEnter = value; Save(); }
    partial void OnKeepInClipboardChanged(bool value) { S.KeepInClipboard = value; Save(); }
    partial void OnCleanupEnabledChanged(bool value) { S.CleanupEnabled = value; Save(); }
    partial void OnSoundStartChanged(bool value) { S.SoundStart = value; Save(); }
    partial void OnSoundStopChanged(bool value) { S.SoundStop = value; Save(); }
    partial void OnSoundCancelChanged(bool value) { S.SoundCancel = value; Save(); }
    partial void OnSoundSuccessChanged(bool value) { S.SoundSuccess = value; Save(); }
    partial void OnSoundErrorChanged(bool value) { S.SoundError = value; Save(); }
    partial void OnAiBaseUrlChanged(string value) { S.AiBaseUrl = value; Save(); }
    partial void OnAiModelChanged(string value) { S.AiModel = value; Save(); }

    partial void OnAiProviderChanged(string value)
    {
        S.AiProvider = value;
        Save();
        OnPropertyChanged(nameof(AiConfigured));
        TestConnectionResult = "";
    }

    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        S.InputDeviceId = value?.Id;
        Save();
    }

    partial void OnLaunchAtLoginChanged(bool value)
    {
        S.LaunchAtLogin = value;
        Save();
        ApplyLaunchAtLogin(value);
    }

    private void Save() => _store.Save();

    private static void ApplyLaunchAtLogin(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Path.ChangeExtension(
                    System.Reflection.Assembly.GetEntryAssembly()!.Location, ".exe");
                key.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch { /* per-user Run key edit failed — non-fatal */ }
    }

    private static string DaysToLabel(int days) => days switch
    {
        0 => "Forever",
        7 => "7 days",
        30 => "30 days",
        90 => "90 days",
        _ => "7 days",
    };

    private static int LabelToDays(string label) => label switch
    {
        "Forever" => 0,
        "7 days" => 7,
        "30 days" => 30,
        "90 days" => 90,
        _ => 7,
    };

    // ---- commands ----

    [RelayCommand]
    private void PlaySound() => System.Media.SystemSounds.Asterisk.Play();

    [RelayCommand]
    private void TestConnection()
        => TestConnectionResult = AiProvider == "None"
            ? "Choose a provider first."
            : $"Test Connection runs against {AiProvider} once the AI client is wired.";

    [RelayCommand]
    private async Task DownloadModel()
    {
        if (IsModelInstalled || IsDownloadingModel) return;

        IsDownloadingModel = true;
        ModelDownloadProgress = 0;
        ModelStatusText = "Downloading… 0%";
        try
        {
            var progress = new Progress<double>(p =>
            {
                ModelDownloadProgress = p * 100;
                ModelStatusText = $"Downloading… {p * 100:0}%";
            });
            await _installer.EnsureInstalledAsync(progress);
        }
        catch (Exception ex)
        {
            ModelStatusText = "Download failed — " + ex.Message;
            IsDownloadingModel = false;
            return;
        }

        IsDownloadingModel = false;
        RefreshModelStatus();
    }

    // ---- vocabulary (custom terms) ----

    public ObservableCollection<string> VocabularyTerms { get; } = new(["Jot", "Parakeet", "WASAPI"]);

    [ObservableProperty] private string _newVocabTerm = "";

    [RelayCommand]
    private void AddVocabTerm()
    {
        string t = NewVocabTerm.Trim();
        if (t.Length > 0 && !VocabularyTerms.Contains(t, StringComparer.OrdinalIgnoreCase))
            VocabularyTerms.Add(t);
        NewVocabTerm = "";
    }

    [RelayCommand]
    private void RemoveVocabTerm(string? term)
    {
        if (term is not null) VocabularyTerms.Remove(term);
    }
}
