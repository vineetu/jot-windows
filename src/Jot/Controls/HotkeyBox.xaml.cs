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

    private bool _capturing;

    public HotkeyBox()
    {
        InitializeComponent();
        // Take *keyboard* focus on click. `Focus()` alone only set logical focus within the hosting
        // focus scope (the NavigationView page), so GotKeyboardFocus never fired and capture never
        // started — that was the "clicking does nothing" bug. Keyboard.Focus(this) forces keyboard
        // focus; PreviewMouseLeftButtonDown (tunneling) guarantees we see the click before any child.
        PreviewMouseLeftButtonDown += (_, e) => { Keyboard.Focus(this); e.Handled = true; };
        GotKeyboardFocus += (_, _) => { _capturing = true; UpdateLabel(); };
        LostKeyboardFocus += (_, _) => { _capturing = false; UpdateLabel(); };
        PreviewKeyDown += OnPreviewKeyDown;
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

        // Ignore bare modifier presses — wait for the actual key.
        if (IsModifier(key)) { UpdateLabel(); return; }

        HotkeyChord chord = HotkeyChord.FromKeyEvent(key, Keyboard.Modifiers);
        if (chord.IsValid)
        {
            Chord = chord.ToString();
            Keyboard.ClearFocus();
        }
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
