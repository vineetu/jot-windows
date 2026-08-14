using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services;
using Jot.Services.Abstractions;
using Jot.Services.Ai;
using Jot.Transcription;
using Jot.Transcription.Nemotron;
using Jot.Vocabulary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Jot.ViewModels;

public sealed record AudioInputDevice(string Id, string Name);

/// <summary>
/// Backs the single Settings page. Wraps <see cref="JotSettings"/> — each property persists on change
/// and applies live side effects (theme switch, launch-at-login registry, language → engine, hotkey
/// rebind via the settings-changed signal picked up in App). Model download is backed by the real
/// Nemotron installer; AI test/rewrite go through <see cref="IAiClient"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly IThemeService _theme;
    private readonly ModelDownload _download;
    private readonly GpuModelDownload _gpuDownload;
    private readonly CtcModelDownload _vocabularyDownload;
    /// <summary>Every model-download surface this page owns, so anything that invalidates "is it on
    /// disk?" refreshes ALL of them. Replaces the individually-named `Refresh()` calls, which were
    /// already one short: a data-folder move refreshed only <see cref="_download"/>, leaving the GPU
    /// row claiming "Installed" against a folder the model no longer lived in.</summary>
    private readonly ModelDownload[] _downloads;
    private readonly DataFolderMigrator _migrator;
    private readonly ITranscriber _transcriber;
    private readonly IAiClient _ai;
    private readonly AiCredentials _credentials;
    private readonly PfbAuth _pfb;
    private readonly ISoundService _sound;
    private readonly VocabularyStore _vocabulary;
    private readonly IVocabularySpotter _spotter;
    private JotSettings S => _store.Current;

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Jot";

    public Array ThemeModes { get; } = Enum.GetValues(typeof(AppThemeMode));
    // All 40 model-card locales + Auto detect, grouped by quality tier (shared builder with the wizard).
    public System.Windows.Data.ListCollectionView LanguageOptions { get; } = LanguagePicker.BuildView();
    // Provider list is build-flavor dependent: public/Store gets bring-your-own cloud, Sony gets only PFB.
    public string[] Providers { get; } = BuildFlavor.AiProviders;

    /// <summary>Flavor-appropriate copy for the AI section's intro banner.</summary>
    public string AiInfoTitle => BuildFlavor.AiInfoTitle;
    public string AiInfoMessage => BuildFlavor.AiInfoMessage;
    public string[] RetentionOptions { get; } = ["Forever", "7 days", "30 days", "90 days"];
    public string[] TranscriptionDevices { get; } =
    [
        Jot.Transcription.TranscriptionDevices.Auto,
        Jot.Transcription.TranscriptionDevices.Cpu,
        Jot.Transcription.TranscriptionDevices.Gpu,
    ];

    /// <summary>Paste-method choices for the Settings dropdown (value persisted, label shown). Mirrors Handy.</summary>
    public sealed record PasteMethodOption(string Value, string Label);
    public PasteMethodOption[] PasteMethods { get; } =
    [
        new("auto", "Ctrl+V (recommended)"),
        // Kept so a profile that already persisted "ctrl_v" still selects an entry; same path as "auto".
        new("ctrl_v", "Ctrl+V"),
        new("shift_insert", "Shift+Insert (terminals)"),
        new("type", "Type it out"),
        new("clipboard", "Copy to clipboard"),
        new("none", "Don't paste"),
    ];

    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty] private AppThemeMode _themeMode;
    [ObservableProperty] private bool _advancedFeatures;
    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private string _retention = "7 days";
    [ObservableProperty] private string _dataDirectory = "";
    [ObservableProperty] private bool _returnToOrigin;
    [ObservableProperty] private AudioInputDevice? _selectedDevice;

    [ObservableProperty] private string _language = "English";
    [ObservableProperty] private string _transcriptionDevice = Jot.Transcription.TranscriptionDevices.Auto;
    [ObservableProperty] private bool _liveCaptions = true;
    [ObservableProperty] private bool _offlineCleanupEnabled = true;
    [ObservableProperty] private bool _autoPaste;
    [ObservableProperty] private bool _autoEnter;
    [ObservableProperty] private bool _keepInClipboard;
    [ObservableProperty] private string _pasteMethod = "auto";

    // Shortcuts (editable chord strings). Persisted; App re-registers on the settings-changed signal.
    // These initializers must match the JotSettings defaults (they're overwritten on load, but a mismatch
    // is a latent bug). Toggle is Ctrl+Shift+Space — NOT Alt+Space (Windows system-menu / focus steal).
    [ObservableProperty] private string _toggleRecordingHotkey = "Ctrl+Shift+Space";
    [ObservableProperty] private string _cancelRecordingHotkey = "Escape";
    [ObservableProperty] private string _pasteLastHotkey = "Ctrl+Alt+V";
    [ObservableProperty] private string _rewriteHotkey = "Ctrl+Alt+OemQuestion";
    [ObservableProperty] private string _rewriteWithVoiceHotkey = "Ctrl+Alt+OemPeriod";
    [ObservableProperty] private string _pushToTalkHotkey = ""; // optional hold-to-dictate key; "" = off (null in store)

    /// <summary>Shared on-device model download — the SAME instance the setup wizard uses (one downloader,
    /// one progress/status surface). The Model row binds its status, progress bar and Download button here.</summary>
    public ModelDownload Download => _download;

    /// <summary>The optional fp16 GPU model's download state — the SAME instance GpuTierCoordinator's
    /// silent background fetch drives, so the "GPU model" row shows live progress either way. The manual
    /// Download button exists for the cases the automatic path skips (metered connection, weak-looking GPU).</summary>
    public GpuModelDownload GpuDownload => _gpuDownload;

    /// <summary>The optional vocabulary keyword-spotter model's download state — the "Vocabulary model"
    /// row's status, progress bar and Download/Retry button. Started ONLY by the user (the toggle-on
    /// prompt or that button); nothing in the app fetches it in the background.</summary>
    public CtcModelDownload VocabularyDownload => _vocabularyDownload;

    /// <summary>Moves the model + recordings + library when the Save location changes — the Save-location
    /// row binds its progress bar and status here. Shared singleton (also finishes interrupted moves on launch).</summary>
    public DataFolderMigrator Migrator => _migrator;

    [ObservableProperty] private string _aiProvider = "None";
    [ObservableProperty] private string _aiBaseUrl = "";
    [ObservableProperty] private string _aiModel = "";
    [ObservableProperty] private string _aiApiKey = ""; // persisted encrypted via AiCredentials (DPAPI); seeded from it at startup
    [ObservableProperty] private string _testConnectionResult = "";
    [ObservableProperty] private bool _isTestingConnection;

    [ObservableProperty] private bool _soundStart;
    [ObservableProperty] private bool _soundStop;
    [ObservableProperty] private bool _soundCancel;
    [ObservableProperty] private bool _soundSuccess;
    [ObservableProperty] private bool _soundError;

    public bool AiConfigured => AiProvider != "None";
    public bool NeedsApiKey => AiConfigured && AiDefaults.NeedsKey(AiProvider);
    /// <summary>True when an encrypted key is already stored for the selected provider.</summary>
    public bool HasSavedKey => !string.IsNullOrEmpty(_credentials.GetKey(AiProvider));
    public bool ShowAiOverrides => AiConfigured && AdvancedFeatures;          // free-text base URL + model overrides
    public bool ShowModelPicker => AiConfigured && !AdvancedFeatures;         // curated model dropdown
    public string EffectiveAiModel => string.IsNullOrWhiteSpace(AiModel) ? AiDefaults.Model(AiProvider) : AiModel;
    public string DefaultModelHint => AiDefaults.Model(AiProvider);
    public string DefaultBaseUrlHint => AiDefaults.BaseUrl(AiProvider);

    /// <summary>The curated model IDs shown in the non-advanced dropdown for the current provider.
    /// The dropdown binds its SelectedItem straight to <see cref="AiModel"/>.</summary>
    public ObservableCollection<string> AiModels { get; } = new();

    /// <summary>Deep link to the provider's API-key page, surfaced as a small link under the key box.</summary>
    public string ApiKeyUrl => AiDefaults.ApiKeyUrl(AiProvider);
    public bool HasApiKeyUrl => !string.IsNullOrEmpty(ApiKeyUrl);

    // PFB replaces the API-key box with a JWT sign-in flow (see PfbAuth). RaiseAiComputed()/RefreshPfb()
    // re-raise these computed properties on change.

    public bool IsPfbProvider => AiProvider.Equals(PfbAuth.Provider, StringComparison.OrdinalIgnoreCase);
    /// <summary>CLI present; otherwise the UI shows an install card instead of sign-in.</summary>
    public bool PfbCliInstalled => PfbAuth.CliInstalled;
    public bool PfbCliMissing => IsPfbProvider && !PfbCliInstalled;
    /// <summary>A valid (unexpired) PFB session exists.</summary>
    public bool PfbSignedIn => _pfb.IsSignedIn;
    /// <summary>Show the Sign-in button: PFB selected, CLI present, not currently signed in.</summary>
    public bool ShowPfbSignIn => IsPfbProvider && PfbCliInstalled && !PfbSignedIn;
    /// <summary>Show the signed-in row (subject + expiry + Disconnect).</summary>
    public bool ShowPfbSignedIn => IsPfbProvider && PfbSignedIn;

    [ObservableProperty] private bool _isPfbBusy;
    [ObservableProperty] private string _pfbStatus = "";

    /// <summary>Inverse of <see cref="IsPfbBusy"/> — bound to button IsEnabled (no converter needed).</summary>
    public bool PfbNotBusy => !IsPfbBusy;
    partial void OnIsPfbBusyChanged(bool value) => OnPropertyChanged(nameof(PfbNotBusy));

    /// <summary>"Signed in as … · expires in Xh Ym", or a prompt to sign in.</summary>
    public string PfbSessionText
    {
        get
        {
            PfbSession? s = _pfb.Current;
            if (s is null) return "Not signed in.";
            string who = string.IsNullOrEmpty(s.Subject) ? "Signed in" : $"Signed in as {s.Subject}";
            TimeSpan r = s.Remaining;
            string left = r.TotalHours >= 1 ? $"{(int)r.TotalHours}h {r.Minutes}m" : $"{r.Minutes}m";
            return $"{who} · expires in {left}";
        }
    }

    private void RefreshPfb()
    {
        OnPropertyChanged(nameof(IsPfbProvider));
        OnPropertyChanged(nameof(PfbCliInstalled));
        OnPropertyChanged(nameof(PfbCliMissing));
        OnPropertyChanged(nameof(PfbSignedIn));
        OnPropertyChanged(nameof(ShowPfbSignIn));
        OnPropertyChanged(nameof(ShowPfbSignedIn));
        OnPropertyChanged(nameof(PfbSessionText));
    }

    /// <summary>Rebuild the model list for the current provider and re-select a valid item. Clearing
    /// the ItemsSource blanks the ComboBox, so the model is re-assigned (to the provider default when
    /// the current one isn't offered) AFTER the items are back in place.</summary>
    private void RefreshAiModels()
    {
        AiModels.Clear();
        foreach (string m in AiDefaults.Models(AiProvider)) AiModels.Add(m);
        if (AiModels.Count > 0 && !AiModels.Contains(AiModel))
            AiModel = AiModels[0];
    }

    private void RaiseAiComputed()
    {
        OnPropertyChanged(nameof(AiConfigured));
        OnPropertyChanged(nameof(NeedsApiKey));
        OnPropertyChanged(nameof(ShowAiOverrides));
        OnPropertyChanged(nameof(ShowModelPicker));
        OnPropertyChanged(nameof(EffectiveAiModel));
        OnPropertyChanged(nameof(DefaultModelHint));
        OnPropertyChanged(nameof(DefaultBaseUrlHint));
        OnPropertyChanged(nameof(ApiKeyUrl));
        OnPropertyChanged(nameof(HasApiKeyUrl));
        RefreshPfb();
    }

    public SettingsViewModel(ISettingsStore store, IThemeService theme,
        ModelDownload download, GpuModelDownload gpuDownload, CtcModelDownload vocabularyDownload,
        DataFolderMigrator migrator,
        ITranscriber transcriber, IAiClient ai, AiCredentials credentials, PfbAuth pfb, ISoundService sound,
        VocabularyStore vocabulary, IVocabularySpotter spotter)
    {
        _store = store;
        _theme = theme;
        _download = download;
        _gpuDownload = gpuDownload;
        _vocabularyDownload = vocabularyDownload;
        _downloads = [download, gpuDownload, vocabularyDownload];
        _migrator = migrator;
        _transcriber = transcriber;
        _ai = ai;
        _credentials = credentials;
        _pfb = pfb;
        _sound = sound;
        _vocabulary = vocabulary;
        _spotter = spotter;

        // Seed backing fields directly so wiring the UI doesn't trigger a save storm.
        _themeMode = S.Theme;
        _advancedFeatures = S.AdvancedFeatures;
        _launchAtLogin = S.LaunchAtLogin;
        _retention = DaysToLabel(S.RetentionDays);
        _dataDirectory = JotPaths.DataDir(S);
        _returnToOrigin = S.ReturnToOrigin;
        _language = NemotronLocales.Normalize(S.Language); // legacy names → codes so the picker matches
        _transcriptionDevice = S.TranscriptionDevice;
        _liveCaptions = S.LiveCaptions;
        _offlineCleanupEnabled = S.OfflineCleanupEnabled;
        _autoPaste = S.AutoPaste;
        _autoEnter = S.AutoEnter;
        _keepInClipboard = S.KeepInClipboard;
        _pasteMethod = S.PasteMethod;
        _toggleRecordingHotkey = S.ToggleRecordingHotkey;
        _cancelRecordingHotkey = S.CancelRecordingHotkey;
        _pasteLastHotkey = S.PasteLastHotkey;
        _rewriteHotkey = S.RewriteHotkey;
        _rewriteWithVoiceHotkey = S.RewriteWithVoiceHotkey;
        _pushToTalkHotkey = S.PushToTalkHotkey ?? "";
        // A provider persisted under a different flavor (or a tampered settings.json) may not exist
        // in this build's list — fall back to None so the dropdown always has a valid selection.
        _aiProvider = Providers.Contains(S.AiProvider, StringComparer.OrdinalIgnoreCase) ? S.AiProvider : "None";
        _aiBaseUrl = S.AiBaseUrl ?? "";
        _aiModel = S.AiModel ?? "";
        _aiApiKey = credentials.GetKey(_aiProvider) ?? ""; // the key saved for the current provider
        _soundStart = S.SoundStart;
        _soundStop = S.SoundStop;
        _soundCancel = S.SoundCancel;
        _soundSuccess = S.SoundSuccess;
        _soundError = S.SoundError;
        _vocabularyEnabled = S.VocabularyEnabled;
        _vocabularyChipEnabled = S.VocabularyChipEnabled;
        _vocabulary.Terms.CollectionChanged += (_, _) => RaiseVocabularyComputed();
        // The vocabulary InfoBars are derived from download state (unavailable / downloading / ready),
        // so they have to be re-raised whenever it moves — otherwise the warning bar stays up through
        // a successful download and only a page rebuild clears it.
        _vocabularyDownload.PropertyChanged += (_, _) => RaiseVocabularyComputed();

        LoadDevices();
        RefreshDownloads();
        RefreshAiModels();
    }

    /// <summary>Re-check every model download against disk. Called at construction, each time Settings
    /// opens, and after a data-folder move — one call, so a fourth model can't be forgotten.</summary>
    public void RefreshDownloads()
    {
        foreach (ModelDownload d in _downloads) d.Refresh();
        RaiseVocabularyComputed();
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


    partial void OnThemeModeChanged(AppThemeMode value) { _theme.SetMode(value); } // SetMode persists
    partial void OnAdvancedFeaturesChanged(bool value) { S.AdvancedFeatures = value; Save(); RaiseAiComputed(); }
    partial void OnReturnToOriginChanged(bool value) { S.ReturnToOrigin = value; Save(); }
    partial void OnRetentionChanged(string value) { S.RetentionDays = LabelToDays(value); Save(); }
    partial void OnLanguageChanged(string value)
    {
        S.Language = value;
        Save();
        TranscriberFactory.ApplyLanguage(_transcriber, value);
        RaiseVocabularyComputed();   // the English-only banner is language-derived
    }
    partial void OnTranscriptionDeviceChanged(string value) { S.TranscriptionDevice = value; Save(); }
    partial void OnLiveCaptionsChanged(bool value) { S.LiveCaptions = value; Save(); }
    partial void OnOfflineCleanupEnabledChanged(bool value) { S.OfflineCleanupEnabled = value; Save(); }
    partial void OnAutoPasteChanged(bool value) { S.AutoPaste = value; Save(); }
    partial void OnAutoEnterChanged(bool value) { S.AutoEnter = value; Save(); }
    partial void OnKeepInClipboardChanged(bool value) { S.KeepInClipboard = value; Save(); }
    partial void OnPasteMethodChanged(string value) { S.PasteMethod = value; Save(); }
    partial void OnSoundStartChanged(bool value) { S.SoundStart = value; Save(); }
    partial void OnSoundStopChanged(bool value) { S.SoundStop = value; Save(); }
    partial void OnSoundCancelChanged(bool value) { S.SoundCancel = value; Save(); }
    partial void OnSoundSuccessChanged(bool value) { S.SoundSuccess = value; Save(); }
    partial void OnSoundErrorChanged(bool value) { S.SoundError = value; Save(); }
    partial void OnAiBaseUrlChanged(string value) { S.AiBaseUrl = value; Save(); }
    partial void OnAiModelChanged(string value)
    {
        S.AiModel = value; Save();
        OnPropertyChanged(nameof(EffectiveAiModel));
    }
    // True while OnAiProviderChanged is loading the stored key into AiApiKey — that assignment must
    // NOT round-trip back into the credential store (switching providers is a read-only operation).
    private bool _loadingKeyFromStore;

    // Persisted encrypted (DPAPI) by AiCredentials, keyed by provider, reloaded next launch — each
    // provider's key stays separate. Only genuine edits (typing in the box) reach the store.
    partial void OnAiApiKeyChanged(string value)
    {
        // Persist non-empty edits only; never auto-delete a saved key on empty. Empty arrives from too
        // many non-intentional sources (control re-render, reveal toggle, provider reload, page rebuild),
        // and a silent wipe of a working key is the worst outcome. Intentional removal goes through ClearApiKey().
        if (!_loadingKeyFromStore && !string.IsNullOrEmpty(value))
            _credentials.SetKey(AiProvider, value);
        OnPropertyChanged(nameof(HasSavedKey));
    }

    /// <summary>Explicitly forget the saved key for the current provider (the only path that deletes
    /// it — empty edits no longer do, so an accidental blank can't wipe a working key).</summary>
    [RelayCommand]
    private void ClearApiKey()
    {
        _credentials.SetKey(AiProvider, "");
        _loadingKeyFromStore = true;               // clearing the box must not round-trip back as a write
        try { AiApiKey = ""; }
        finally { _loadingKeyFromStore = false; }
        OnPropertyChanged(nameof(HasSavedKey));
        TestConnectionResult = "";
    }

    /// <summary>Re-read the saved key for the current provider into the box. Called when Settings opens
    /// so the box always reflects the store — this singleton VM seeds the key once at construction, which
    /// can be stale (or not yet synced to the PasswordBox); a provider round-trip did this already, so
    /// now every open does too.</summary>
    public void RefreshApiKey()
    {
        _loadingKeyFromStore = true;
        try { AiApiKey = _credentials.GetKey(AiProvider) ?? ""; }
        finally { _loadingKeyFromStore = false; }
        OnPropertyChanged(nameof(HasSavedKey));
    }

    // Shortcuts: persist, then Save() raises ISettingsStore.Changed, which App uses to re-register.
    partial void OnToggleRecordingHotkeyChanged(string value) { S.ToggleRecordingHotkey = value; Save(); }
    partial void OnCancelRecordingHotkeyChanged(string value) { S.CancelRecordingHotkey = value; Save(); }
    partial void OnPasteLastHotkeyChanged(string value) { S.PasteLastHotkey = value; Save(); }
    partial void OnRewriteHotkeyChanged(string value) { S.RewriteHotkey = value; Save(); }
    partial void OnRewriteWithVoiceHotkeyChanged(string value) { S.RewriteWithVoiceHotkey = value; Save(); }
    partial void OnPushToTalkHotkeyChanged(string value)
    { S.PushToTalkHotkey = string.IsNullOrWhiteSpace(value) ? null : value; Save(); } // "" clears → off

    partial void OnAiProviderChanged(string value)
    {
        S.AiProvider = value;
        Save();
        // Show the key stored for THIS provider — each provider keeps its own. Guarded so the load
        // doesn't echo back into the store as a (pointless, potentially destructive) write.
        _loadingKeyFromStore = true;
        try { AiApiKey = _credentials.GetKey(value) ?? ""; }
        finally { _loadingKeyFromStore = false; }
        // Rebuild the model dropdown for the new provider (resets selection to its default if needed).
        RefreshAiModels();
        RaiseAiComputed();
        OnPropertyChanged(nameof(HasSavedKey));
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
        // Test the SAME key the real AI actions use — the persisted store (GetKey) — falling back to
        // the just-typed value only when nothing is stored yet, so Test and runtime can't disagree.
        _credentials.GetKey(AiProvider) is string k && k.Length > 0 ? k
            : (string.IsNullOrWhiteSpace(AiApiKey) ? null : AiApiKey));

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


    [RelayCommand]
    private async Task BrowseDataDirectory()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose where Jot saves your model, recordings and transcripts",
            UseDescriptionForTitle = true,
            SelectedPath = DataDirectory,
        };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dlg.SelectedPath))
            return;
        await MoveDataTo(dlg.SelectedPath);
    }

    [RelayCommand]
    private Task UseDefaultDataDirectory() => MoveDataTo(JotPaths.DefaultDataDir);

    // Migrate everything (model + recordings + library) to the chosen folder rather than stranding it,
    // then reflect the now-flipped setting. The migrator repoints DataDirectory itself on success and
    // leaves it untouched on failure, so DataDir(S) is the source of truth either way.
    private async Task MoveDataTo(string target)
    {
        bool ok = await _migrator.MoveToAsync(target);
        DataDirectory = JotPaths.DataDir(S);
        // Every model now lives in the new folder — re-check "Installed" against it. All of them, not
        // just the required one: see _downloads.
        if (ok) RefreshDownloads();
    }

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
    private async Task SignInPfb()
    {
        if (IsPfbBusy) return;
        IsPfbBusy = true;
        PfbStatus = "Signing in… complete the login in your browser.";
        try
        {
            PfbSignInResult result = await _pfb.SignInAsync();
            PfbStatus = result.Message;
        }
        catch (Exception ex)
        {
            PfbStatus = "Sign-in error: " + ex.Message;
        }
        finally
        {
            IsPfbBusy = false;
            RefreshPfb();
        }
    }

    [RelayCommand]
    private void DisconnectPfb()
    {
        _pfb.Disconnect();
        PfbStatus = "Signed out.";
        RefreshPfb();
    }

    [RelayCommand]
    private async Task InstallPfbHelper()
    {
        if (IsPfbBusy) return;
        IsPfbBusy = true;
        PfbStatus = "Downloading the sign-in helper…";
        try
        {
            PfbSignInResult result = await _pfb.InstallCliAsync();
            PfbStatus = result.Message;
        }
        catch (Exception ex)
        {
            PfbStatus = "Download error: " + ex.Message;
        }
        finally
        {
            IsPfbBusy = false;
            RefreshPfb();
        }
    }


    // VOCABULARY. The section is VISIBLE (inside Advanced features) with the master toggle default off.
    // Term editing itself lives on VocabularyPage; this pane owns the two toggles, the two honesty
    // banners, the model-download row, and the door to that page.

    [ObservableProperty] private bool _vocabularyEnabled;
    [ObservableProperty] private bool _vocabularyChipEnabled = true;

    /// <summary>"Manage…" — a programmatic navigation, like the one Recents makes to
    /// RecordingDetailPage. Resolved on demand rather than injected so this already-long constructor
    /// doesn't grow a UI-navigation dependency (same pattern as ReTranscribe's transcriber).</summary>
    [RelayCommand]
    private void ManageVocabulary() =>
        App.Services.GetRequiredService<Services.Navigation.INavigator>()
           .Navigate(typeof(Views.VocabularyPage));

    /// <summary>
    /// Turning vocabulary ON is the one moment the ~132 MB model is worth asking about: the user has
    /// just said they want the feature, and it cannot do anything without it. ASKED, never automatic —
    /// this is by far the largest optional fetch in the app and it must be the user's call.
    ///
    /// Declining leaves the toggle ON deliberately: the terms are saved, the row keeps its Download
    /// button, and the state is exactly the pre-download one the whole feature is already designed to
    /// survive (no model ⇒ no spotter ⇒ no corrections ⇒ no error). Flipping the toggle back off
    /// behind the user's back would throw away the intent they just expressed.
    /// </summary>
    partial void OnVocabularyEnabledChanged(bool value)
    {
        S.VocabularyEnabled = value;
        Save();
        RaiseVocabularyComputed();
        if (!CtcModelDownload.ShouldOffer(value, _vocabularyDownload.IsInstalled,
                                          _vocabularyDownload.IsDownloading, VocabularyLanguageOk))
            return;

        // Fully qualified: the project also references WinForms, whose MessageBox would silently win a
        // plain `using System.Windows`.
        System.Windows.MessageBoxResult answer = System.Windows.MessageBox.Show(
            CtcModelDownload.ConsentMessage, CtcModelDownload.ConsentTitle,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer == System.Windows.MessageBoxResult.Yes) DownloadVocabularyModelCommand.Execute(null);
    }

    partial void OnVocabularyChipEnabledChanged(bool value) { S.VocabularyChipEnabled = value; Save(); }

    /// <summary>
    /// The one path that fetches the vocabulary model — the toggle-on prompt and the row's
    /// Download/Retry button both land here. Idempotent and resumable (that is <c>EnsureAsync</c>), and
    /// it never throws: a failure leaves the message in the row and the feature in its designed
    /// no-model state.
    /// </summary>
    [RelayCommand]
    private async Task DownloadVocabularyModel()
    {
        bool ok = await _vocabularyDownload.EnsureAsync();
        RaiseVocabularyComputed();
        if (!ok) return;

        // Pay the 1–1.9 s session load HERE rather than inside the first dictation's stop path — the
        // exact reason App.WireVocabularySpotterLifecycle warms at all. That wiring only re-syncs on a
        // settings change or a term-list change, and a finished download is neither.
        // Concrete type test, like ApplyLanguage's transcriber switch: Warm is a session-lifecycle
        // detail of the CTC spotter, not part of the IVocabularySpotter seam.
        if (_spotter is CtcVocabularySpotter ctc)
            _ = Task.Run(() =>
            {
                try { ctc.Warm(); }
                catch (Exception ex) { JotLog.Error("vocabulary: warm after download failed", ex); }
            });
    }

    /// <summary>Live count of saved terms — the "Your terms" row, and what makes the empty case honest.</summary>
    public int VocabularyTermCount => _vocabulary.Terms.Count;
    public string VocabularyTermCountText =>
        VocabularyTermCount == 1 ? "1 term" : $"{VocabularyTermCount} terms";

    /// <summary>D5 · whether the SOUND-ALIKE (acoustic) path may run. Still the English-only question:
    /// it is what the 132 MB download offer keys on, and what the spotter session's lifecycle keys on.
    /// It is NO LONGER the same as "vocabulary works here" — see <see cref="VocabularyMode"/>.</summary>
    public bool VocabularyLanguageOk => VocabularyRunner.LanguageSupported(Language);

    public VocabularyRunner.VocabularyMode VocabularyMode => VocabularyRunner.ModeFor(Language);

    /// <summary>The banner fires only when vocabulary can do NOTHING — Auto detect, or a language with
    /// no everyday-word list, where the gate would lose the brake that stops a term clobbering an
    /// ordinary word. Spelling-only languages are a working feature and get a badge, not a warning.
    /// The toggle and the term list stay usable either way: people switch languages, and silently
    /// locking their data behind one is worse than saying so.</summary>
    public bool VocabularyLanguageBlocked => VocabularyMode == VocabularyRunner.VocabularyMode.Off;
    public bool VocabularyLanguageIsAuto =>
        NemotronLocales.Normalize(Language).Equals(NemotronLocales.AutoCode, StringComparison.OrdinalIgnoreCase);

    public string VocabularyLanguageTitle => VocabularyLanguageIsAuto
        ? "Custom vocabulary needs a language, not Auto detect."
        : $"Custom vocabulary doesn't cover {LanguageLabel(Language)} yet.";

    /// <summary>
    /// THREE reasons, because there are three, and giving the wrong one is its own kind of lying.
    /// Auto detect has no resolved language; most blocked languages have no everyday-word list at all;
    /// and Slovenian HAS one that E6 measured too weak to protect anyone
    /// (docs/plans/vocabulary-brake-per-language.md — 1.72 false applies per 1000 words, and the only
    /// setting that fixed it recovered one missed term in six). Telling that user "Jot has no
    /// everyday-word list for Slovenian" would be false, and the shape of false that gets fixed by
    /// someone shipping the list we already have.
    /// </summary>
    public string VocabularyLanguageMessage => BlockedMessage(Language);

    /// <summary>
    /// STATIC so it can be tested. The bar it feeds is a WPF-UI <c>InfoBar</c>, whose Title and Message
    /// are not exposed to UI Automation at all — verified by walking the live window's 182 elements —
    /// so the running app cannot answer "does it give the right reason?" and nothing else would.
    /// </summary>
    public static string BlockedMessage(string? language)
    {
        string locale = NemotronLocales.Normalize(language);
        if (locale.Equals(NemotronLocales.AutoCode, StringComparison.OrdinalIgnoreCase))
        {
            return "Your language is set to Auto detect, so Jot can't tell which language you're " +
                   "speaking. Pick one in Settings → Language to use your terms. Your list is saved " +
                   "either way.";
        }
        // A language with a list that E6 cut is NOT a language with no list, and saying so would be
        // the kind of false that gets "fixed" by someone shipping the list we already have.
        return EmbeddedCommonWordsProvider.ResourceFor(locale) is not null
            ? $"Jot can't yet tell your terms apart from ordinary {LanguageLabel(locale)} words " +
              "reliably enough to be safe — it would risk changing words you said correctly — so it " +
              "leaves your dictations alone. Your list is saved."
            : $"Jot has no everyday-word list for {LanguageLabel(locale)}, and without one it can't " +
              "tell your term from an ordinary word — so it leaves your dictations alone. Your list " +
              "is saved.";
    }

    /// <summary>The badge next to the section title. It has to state the limit for the users the
    /// feature DOES work for, because the InfoBar above only appears when it doesn't.</summary>
    public string VocabularyModeBadge => VocabularyMode switch
    {
        VocabularyRunner.VocabularyMode.Acoustic => "Experimental · sound-alike matching",
        VocabularyRunner.VocabularyMode.Textual => "Experimental · spelling matching",
        _ => "Experimental",
    };

    private const string VocabularyLead =
        "Words Jot should get right — names, products, jargon. Runs entirely on this PC. ";

    public string VocabularyModeDescription => VocabularyMode switch
    {
        VocabularyRunner.VocabularyMode.Acoustic => VocabularyLead +
            "In English, Jot listens for your terms in the audio as well as checking the spelling.",
        VocabularyRunner.VocabularyMode.Textual => VocabularyLead +
            "Outside English, Jot fixes near-miss spellings of your terms; it can't listen for them.",
        _ => VocabularyLead + "Your current language isn't covered — see above.",
    };

    /// <summary>Model state, as the user's problem rather than ours. The headline is deliberately
    /// "Vocabulary unavailable — …", not "Download failed": the same state is reached by a tokenizer
    /// error on an already-downloaded bundle, and by a model that was simply never fetched.
    ///
    /// The copy used to say "this version of Jot can't fetch it", which was true while nothing called
    /// <c>CtcModelInstaller</c> and its release tag was uncut. Both are now false — the release is live
    /// and the row below downloads it — so the copy points at the button instead. If a future build
    /// ever drops the fetch path again, this string is the thing that has to go back.</summary>
    public bool VocabularyModelReady => _spotter.IsReady;

    /// <summary>The warning bar's gate. A running download is NOT "unavailable" — the row underneath is
    /// already showing MB-of-MB progress, and a warning sitting on top of it reads as a failure. Nor is
    /// a language that never uses the model: warning a Spanish user about a missing English checkpoint
    /// would be advertising a download that would do nothing for them.</summary>
    public bool VocabularyModelUnavailable =>
        VocabularyLanguageOk && !VocabularyModelReady && !_vocabularyDownload.IsDownloading;

    /// <summary>The InfoBar's BODY only — the "Vocabulary unavailable" headline lives in its Title, so
    /// repeating it here printed the phrase twice on screen.</summary>
    public string VocabularyModelStatus => VocabularyModelReady
        ? "Vocabulary model ready"
        : $"This feature needs an extra on-device model (about {CtcModelDownload.SizeMb} MB) that isn't " +
          "on this PC yet. Your terms are saved; download it below and they start working.";

    private static string LanguageLabel(string code)
    {
        string norm = NemotronLocales.Normalize(code);
        NemotronLocale? l = NemotronLocales.All.FirstOrDefault(x => x.Code == norm);
        if (l is null) return norm;
        return l.EnglishName == l.NativeName ? l.EnglishName : $"{l.EnglishName} — {l.NativeName}";
    }

    private void RaiseVocabularyComputed()
    {
        OnPropertyChanged(nameof(VocabularyTermCount));
        OnPropertyChanged(nameof(VocabularyTermCountText));
        OnPropertyChanged(nameof(VocabularyLanguageOk));
        OnPropertyChanged(nameof(VocabularyMode));
        OnPropertyChanged(nameof(VocabularyModeBadge));
        OnPropertyChanged(nameof(VocabularyModeDescription));
        OnPropertyChanged(nameof(VocabularyLanguageBlocked));
        OnPropertyChanged(nameof(VocabularyLanguageIsAuto));
        OnPropertyChanged(nameof(VocabularyLanguageTitle));
        OnPropertyChanged(nameof(VocabularyLanguageMessage));
        OnPropertyChanged(nameof(VocabularyModelReady));
        OnPropertyChanged(nameof(VocabularyModelUnavailable));
        OnPropertyChanged(nameof(VocabularyModelStatus));
    }
}
