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
    public int RetentionDays { get; set; } = 7;       // 0 = forever; older recordings are pruned on launch
    public string? InputDeviceId { get; set; }

    /// <summary>Folder where recordings + the transcript library are saved. Null = the default
    /// (%LOCALAPPDATA%\Jot). Chosen in the setup wizard and changeable in Settings.</summary>
    public string? DataDirectory { get; set; }

    // Transcription / output
    public string Language { get; set; } = "English";
    public string TranscriptionDevice { get; set; } = "CPU"; // CPU | GPU (DirectML) — encoder execution provider
    public bool LiveCaptions { get; set; } = true;            // show a running transcript in the pill while recording
    public bool OfflineCleanupEnabled { get; set; } = true;   // on-device, non-AI tidy of every transcript (filler/casing/numbers)
    public bool AutoPaste { get; set; } = true;
    public bool AutoEnter { get; set; }
    public bool KeepInClipboard { get; set; }
    public bool ReturnToOrigin { get; set; }

    // AI (no Apple Intelligence on Windows — user must pick a provider)
    public string AiProvider { get; set; } = "None"; // None | OpenAI | Anthropic | Gemini | Ollama
    public string? AiBaseUrl { get; set; }
    public string? AiModel { get; set; }

    // Sounds
    public bool SoundStart { get; set; } = true;
    public bool SoundStop { get; set; } = true;
    public bool SoundCancel { get; set; } = true;
    public bool SoundSuccess { get; set; } = true;
    public bool SoundError { get; set; } = true;

    // Shortcuts (human-readable chord strings; parsed by HotkeyChord and registered by HotkeyManager)
    public string ToggleRecordingHotkey { get; set; } = "Alt+Space";
    public string CancelRecordingHotkey { get; set; } = "Escape"; // armed only while recording
    public string? PushToTalkHotkey { get; set; }
    public string PasteLastHotkey { get; set; } = "Ctrl+Alt+V"; // paste last transcript (rewrite or raw) at the cursor
    public string RewriteHotkey { get; set; } = "Ctrl+Alt+OemQuestion";   // rewrite selection: Ctrl+Alt+/
    public string RewriteWithVoiceHotkey { get; set; } = "Ctrl+Alt+OemPeriod"; // rewrite with voice: Ctrl+Alt+.

    // Lifecycle
    public bool FirstRunComplete { get; set; }
    public bool ShowSampleData { get; set; } = true; // seed the demo library until real recordings exist

    // Donation nudge — one-time "you've saved ~1h" prompt. Terminal once dismissed-forever or donated;
    // SnoozedAt records a "maybe later" so it re-asks later at a higher bar.
    public bool DonationNudgeDone { get; set; }
    public DateTime? DonationNudgeSnoozedAt { get; set; }
}

/// <summary>Loads, exposes, and persists <see cref="JotSettings"/>; raises <see cref="Changed"/> after a save.</summary>
public interface ISettingsStore
{
    JotSettings Current { get; }
    void Save();

    /// <summary>Resets every setting to its default and persists — used by "Reset settings".</summary>
    void Reset();

    event EventHandler? Changed;
}
