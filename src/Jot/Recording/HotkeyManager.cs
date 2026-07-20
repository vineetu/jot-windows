using Jot.Services.Abstractions;

namespace Jot.Recording;

/// <summary>
/// Owns every global hotkey and keeps them in sync with <see cref="JotSettings"/>. Chords are stored
/// as human strings in settings and (re)registered here, so rebinding takes effect immediately —
/// <see cref="Rebuild"/> tears down the old registrations and re-reads the chords.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly LowLevelHotkeys _lowLevel;

    private GlobalHotkey? _toggle;
    private GlobalHotkey? _pasteLast;
    private GlobalHotkey? _rewrite;
    private GlobalHotkey? _rewriteWithVoice;

    /// <summary>Raised when a chord couldn't be registered (already taken / invalid): (label, reason).</summary>
    public event Action<string, string>? RegistrationFailed;

    public event Action? ToggleRecording;
    public event Action? PasteLast;
    public event Action? Rewrite;
    public event Action? RewriteWithVoice;

    public HotkeyManager(ISettingsStore settings)
    {
        _settings = settings;
        _lowLevel = new LowLevelHotkeys(System.Windows.Application.Current.Dispatcher);
    }

    /// <summary>(Re)registers all hotkeys from current settings. Safe to call repeatedly.</summary>
    public void Rebuild()
    {
        DisposeHotkeys();
        JotSettings s = _settings.Current;

        // Bare special keys (Apps, F-keys, locks) go through the suppressing low-level hook so their
        // native behaviour (e.g. the Apps key's context menu) can't leak; everything else uses
        // RegisterHotKey. Collect the bare-key binds first, then hand them to the hook in one shot.
        var bareBinds = new List<(uint vk, Action action)>();

        _toggle = Register(s.ToggleRecordingHotkey, "Toggle recording", 1, () => ToggleRecording?.Invoke(), bareBinds);
        _pasteLast = Register(s.PasteLastHotkey, "Paste last transcript", 2, () => PasteLast?.Invoke(), bareBinds);
        _rewrite = Register(s.RewriteHotkey, "Rewrite", 3, () => Rewrite?.Invoke(), bareBinds);
        _rewriteWithVoice = Register(s.RewriteWithVoiceHotkey, "Rewrite with voice", 4, () => RewriteWithVoice?.Invoke(), bareBinds);

        _lowLevel.SetBindings(bareBinds);
    }

    /// <summary>Registers one chord. Bare special keys are routed to the suppressing low-level hook
    /// (added to <paramref name="bareBinds"/>, returns null); other chords use RegisterHotKey.</summary>
    private GlobalHotkey? Register(string? chordText, string label, int id, Action onPressed,
        List<(uint vk, Action action)> bareBinds)
    {
        if (!HotkeyChord.TryParse(chordText, out HotkeyChord chord))
        {
            if (!string.IsNullOrWhiteSpace(chordText))
                RegistrationFailed?.Invoke(label, $"“{chordText}” isn't a valid shortcut.");
            return null;
        }

        if (LowLevelHotkeys.IsSuppressableBareKey(chord))
        {
            bareBinds.Add((chord.VirtualKey, onPressed));
            return null;
        }

        try
        {
            var hk = new GlobalHotkey(chord.Modifiers, chord.VirtualKey, id);
            hk.Pressed += onPressed;
            return hk;
        }
        catch (Exception ex)
        {
            RegistrationFailed?.Invoke(label, ex.Message);
            return null;
        }
    }

    private void DisposeHotkeys()
    {
        _toggle?.Dispose(); _toggle = null;
        _pasteLast?.Dispose(); _pasteLast = null;
        _rewrite?.Dispose(); _rewrite = null;
        _rewriteWithVoice?.Dispose(); _rewriteWithVoice = null;
        _lowLevel.SetBindings([]); // drop any bare-key bindings + uninstall the hook if now empty
    }

    public void Dispose()
    {
        DisposeHotkeys();
        _lowLevel.Dispose();
    }
}
