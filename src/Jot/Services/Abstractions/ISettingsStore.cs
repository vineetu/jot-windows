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
    public string Language { get; set; } = "en-US"; // locale code (NemotronLocales); legacy display names still resolve
    public string TranscriptionDevice { get; set; } = "Auto"; // Auto | CPU | GPU (DirectML) — see EngineSelector; existing "CPU" holders migrate to Auto once
    public bool TranscriptionDeviceMigrated { get; set; }     // one-time CPU→Auto migration marker; makes a later explicit CPU choice sticky
    public bool ToggleHotkeyMigrated { get; set; }            // one-time Alt+Space→Ctrl+Shift+Space rescue (Alt+Space = Windows system menu)
    // GPU probe verdict cache — keyed to the exact adapter+driver it was measured on (GpuInfo.CacheKey);
    // a key mismatch (new GPU / driver update) invalidates the verdict and triggers a background re-probe.
    public string? GpuProbeKey { get; set; }
    public string? GpuProbeVerdict { get; set; }              // "GPU" | "CPU"
    public double GpuProbeAvgChunkMs { get; set; }
    public string? GpuProbeReason { get; set; }               // diagnostics text from the probe
    public bool GpuUpgradeBalloonShown { get; set; }          // the one informational "GPU ready" balloon fires once
    public bool LiveCaptions { get; set; } = true;            // show a running transcript in the pill while recording
    public bool OfflineCleanupEnabled { get; set; } = true;   // on-device, non-AI tidy of every transcript (filler/casing/numbers)
    public bool AutoPaste { get; set; } = true;
    public bool AutoEnter { get; set; }
    public bool KeepInClipboard { get; set; }
    public bool ReturnToOrigin { get; set; }
    // How Jot delivers the transcript — see Delivery.TextInjector.PasteMethod. "auto" (default) and "ctrl_v"
    // are the SAME path (the Ctrl+V chord, no fallback ladder behind it); other values: "shift_insert",
    // "type" (synthesise characters), "clipboard" (copy + prompt), "none" (save only).
    public string PasteMethod { get; set; } = "auto";

    // Vocabulary — SHIPS VISIBLE (inside Advanced features) and DEFAULT OFF. Default-off is the whole
    // safety: nothing downloads, nothing loads and no transcript is touched until the user asks. The
    // section is no longer Visibility="Collapsed" — note that also means Settings search reaches it,
    // because search force-reveals the whole Advanced pane. The term list itself lives
    // in <DataDir>\Vocabulary\vocabulary.json, not here — it is user data, not a setting.
    public bool VocabularyEnabled { get; set; }
    public bool VocabularyChipEnabled { get; set; } = true;   // "Tell me when a term is used" — the pill chip

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

    // Shortcuts (human-readable chord strings; parsed by HotkeyChord and registered by HotkeyManager).
    // NOTE: default toggle is Ctrl+Shift+Space, NOT Alt+Space — Alt+Space is the Windows window system-menu
    // shortcut, so it steals focus from the app you're dictating into and the paste lands nowhere. (The
    // Apps/Menu key is another great toggle — bare-key hook fully suppresses its native menu — but not every
    // keyboard has one, so Ctrl+Shift+Space is the universal default.)
    public string ToggleRecordingHotkey { get; set; } = "Ctrl+Shift+Space";
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

    // One-time coach for the Shift+Enter "add a direction" affordance in the rewrite picker: the footer
    // shows a tip for the first few opens (DirectionTipOpens), until the user tries it (DirectionTipDone).
    public bool DirectionTipDone { get; set; }
    public int DirectionTipOpens { get; set; }

    // One-time "quick tour" shown once, right after the setup wizard closes with setup complete. Re-runnable
    // from Help without resetting this. Existing upgraders never see the wizard, so they never see the tour.
    public bool FirstRunTipsDone { get; set; }

    // Per-feature contextual tours already shown, by Tour.Id (see TourCatalog). One JSON-stable list instead
    // of N bools — a new tour just adds an Id, no migration. Getting-started keeps its own FirstRunTipsDone
    // flag above (a separate one-time lifecycle tied to the wizard).
    public List<string> ShownTours { get; set; } = new();

    // One-time behavioural nudge: a real user (≥N dictations) with no AI provider is offered the AI-setup
    // tour once. Set the moment we offer, so it never nags again — whether or not they act on it.
    public bool AiSetupNudgeDone { get; set; }
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
