using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jot.Services.Abstractions;
using NAudio.CoreAudioApi;

namespace Jot.ViewModels;

/// <summary>
/// Drives the first-run setup wizard. Six steps: Welcome, Permissions (mic privacy), Language,
/// Microphone, Shortcut, Done. Skippable and re-runnable. Completing it marks first-run done and
/// flips Advanced features on (matching the Mac app), then requests the window close.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    public const int StepCount = 6;

    private readonly ISettingsStore _store;

    public string[] Languages { get; } =
        ["English", "Spanish", "French", "German", "Italian", "Portuguese", "Japanese"];
    public ObservableCollection<AudioInputDevice> InputDevices { get; } = new();

    [ObservableProperty] private int _stepIndex;
    [ObservableProperty] private string _language = "English";
    [ObservableProperty] private AudioInputDevice? _selectedDevice;

    public event Action? CloseRequested;

    public WizardViewModel(ISettingsStore store)
    {
        _store = store;
        _language = store.Current.Language;
        LoadDevices();
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
        2 => "What language will you speak?",
        3 => "Choose your microphone",
        4 => "Your dictation shortcut",
        _ => "You're all set",
    };

    public string StepSubtitle => StepIndex switch
    {
        0 => "Free, on-device dictation for Windows. Press a key, speak, and your words are pasted at the cursor.",
        1 => "Jot needs permission to use your microphone. Windows has no separate input-monitoring or accessibility prompt.",
        2 => "Jot downloads the right on-device model for the language you pick. Model names stay out of your way.",
        3 => "Pick the input device to record from. The meter below confirms Jot can hear you.",
        4 => "Press Alt + Space anywhere to start and stop dictation. You can change this later in Settings.",
        _ => "You can re-run this anytime from Settings. Press Alt + Space to try your first dictation.",
    };

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
    }

    partial void OnLanguageChanged(string value) { _store.Current.Language = value; _store.Save(); }
    partial void OnSelectedDeviceChanged(AudioInputDevice? value)
    {
        _store.Current.InputDeviceId = value?.Id;
        _store.Save();
    }

    [RelayCommand]
    private void Next()
    {
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
        _store.Current.AdvancedFeatures = true; // completing setup reveals power-user surfaces
        _store.Save();
        CloseRequested?.Invoke();
    }
}
