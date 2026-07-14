using Jot.Services.Abstractions;

namespace Jot.Recording;

/// <summary>
/// Owns every global hotkey and keeps them in sync with <see cref="JotSettings"/>. Chords are stored
/// as human strings in settings and (re)registered here, so rebinding in Settings takes effect
/// immediately — <see cref="Rebuild"/> tears down the old registrations and re-reads the chords.
/// Only the toggle-recording hotkey is registered. Rewrite / paste-last / rewrite-with-voice are
/// disabled: they don't work on Windows 11 yet (selection capture + paste is unreliable), so we don't
/// grab global keys for dead features. Re-enable once they actually work — see fixit-worklist A4.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly ISettingsStore _settings;

    private GlobalHotkey? _toggle;

    /// <summary>Raised when a chord couldn't be registered (already taken / invalid): (label, reason).</summary>
    public event Action<string, string>? RegistrationFailed;

    public event Action? ToggleRecording;
    // Kept for when rewrite/paste-last are revived (worklist A4); not raised while those hotkeys are
    // disabled, so suppress the "event never used" warning rather than deleting the public surface.
#pragma warning disable CS0067
    public event Action? PasteLast;
    public event Action? Rewrite;
    public event Action? RewriteWithVoice;
#pragma warning restore CS0067

    public HotkeyManager(ISettingsStore settings) => _settings = settings;

    /// <summary>(Re)registers all hotkeys from current settings. Safe to call repeatedly.</summary>
    public void Rebuild()
    {
        DisposeHotkeys();
        JotSettings s = _settings.Current;

        _toggle = Register(s.ToggleRecordingHotkey, "Toggle recording", 1, () => ToggleRecording?.Invoke());
        // Rewrite / rewrite-with-voice / paste-last are intentionally NOT registered: they don't work
        // on Windows 11 (selection capture + paste is unreliable), so registering global keys for them
        // just grabs shortcuts for dead features. Re-enable when the transform works. See worklist A4.
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
    }

    public void Dispose() => DisposeHotkeys();
}
