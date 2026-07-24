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
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    private GlobalHotkey? _toggle;
    private GlobalHotkey? _pasteLast;
    private GlobalHotkey? _rewrite;
    private GlobalHotkey? _rewriteWithVoice;
    private GlobalHotkey? _pushToTalk;          // chord form: press via RegisterHotKey…
    private PassiveKeyMonitor? _pttMonitor;     // …release (and bare-modifier press) via passive watcher

    /// <summary>Raised when a chord couldn't be registered (already taken / invalid): (label, reason).</summary>
    public event Action<string, string>? RegistrationFailed;

    public event Action? ToggleRecording;
    public event Action? PasteLast;
    public event Action? Rewrite;
    public event Action? RewriteWithVoice;
    public event Action? PushToTalkPressed;
    public event Action? PushToTalkReleased;

    public HotkeyManager(ISettingsStore settings)
    {
        _settings = settings;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
        _lowLevel = new LowLevelHotkeys(_dispatcher);
    }

    /// <summary>(Re)registers all hotkeys from current settings. Safe to call repeatedly.</summary>
    public void Rebuild()
    {
        DisposeHotkeys();
        JotSettings s = _settings.Current;

        // Bare special keys (Apps, F-keys, locks) go through the suppressing low-level hook so their
        // native behaviour (e.g. the Apps key's context menu) can't leak; everything else uses
        // RegisterHotKey. Collect the bare-key binds first, then hand them to the hook in one shot.
        var bareBinds = new List<(uint vk, Action down, Action? up)>();

        _toggle = Register(s.ToggleRecordingHotkey, "Toggle recording", 1, () => ToggleRecording?.Invoke(), bareBinds);
        _pasteLast = Register(s.PasteLastHotkey, "Paste last transcript", 2, () => PasteLast?.Invoke(), bareBinds);
        _rewrite = Register(s.RewriteHotkey, "Rewrite", 3, () => Rewrite?.Invoke(), bareBinds);
        _rewriteWithVoice = Register(s.RewriteWithVoiceHotkey, "Rewrite with voice", 4, () => RewriteWithVoice?.Invoke(), bareBinds);
        RegisterPushToTalk(s.PushToTalkHotkey, bareBinds);

        _lowLevel.SetBindings(bareBinds);
    }

    /// <summary>
    /// The hold binding needs BOTH edges, which no single existing mechanism delivers for every key
    /// class — so it routes by class:
    /// bare special key (F13, Apps…) → the suppressing hook, which already sees both edges;
    /// bare modifier (Right Ctrl…) → a passive watcher for both edges (nothing to suppress — a lone
    ///   modifier has no native action, and RegisterHotKey can't take one anyway);
    /// modifier chord (Ctrl+Alt+D…) → RegisterHotKey for the press (system-wide, no repeat) + a passive
    ///   watcher on the chord's MAIN key for the release (modifier-release order deliberately ignored).
    /// </summary>
    private void RegisterPushToTalk(string? chordText, List<(uint vk, Action down, Action? up)> bareBinds)
    {
        if (string.IsNullOrWhiteSpace(chordText)) return; // optional binding — off by default
        if (!HotkeyChord.TryParse(chordText, out HotkeyChord chord))
        {
            RegistrationFailed?.Invoke("Push to talk", $"“{chordText}” isn't a valid shortcut.");
            return;
        }

        Action down = () => PushToTalkPressed?.Invoke();
        Action up = () => PushToTalkReleased?.Invoke();

        if (LowLevelHotkeys.IsSuppressableBareKey(chord))
        {
            bareBinds.Add((chord.VirtualKey, down, up));
            return;
        }

        if (chord.IsBareModifier)
        {
            _pttMonitor = new PassiveKeyMonitor(_dispatcher, chord.VirtualKey,
                PassiveKeyMonitor.Mode.DownAndUp, down, up);
            return;
        }

        try
        {
            var hk = new GlobalHotkey(chord.Modifiers, chord.VirtualKey, 5);
            hk.Pressed += down;
            _pushToTalk = hk;
            _pttMonitor = new PassiveKeyMonitor(_dispatcher, chord.VirtualKey,
                PassiveKeyMonitor.Mode.UpOnly, null, up);
        }
        catch (Exception ex)
        {
            RegistrationFailed?.Invoke("Push to talk", ex.Message);
        }
    }

    /// <summary>Registers one chord. Bare special keys are routed to the suppressing low-level hook
    /// (added to <paramref name="bareBinds"/>, returns null); other chords use RegisterHotKey.</summary>
    private GlobalHotkey? Register(string? chordText, string label, int id, Action onPressed,
        List<(uint vk, Action down, Action? up)> bareBinds)
    {
        if (!HotkeyChord.TryParse(chordText, out HotkeyChord chord))
        {
            if (!string.IsNullOrWhiteSpace(chordText))
                RegistrationFailed?.Invoke(label, $"“{chordText}” isn't a valid shortcut.");
            return null;
        }

        if (LowLevelHotkeys.IsSuppressableBareKey(chord))
        {
            bareBinds.Add((chord.VirtualKey, onPressed, null));
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
        _pushToTalk?.Dispose(); _pushToTalk = null;
        _pttMonitor?.Dispose(); _pttMonitor = null;
        _lowLevel.SetBindings([]); // drop any bare-key bindings + uninstall the hook if now empty
    }

    public void Dispose()
    {
        DisposeHotkeys();
        _lowLevel.Dispose();
    }
}
