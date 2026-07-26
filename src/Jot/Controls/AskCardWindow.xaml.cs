using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Jot.Vocabulary;

namespace Jot.Controls;

/// <summary>
/// The "did you mean?" ask (ux §4) — a sibling of <see cref="PromptPickerWindow"/>, NEVER a change
/// to <see cref="PillWindow"/> (D9).
///
/// WHY A SEPARATE WINDOW. The pill is <c>WS_EX_NOACTIVATE</c> and must stay that way forever: the
/// paste path's whole focus model depends on it never becoming foreground. This window sets
/// <c>WS_EX_TOOLWINDOW</c> only — activatable, keyboard-first — exactly like the prompt picker
/// already does on the same paste path. No low-level hook, no change to the pill.
///
/// THE REAL HAZARD IS THE PASTE TARGET, not the focus flag. With an activatable window up, the
/// foreground window at paste time is Jot's own, and <c>PasteAtCursor</c>'s <c>IntPtr.Zero</c> path
/// resolves the target with <c>GetForegroundWindow()</c> — so the transcript would be pasted into
/// this card. <c>RecorderController</c> therefore forces <c>_originWindow</c> as the paste target
/// from the moment the ask block is ENTERED, error path included, and closes any live card in a
/// <c>finally</c> before pasting.
///
/// RESOLUTION IS DELIVERY. <c>Deactivated</c> must resolve-and-deliver, never merely close: the Mac
/// round-1 blocker was a dismiss path that hid the card WITHOUT delivering, so the held paste
/// silently never landed. Every exit — answer, Esc, click-away, timeout, a new recording — completes
/// the one <see cref="TaskCompletionSource{TResult}"/>.
/// </summary>
public partial class AskCardWindow : Window
{
    /// <summary>Per-card countdown. In the design's 6–10 s band, at the calm end because the card is
    /// a confirmation of something already done, not a request for permission.</summary>
    public const int DefaultCardMs = 8_000;

    /// <summary>The one live deck, so a new recording can force-resolve it. Dictation is serial, so
    /// there is never more than one.</summary>
    private static AskCardWindow? _live;

    private readonly AskDeck _deck;
    private readonly TaskCompletionSource<IReadOnlyList<AskAnswer>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _tick;
    private readonly int _cardMs;
    private readonly IntPtr _anchorWindow;
    private DateTime _cardStartedAt;
    private bool _resolved;

    private AskCardWindow(AskDeck deck, IntPtr anchorWindow, int cardMs)
    {
        InitializeComponent();
        _deck = deck;
        _anchorWindow = anchorWindow;
        _cardMs = cardMs;

        SourceInitialized += OnSourceInitialized;
        // NOT "if (CloseOnDeactivate) Close()" like the picker: clicking back into the document means
        // "I'm done" — it must deliver what has been answered so far, not drop the held paste.
        // (ResolveOnDeactivate is off ONLY for --askcarddemo, mirroring the picker's CloseOnDeactivate.)
        Deactivated += (_, _) => { if (ResolveOnDeactivate) Resolve(); };

        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += OnTick;
        Loaded += OnLoaded;
        // SizeToContent means ActualHeight is still 0 at Loaded, so positioning once would park the
        // card's TOP on the taskbar edge and push the whole thing off-screen. Same fix the pill uses.
        SizeChanged += (_, _) => PositionOnAnchorMonitor();
    }

    /// <summary>
    /// Show the deck and complete when it is done, however it ends. Always completes — that is the
    /// contract the caller's paste depends on.
    /// </summary>
    /// <summary>Off only for the demo switch: a screenshot pass steals focus, and every exit path
    /// resolving means the card would vanish before it can be looked at.</summary>
    public bool ResolveOnDeactivate { get; set; } = true;

    public static Task<IReadOnlyList<AskAnswer>> RunAsync(
        IReadOnlyList<AskPolicy.Selection> deck, IntPtr anchorWindow, int cardMs = DefaultCardMs,
        bool resolveOnDeactivate = true)
    {
        if (deck.Count == 0) return Task.FromResult<IReadOnlyList<AskAnswer>>([]);

        // A second deck can only mean the first was abandoned; force-resolve it rather than stacking.
        ForceResolveLive();

        var window = new AskCardWindow(new AskDeck(deck), anchorWindow, cardMs)
        {
            ResolveOnDeactivate = resolveOnDeactivate,
        };
        _live = window;
        window.Show();
        window.Activate();
        return window._completion.Task;
    }

    /// <summary>A new recording (or the delivery path's <c>finally</c>) force-resolves any live deck
    /// BEFORE doing anything else: <c>Toggle</c> / <c>PressToStart</c> are global and reachable while
    /// the card is up.</summary>
    public static void ForceResolveLive() => _live?.Resolve();

    /// <summary>True while a card is on screen — the caller uses it to force the paste target.</summary>
    public static bool IsLive => _live is not null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionOnAnchorMonitor();
        ShowCurrentCard();
    }

    private void ShowCurrentCard()
    {
        if (_deck.Current is not { } selection) { Resolve(); return; }
        CorrectionRecord r = selection.Record;

        ContextLine.Text = ContextFor(r);
        Headline.Text = $"Jot wrote {r.Term}.";
        PositionText.Text = _deck.PositionText;
        OriginalChip.Content = r.OriginalWord;
        TermChip.Content = r.Term + "  ✓";
        AutomationProperties.SetName(OriginalChip, $"Use {r.OriginalWord} — occurrence {_deck.Index + 1}");
        AutomationProperties.SetName(TermChip, $"Use {r.Term} — occurrence {_deck.Index + 1}");
        AutomationProperties.SetName(this, $"Jot wrote {r.Term}. {_deck.PositionText}.");

        // Default focus is the APPLIED term, so pressing Enter agrees with what the timeout would do.
        // (Focusing the revert chip would make the fastest possible interaction the destructive one.)
        TermChip.Focus();

        _cardStartedAt = DateTime.UtcNow;
        Countdown.Progress = 100;
        _tick.Start();   // ALWAYS: reduced motion stops the ring moving, never the clock
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _cardStartedAt).TotalMilliseconds;
        if (SystemParameters.ClientAreaAnimation)
            Countdown.Progress = Math.Max(0, 100 - (elapsed / _cardMs * 100));
        if (elapsed < _cardMs) return;

        // §4.5 · card 1 with ZERO interaction skips the WHOLE deck — don't march the user through
        // 3×8 s they are ignoring. After any interaction, only this card is skipped. No banner
        // either way: a banner is itself a nag.
        _deck.TimeOut();
        Advance();
    }

    private void Advance()
    {
        _tick.Stop();
        if (_deck.IsComplete) { Resolve(); return; }
        ShowCurrentCard();
    }

    private void OnPickOriginal(object sender, RoutedEventArgs e) { _deck.Answer(keepOriginal: true); Advance(); }

    private void OnPickTerm(object sender, RoutedEventArgs e) { _deck.Answer(keepOriginal: false); Advance(); }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: OriginalChip.Focus(); e.Handled = true; break;
            case Key.Right: TermChip.Focus(); e.Handled = true; break;
            case Key.Enter:
                _deck.Answer(keepOriginal: OriginalChip.IsKeyboardFocusWithin);
                Advance();
                e.Handled = true;
                break;
            case Key.Escape:
                // Keep what is already in the text and move on, writing nothing. Esc is free here:
                // DisarmStopHotkey has already run by the time this window can exist.
                _deck.Skip();
                Advance();
                e.Handled = true;
                break;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Every exit path lands here. Idempotent, and it CLOSES as well as completing, so no
    /// activatable window can be left in front of the paste.</summary>
    private void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        _tick.Stop();
        _deck.Abandon();
        if (ReferenceEquals(_live, this)) _live = null;
        try { Close(); } catch { /* already closing */ }
        _completion.TrySetResult(_deck.Answers);
    }

    // MARK: - Window recipe (copied from PromptPickerWindow, minus its centering)

    // Tool window so the card stays out of Alt+Tab; still activatable (no NOACTIVATE) for the
    // keyboard. The pill's own flags are untouched, and must stay that way.
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr h = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    // Bottom-center on the DICTATION monitor, like the pill — the card appears where the user is
    // already looking, not centered like the prompt palette.
    private void PositionOnAnchorMonitor()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        IntPtr fg = _anchorWindow != IntPtr.Zero ? _anchorWindow : GetForegroundWindow();
        IntPtr mon = MonitorFromWindow(fg != IntPtr.Zero ? fg : handle, MONITOR_DEFAULTTONEAREST);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double workLeft = mi.rcWork.Left / dpi.DpiScaleX;
        double workRight = mi.rcWork.Right / dpi.DpiScaleX;
        double workBottom = mi.rcWork.Bottom / dpi.DpiScaleY;

        Left = workLeft + ((workRight - workLeft) - ActualWidth) / 2.0;
        Top = workBottom - ActualHeight - 24;
    }

    private static string ContextFor(CorrectionRecord r) => $"…{r.OriginalWord} → {r.Term}…";

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
