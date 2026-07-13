using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Jot.Recording;

/// <summary>
/// A single system-wide hotkey registered via Win32 <c>RegisterHotKey</c>.
/// Mirrors the Mac app's KeyboardShortcuts binding: the OS delivers a
/// <c>WM_HOTKEY</c> message even when Jot has no focused window, which is what
/// makes dictation work "into any app". Messages are pumped through a hidden
/// <see cref="HwndSource"/> so we don't need a visible window.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [Flags]
    public enum Modifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000, // suppress auto-repeat while the key is held
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id;
    private readonly Modifiers _modifiers;
    private readonly uint _vk;
    private bool _registered;

    /// <summary>Raised on the UI thread each time the hotkey fires.</summary>
    public event Action? Pressed;

    /// <summary>True once the combination is actively registered with the OS.</summary>
    public bool IsRegistered => _registered;

    /// <param name="vk">Virtual-key code (e.g. <c>0x20</c> for Space).</param>
    /// <param name="autoRegister">Register immediately (default). Pass false to arm later with
    /// <see cref="Register"/> — e.g. a cancel key that only listens while recording.</param>
    public GlobalHotkey(Modifiers modifiers, uint vk, int id = 1, bool autoRegister = true)
    {
        _id = id;
        _modifiers = modifiers;
        _vk = vk;

        // Message-only window: invisible, never shown, exists purely to receive WM_HOTKEY.
        var parameters = new HwndSourceParameters("Jot.HotkeyWindow")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        if (autoRegister) Register();
    }

    /// <summary>Registers the combination with the OS. Throws if the OS rejects it (e.g. already taken).</summary>
    public void Register()
    {
        if (_registered) return;
        _registered = RegisterHotKey(_source.Handle, _id, (uint)(_modifiers | Modifiers.NoRepeat), _vk);
        if (!_registered)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"RegisterHotKey failed (Win32 error {err}). The combination may already be taken by another app.");
        }
    }

    /// <summary>Releases the combination so other apps (and Jot) can use the key again.</summary>
    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, _id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
