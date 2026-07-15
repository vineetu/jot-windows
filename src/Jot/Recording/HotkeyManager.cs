using Jot.Services.Abstractions;

namespace Jot.Recording;

/// <summary>
/// Owns every global hotkey and keeps them in sync with <see cref="JotSettings"/>. Chords are stored
/// as human strings in settings and (re)registered here, so rebinding in Settings takes effect
/// immediately — <see cref="Rebuild"/> tears down the old registrations and re-reads the chords.
/// Rewrite / paste-last / rewrite-with-voice were disabled 2026-07-14 (worklist A4) on the belief that
/// selection capture (synthetic Ctrl+C) was fundamentally unreliable on Windows 11. Re-enabled the same
/// day after `--rewriteselftest` proved the real <see cref="Rewrite.RewriteController"/> pipeline
/// (capture → AI rewrite → paste-back) passes end-to-end against a target with its own message pump —
/// the earlier "broken" verdict was very likely a same-thread self-test artifact, not a real Windows 11
/// limitation (see fixit-worklist AI Rewrite section). Still wants a hands-on test against a real
/// foreign app (Notepad/browser/Office) for full confidence; reopen A4 if that fails.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly ISettingsStore _settings;

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

    public HotkeyManager(ISettingsStore settings) => _settings = settings;

    /// <summary>(Re)registers all hotkeys from current settings. Safe to call repeatedly.</summary>
    public void Rebuild()
    {
        DisposeHotkeys();
        JotSettings s = _settings.Current;

        _toggle = Register(s.ToggleRecordingHotkey, "Toggle recording", 1, () => ToggleRecording?.Invoke());
        _pasteLast = Register(s.PasteLastHotkey, "Paste last transcript", 2, () => PasteLast?.Invoke());
        _rewrite = Register(s.RewriteHotkey, "Rewrite", 3, () => Rewrite?.Invoke());
        _rewriteWithVoice = Register(s.RewriteWithVoiceHotkey, "Rewrite with voice", 4, () => RewriteWithVoice?.Invoke());
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
        _rewriteWithVoice?.Dispose(); _rewriteWithVoice = null;
    }

    public void Dispose() => DisposeHotkeys();
}
