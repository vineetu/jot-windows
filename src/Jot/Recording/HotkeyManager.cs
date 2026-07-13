using Jot.Services.Abstractions;

namespace Jot.Recording;

/// <summary>
/// Owns every global hotkey and keeps them in sync with <see cref="JotSettings"/>. Chords are stored
/// as human strings in settings and (re)registered here, so rebinding in Settings takes effect
/// immediately — <see cref="Rebuild"/> tears down the old registrations and re-reads the chords.
/// The toggle-recording hotkey is always active; the advanced paste/rewrite hotkeys register only
/// when advanced features are enabled.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly ISettingsStore _settings;

    private GlobalHotkey? _toggle;
    private GlobalHotkey? _pasteLast;
    private GlobalHotkey? _rewrite;
    private GlobalHotkey? _rewriteVoice;

    /// <summary>Raised when a chord couldn't be registered (already taken / invalid): (label, reason).</summary>
    public event Action<string, string>? RegistrationFailed;

    public event Action? ToggleRecording;
    public event Action? PasteLast;
    public event Action? Rewrite;
    public event Action? RewriteWithVoice;

    public HotkeyManager(ISettingsStore settings) => _settings = settings;

    /// <summary>(Re)registers all hotkeys from current settings. Safe to call repeatedly.</summary>
    public void Rebuild()
    {
        DisposeHotkeys();
        JotSettings s = _settings.Current;

        _toggle = Register(s.ToggleRecordingHotkey, "Toggle recording", 1, () => ToggleRecording?.Invoke());

        if (s.AdvancedFeatures)
        {
            _pasteLast = Register(s.PasteLastHotkey, "Paste last transcription", 2, () => PasteLast?.Invoke());
            _rewrite = Register(s.RewriteHotkey, "Rewrite", 3, () => Rewrite?.Invoke());
            _rewriteVoice = Register(s.RewriteWithVoiceHotkey, "Rewrite with voice", 4, () => RewriteWithVoice?.Invoke());
        }
    }

    private GlobalHotkey? Register(string? chordText, string label, int id, Action onPressed)
    {
        if (!HotkeyChord.TryParse(chordText, out HotkeyChord chord))
        {
            if (!string.IsNullOrWhiteSpace(chordText))
                RegistrationFailed?.Invoke(label, $"“{chordText}” isn't a valid shortcut.");
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
        _rewriteVoice?.Dispose(); _rewriteVoice = null;
    }

    public void Dispose() => DisposeHotkeys();
}
