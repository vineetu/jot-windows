namespace Jot.Services.Abstractions;

/// <summary>How Jot picks light vs dark. Default follows the OS personalization setting.</summary>
public enum AppThemeMode { System, Light, Dark }

/// <summary>
/// The full user-settings surface for the Windows app. One flat POCO serialized to JSON
/// under %LOCALAPPDATA%\Jot\settings.json. Fields span every Settings pane; panes bind to
/// this and call <see cref="ISettingsStore.Save"/> on change. Engine/network-backed values
/// (model download state, AI reachability) live in their own stubbed services, not here.
/// </summary>
public sealed class JotSettings
{
    // Appearance
    public AppThemeMode Theme { get; set; } = AppThemeMode.System;

    // General
    public bool AdvancedFeatures { get; set; }
    public bool LaunchAtLogin { get; set; }
    public int RetentionDays { get; set; } = 7;       // 0 = forever
    public bool SemanticSearch { get; set; } = true;
    public string? InputDeviceId { get; set; }

    // Transcription / output
    public string Language { get; set; } = "English";
    public string TranscriptionDevice { get; set; } = "CPU"; // CPU | GPU (DirectML) — encoder execution provider
    public bool LiveCaptions { get; set; } = true;            // show a running transcript in the pill while recording
    public bool AutoPaste { get; set; } = true;
    public bool AutoEnter { get; set; }
    public bool KeepInClipboard { get; set; }
    public bool ReturnToOrigin { get; set; }

    // AI (no Apple Intelligence on Windows — user must pick a provider)
    public string AiProvider { get; set; } = "None"; // None | OpenAI | Anthropic | Gemini | Ollama
    public string? AiBaseUrl { get; set; }
    public string? AiModel { get; set; }
    public bool CleanupEnabled { get; set; }

    // Sounds
    public bool SoundStart { get; set; } = true;
    public bool SoundStop { get; set; } = true;
    public bool SoundCancel { get; set; } = true;
    public bool SoundSuccess { get; set; } = true;
    public bool SoundError { get; set; } = true;

    // Shortcuts (human-readable chord strings; parsed by the hotkey service)
    public string ToggleRecordingHotkey { get; set; } = "Alt+Space";
    public string? PushToTalkHotkey { get; set; }
    public string PasteLastHotkey { get; set; } = "Alt+OemComma";
    public string RewriteHotkey { get; set; } = "Alt+OemQuestion";
    public string RewriteWithVoiceHotkey { get; set; } = "Alt+OemPeriod";

    // Lifecycle
    public bool FirstRunComplete { get; set; }
    public bool ShowSampleData { get; set; } = true; // seed the demo library until real recordings exist
}

/// <summary>Loads, exposes, and persists <see cref="JotSettings"/>; raises <see cref="Changed"/> after a save.</summary>
public interface ISettingsStore
{
    JotSettings Current { get; }
    void Save();
    event EventHandler? Changed;
}
