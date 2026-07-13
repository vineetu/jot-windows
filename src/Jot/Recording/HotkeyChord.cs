using System.Windows.Input;

namespace Jot.Recording;

/// <summary>
/// A parsed keyboard chord — modifier flags plus a single key — with conversion to/from the
/// human-readable strings stored in settings ("Alt+Space", "Ctrl+Shift+F13"). The key part uses
/// WPF <see cref="Key"/> names, so a chord round-trips through <see cref="KeyInterop"/> to the
/// Win32 virtual-key code that <see cref="GlobalHotkey"/> needs.
/// </summary>
public readonly record struct HotkeyChord(GlobalHotkey.Modifiers Modifiers, Key Key)
{
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public bool IsValid => Key != Key.None;

    /// <summary>Parses "Alt+Space" style strings. Returns false for empty/garbage input.</summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var mods = GlobalHotkey.Modifiers.None;
        Key key = Key.None;

        foreach (string p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= GlobalHotkey.Modifiers.Control; break;
                case "alt": mods |= GlobalHotkey.Modifiers.Alt; break;
                case "shift": mods |= GlobalHotkey.Modifiers.Shift; break;
                case "win" or "windows" or "meta": mods |= GlobalHotkey.Modifiers.Win; break;
                default:
                    if (!Enum.TryParse(p, ignoreCase: true, out Key parsed)) return false;
                    key = parsed;
                    break;
            }
        }

        if (key == Key.None) return false;
        chord = new HotkeyChord(mods, key);
        return true;
    }

    /// <summary>Builds a chord from a live key press. The caller must have already resolved
    /// <see cref="Key.System"/> (Alt-held) to the real key via <c>e.SystemKey</c>, and must exclude
    /// presses where <paramref name="key"/> is itself a bare modifier.</summary>
    public static HotkeyChord FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        var mods = GlobalHotkey.Modifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= GlobalHotkey.Modifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= GlobalHotkey.Modifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= GlobalHotkey.Modifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= GlobalHotkey.Modifiers.Win;
        return new HotkeyChord(mods, key);
    }

    /// <summary>The stored/canonical form using WPF <see cref="Key"/> names so it round-trips
    /// through <see cref="TryParse"/> — e.g. "Alt+Space", "Ctrl+Shift+F13".</summary>
    public override string ToString()
    {
        var sb = new List<string>();
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Control)) sb.Add("Ctrl");
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Alt)) sb.Add("Alt");
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Shift)) sb.Add("Shift");
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Win)) sb.Add("Win");
        if (Key != Key.None) sb.Add(Key.ToString());
        return string.Join("+", sb);
    }

    /// <summary>A friendlier label for display ("Alt + Space"): pretty key name, spaced separators.</summary>
    public string ToDisplayString()
    {
        var sb = new List<string>();
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Control)) sb.Add("Ctrl");
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Alt)) sb.Add("Alt");
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Shift)) sb.Add("Shift");
        if (Modifiers.HasFlag(GlobalHotkey.Modifiers.Win)) sb.Add("Win");
        if (Key != Key.None) sb.Add(Prettify(Key));
        return string.Join(" + ", sb);
    }

    private static string Prettify(Key key) => key switch
    {
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        Key.OemQuestion => "Slash",
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Space => "Space",
        _ => key.ToString(),
    };
}
