using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jot.Recording;

namespace Jot.Controls;

/// <summary>
/// A click-to-capture keyboard-shortcut field. Click (or tab) to focus, then press a combination —
/// the chord is written back to the two-way-bound <see cref="Chord"/> string in canonical form
/// ("Alt+Space"). Esc cancels capture without changing the binding; Backspace/Delete clears it.
/// </summary>
public partial class HotkeyBox : UserControl
{
    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnChordChanged));

    /// <summary>The bound chord in canonical form, e.g. "Alt+Space".</summary>
    public string Chord
    {
        get => (string)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public static readonly DependencyProperty AllowBareModifierProperty = DependencyProperty.Register(
        nameof(AllowBareModifier), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    /// <summary>Lets a LONE modifier (Right Ctrl) be captured as the whole chord — the classic
    /// push-to-talk binding. Off by default: for normal shortcuts a bare modifier press is just the
    /// start of a chord. Capture commits on the modifier's key-UP, and only if no real key (which
    /// would have committed a normal chord) and no second modifier is still involved.</summary>
    public bool AllowBareModifier
    {
        get => (bool)GetValue(AllowBareModifierProperty);
        set => SetValue(AllowBareModifierProperty, value);
    }

    private bool _capturing;
    private Key _pendingModifier; // AllowBareModifier: modifier held down, commits on its release

    public HotkeyBox()
    {
        InitializeComponent();
        // Take *keyboard* focus on click. `Focus()` alone only set logical focus within the hosting
        // focus scope (the NavigationView page), so GotKeyboardFocus never fired and capture never
        // started — that was the "clicking does nothing" bug. Keyboard.Focus(this) forces keyboard
        // focus; PreviewMouseLeftButtonDown (tunneling) guarantees we see the click before any child.
        PreviewMouseLeftButtonDown += (_, e) => { Keyboard.Focus(this); e.Handled = true; };
        GotKeyboardFocus += (_, _) => { _capturing = true; _pendingModifier = Key.None; UpdateLabel(); };
        LostKeyboardFocus += (_, _) => { _capturing = false; _pendingModifier = Key.None; UpdateLabel(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        UpdateLabel();
    }

    private static void OnChordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HotkeyBox)d).UpdateLabel();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true; // never let the captured keys reach the app while binding

        // When Alt is held, WPF delivers the real key as SystemKey.
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Clear the binding.
        if (key is Key.Back or Key.Delete)
        {
            Chord = string.Empty;
            Keyboard.ClearFocus();
            return;
        }

        // Cancel capture, keep the existing binding.
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            Keyboard.ClearFocus();
            return;
        }

        // Bare modifier press: normally just the start of a chord (wait for the real key), but in
        // AllowBareModifier mode remember it — its RELEASE commits it as the whole binding.
        if (IsModifier(key))
        {
            if (AllowBareModifier) _pendingModifier = key;
            UpdateLabel();
            return;
        }

        _pendingModifier = Key.None; // a real key arrived — this is a normal chord capture
        HotkeyChord chord = HotkeyChord.FromKeyEvent(key, Keyboard.Modifiers);
        if (chord.IsValid)
        {
            Chord = chord.ToString();
            Keyboard.ClearFocus();
        }
    }

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_capturing || !AllowBareModifier || _pendingModifier == Key.None) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != _pendingModifier) return;
        e.Handled = true;

        // Commit only when nothing else is still held — releasing Ctrl while Shift is down means the
        // user was building a Ctrl+Shift chord, not binding bare Ctrl.
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            Chord = new HotkeyChord(GlobalHotkey.Modifiers.None, _pendingModifier).ToString();
            Keyboard.ClearFocus();
        }
        _pendingModifier = Key.None;
    }

    private static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private void UpdateLabel()
    {
        if (_capturing)
        {
            Label.Text = "Press a shortcut…";
            Root.BorderBrush = Brush("SystemControlHighlightAccentBrush", "AccentControlElevationBorderBrush")
                ?? Root.BorderBrush;
            return;
        }

        Root.BorderBrush = Brush("ControlElevationBorderBrush") ?? Root.BorderBrush;
        Label.Text = HotkeyChord.TryParse(Chord, out HotkeyChord chord)
            ? chord.ToDisplayString()
            : "Unset";
    }

    private Brush? Brush(params string[] keys)
    {
        foreach (string key in keys)
            if (TryFindResource(key) is Brush b) return b;
        return null;
    }
}
