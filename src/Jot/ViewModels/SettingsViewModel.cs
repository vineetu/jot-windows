using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services.Abstractions;
using Jot.Services.Ai;
using Jot.Transcription;
using Jot.Transcription.Nemotron;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Jot.ViewModels;

public sealed record AudioInputDevice(string Id, string Name);

/// <summary>
/// Backs the single Settings page. Wraps <see cref="JotSettings"/> — each property persists on change
/// and applies live side effects (theme switch, launch-at-login registry, language → engine, hotkey
/// rebind via the settings-changed signal picked up in App). Model download is backed by the real
/// Nemotron installer; AI test/cleanup go through <see cref="IAiClient"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IThemeService _theme;
    private readonly NemotronModel _model;
    private readonly NemotronModelInstaller _installer;
    private readonly ITranscriber _transcriber;
    private readonly IAiClient _ai;
    private readonly AiCredentials _credentials;
    private readonly ISoundService _sound;
    private JotSettings S => _store.Current;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Jot";

    public Array ThemeModes { get; } = Enum.GetValues(typeof(AppThemeMode));
    // Languages supported by the Nemotron 3.5 multilingual engine (transcription-ready + broad-coverage
    // tiers). English is the default and the most accurate; smaller languages improve over time.
    public string[] Languages { get; } =
    [
        "English", "Arabic", "Bulgarian", "Chinese", "Croatian", "Czech", "Danish", "Dutch",
        "Estonian", "Finnish", "French", "German", "Greek", "Hebrew", "Hindi", "Hungarian",
        "Italian", "Japanese", "Korean", "Latvian", "Lithuanian", "Norwegian", "Polish",
        "Portuguese", "Romanian", "Russian", "Slovak", "Slovenian", "Spanish", "Swedish",
        "Turkish", "Ukrainian", "Vietnamese",
    ];
    public string[] Providers { get; } = ["None", "OpenAI", "Anthropic", "Gemini", "Ollama"];
    public string[] RetentionOptions { get; } = ["Forever", "7 days", "30 days", "90 days"];
    public string[] TranscriptionDevices { get; } = ["CPU", "GPU (DirectML)"];

    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty] private AppThemeMode _themeMode;
    [ObservableProperty] private bool _advancedFeatures;
    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private string _retention = "7 days";
    [ObservableProperty] private bool _returnToOrigin;
    [ObservableProperty] private AudioInputDevice? _selectedDevice;

    [ObservableProperty] private string _language = "English";
    [ObservableProperty] private string _transcriptionDevice = "CPU";
    [ObservableProperty] private bool _liveCaptions = true;
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _autoEnter;
    [ObservableProperty] private bool _keepInClipboard;

    // Shortcuts (editable chord strings). Persisted; App re-registers on the settings-changed signal.
    [ObservableProperty] private string _toggleRecordingHotkey = "Alt+Space";
    [ObservableProperty] private string _cancelRecordingHotkey = "Escape";
    [ObservableProperty] private string _pasteLastHotkey = "Alt+OemComma";
    [ObservableProperty] private string _rewriteHotkey = "Alt+OemQuestion";
    [ObservableProperty] private string _rewriteWithVoiceHotkey = "Alt+OemPeriod";

    // On-device model download state (backed by NemotronModelInstaller)
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
    [ObservableProperty] private bool _isTestingConnection;

    [ObservableProperty] private bool _soundStart;
    [ObservableProperty] private bool _soundStop;
    [ObservableProperty] private bool _soundCancel;
    [ObservableProperty] private bool _soundSuccess;
    [ObservableProperty] private bool _soundError;

    public bool AiConfigured => AiProvider != "None";
    public bool NeedsApiKey => AiConfigured && AiDefaults.NeedsKey(AiProvider);
    public bool ShowAiOverrides => AiConfigured && AdvancedFeatures;          // base URL + model overrides
    public bool ShowAiModelReadonly => AiConfigured && !AdvancedFeatures;      // just show the default
    public string EffectiveAiModel => string.IsNullOrWhiteSpace(AiModel) ? AiDefaults.Model(AiProvider) : AiModel;
    public string DefaultModelHint => AiDefaults.Model(AiProvider);
    public string DefaultBaseUrlHint => AiDefaults.BaseUrl(AiProvider);

    private void RaiseAiComputed()
    {
        OnPropertyChanged(nameof(AiConfigured));
        OnPropertyChanged(nameof(NeedsApiKey));
        OnPropertyChanged(nameof(ShowAiOverrides));
        OnPropertyChanged(nameof(ShowAiModelReadonly));
        OnPropertyChanged(nameof(EffectiveAiModel));
        OnPropertyChanged(nameof(DefaultModelHint));
        OnPropertyChanged(nameof(DefaultBaseUrlHint));
    }

    public SettingsViewModel(ISettingsStore store, IThemeService theme,
        NemotronModel model, NemotronModelInstaller installer,
        ITranscriber transcriber, IAiClient ai, AiCredentials credentials, ISoundService sound)
    {
        _store = store;
        _theme = theme;
        _model = model;
        _installer = installer;
        _transcriber = transcriber;
        _ai = ai;
        _credentials = credentials;
        _sound = sound;

        // Seed backing fields directly so wiring the UI doesn't trigger a save storm.
        _themeMode = S.Theme;
        _advancedFeatures = S.AdvancedFeatures;
        _launchAtLogin = S.LaunchAtLogin;
        _retention = DaysToLabel(S.RetentionDays);
        _returnToOrigin = S.ReturnToOrigin;
        _language = S.Language;
        _transcriptionDevice = S.TranscriptionDevice;
        _liveCaptions = S.LiveCaptions;
        _autoPaste = S.AutoPaste;
        _autoEnter = S.AutoEnter;
        _keepInClipboard = S.KeepInClipboard;
        _toggleRecordingHotkey = S.ToggleRecordingHotkey;
        _cancelRecordingHotkey = S.CancelRecordingHotkey;
        _pasteLastHotkey = S.PasteLastHotkey;
        _rewriteHotkey = S.RewriteHotkey;
        _rewriteWithVoiceHotkey = S.RewriteWithVoiceHotkey;
        _aiProvider = S.AiProvider;
        _aiBaseUrl = S.AiBaseUrl ?? "";
        _aiModel = S.AiModel ?? "";
        _aiApiKey = credentials.ApiKey ?? "";
        _cleanupEnabled = S.CleanupEnabled;
        _soundStart = S.SoundStart;
        _soundStop = S.SoundStop;
        _soundCancel = S.SoundCancel;
        _soundSuccess = S.SoundSuccess;
        _soundError = S.SoundError;

        LoadDevices();
        RefreshModelStatus();
    }

    /// <summary>Applies the stored language to the engine. Called at startup and on change.</summary>
    public static void ApplyLanguage(ITranscriber transcriber, string language)
    {
        if (transcriber is NemotronTranscriber n)
            n.SetLanguageId(NemotronLanguages.TryGetId(language, out long id) ? id : 0);
    }

    private void RefreshModelStatus()
    {
        IsModelInstalled = _model.IsInstalled;
        if (IsModelInstalled)
            ModelStatusText = "Nemotron 3.5 · Installed";
        else if (!IsDownloadingModel)
            ModelStatusText = "Not installed (~0.67 GB)";
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
    partial void OnAdvancedFeaturesChanged(bool value) { S.AdvancedFeatures = value; Save(); RaiseAiComputed(); }
    partial void OnReturnToOriginChanged(bool value) { S.ReturnToOrigin = value; Save(); }
    partial void OnRetentionChanged(string value) { S.RetentionDays = LabelToDays(value); Save(); }
    partial void OnLanguageChanged(string value)
    {
        S.Language = value;
        Save();
        ApplyLanguage(_transcriber, value);
    }
    partial void OnTranscriptionDeviceChanged(string value) { S.TranscriptionDevice = value; Save(); }
    partial void OnLiveCaptionsChanged(bool value) { S.LiveCaptions = value; Save(); }
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
    partial void OnAiModelChanged(string value) { S.AiModel = value; Save(); OnPropertyChanged(nameof(EffectiveAiModel)); }
    partial void OnAiApiKeyChanged(string value) { _credentials.ApiKey = value; } // session-only, never persisted

    // Shortcuts: persist, then Save() raises ISettingsStore.Changed, which App uses to re-register.
    partial void OnToggleRecordingHotkeyChanged(string value) { S.ToggleRecordingHotkey = value; Save(); }
    partial void OnCancelRecordingHotkeyChanged(string value) { S.CancelRecordingHotkey = value; Save(); }
    partial void OnPasteLastHotkeyChanged(string value) { S.PasteLastHotkey = value; Save(); }
    partial void OnRewriteHotkeyChanged(string value) { S.RewriteHotkey = value; Save(); }
    partial void OnRewriteWithVoiceHotkeyChanged(string value) { S.RewriteWithVoiceHotkey = value; Save(); }

    partial void OnAiProviderChanged(string value)
    {
        S.AiProvider = value;
        Save();
        RaiseAiComputed();
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

    private AiConfig BuildAiConfig() => new(AiProvider,
        string.IsNullOrWhiteSpace(AiBaseUrl) ? null : AiBaseUrl,
        string.IsNullOrWhiteSpace(AiModel) ? null : AiModel,
        string.IsNullOrWhiteSpace(AiApiKey) ? null : AiApiKey);

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
    private void PlaySound() => _sound.Preview();

    [RelayCommand]
    private async Task TestConnection()
    {
        if (AiProvider == "None") { TestConnectionResult = "Choose a provider first."; return; }
        IsTestingConnection = true;
        TestConnectionResult = "Testing…";
        try
        {
            AiResult result = await _ai.TestConnectionAsync(BuildAiConfig());
            TestConnectionResult = result.Message;
        }
        catch (Exception ex)
        {
            TestConnectionResult = "Failed — " + ex.Message;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

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

    public ObservableCollection<string> VocabularyTerms { get; } = new(["Jot", "Nemotron", "WASAPI"]);

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
