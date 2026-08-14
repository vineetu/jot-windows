using System.Runtime.InteropServices;
// WinForms interop is enabled project-wide, so alias to the WPF types explicitly.
using Clipboard = System.Windows.Clipboard;
using Application = System.Windows.Application;

namespace Jot.Delivery;

/// <summary>
/// Types transcribed text into the focused app via a "clipboard sandwich": save the user's
/// clipboard, write our text, send a synthetic paste, then restore the original clipboard.
/// STA thread required (WPF UI thread qualifies) — <see cref="Clipboard"/> demands it.
/// </summary>
public static class TextInjector
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort SCAN_CONTROL = 0x1D;
    private const ushort SCAN_C = 0x2E;
    internal const ushort SCAN_A = 0x1E;
    private const ushort SCAN_RETURN = 0x1C;
    internal const ushort SCAN_ALT = 0x38;
    private const ushort SCAN_SHIFT = 0x2A;
    private const ushort VK_V = 0x56;
    private const uint MAPVK_VK_TO_VSC_EX = 4;
    // Unassigned VK injected as the "menu mask". The shell arms a menu (Win → Start, Alt → the focused
    // window's menu bar) only when a Win/Alt down→up pair passes with NO other key between them; this inert
    // keystroke breaks that pair so we can release a held Win/Alt without opening anything. 0xE8 maps to no
    // command anywhere — it is AutoHotkey's mask key, and handy-keys (what Handy ships) uses the same one.
    private const ushort MENU_MASK_VK = 0xE8;
    // Stamped on our injected mask events so a keyboard hook can recognise its own injection ("JOTM").
    private static readonly IntPtr MenuMaskMarker = new(0x4A4F_544D);
    // How long Ctrl stays held after the V click. Most apps read the modifier off the V event's flags and
    // need no hold, but apps that poll global keyboard state while handling the key need Ctrl still down.
    // Matches enigo's ~100 ms hold, which is what Handy ships.
    private const int ChordHoldMs = 100;
    // Handy's paste_delay_ms (its shipping default is 60). Jot's post-paste restore stays at 150 ms rather
    // than Handy's paste_delay_after_ms=60 — deliberately more conservative, since restoring too early is
    // exactly how a slow target ends up pasting the user's OLD clipboard.
    private const int ClipboardSettleMs = 60;

    /// <summary>The foreground window right now — capture this when recording starts so the
    /// transcript can be delivered back to the app the user was in, even if focus drifts.</summary>
    public static IntPtr CaptureForegroundWindow() => GetForegroundWindow();

    /// <summary>A short "'Title' [pid N]" description of a window, for diagnostics logging.</summary>
    public static string DescribeWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "none";
        var sb = new System.Text.StringBuilder(256);
        int len = GetWindowText(hWnd, sb, sb.Capacity);
        GetWindowThreadProcessId(hWnd, out uint pid);
        string title = len > 0 ? sb.ToString() : "(no title)";
        return $"'{title}' [pid {pid}]";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>Outcome of a paste: it landed, or the user's chosen method was <c>Clipboard</c> so the
    /// transcript was left for a manual Ctrl+V (the caller should tell the user).</summary>
    public enum PasteResult { Pasted, CopiedToClipboard }

    /// <summary>How to deliver the transcript (mirrors Handy's paste-method options). <c>Auto</c> and
    /// <c>CtrlV</c> both send the Ctrl+V chord — there is deliberately NO ladder behind Auto (see
    /// <see cref="SendPasteChord"/>). <c>Type</c> = synthesise the characters; <c>ShiftInsert</c> = the paste
    /// combo terminals honour; <c>Clipboard</c> = copy + prompt; <c>None</c> = don't paste (save only).</summary>
    public enum PasteMethod { Auto, CtrlV, ShiftInsert, Type, Clipboard, None }

    /// <summary>Maps the persisted setting string to a <see cref="PasteMethod"/> (default Auto).</summary>
    public static PasteMethod ParsePasteMethod(string? s) => s switch
    {
        "ctrl_v" => PasteMethod.CtrlV,
        "shift_insert" => PasteMethod.ShiftInsert,
        "type" => PasteMethod.Type,
        "clipboard" => PasteMethod.Clipboard,
        "none" => PasteMethod.None,
        _ => PasteMethod.Auto,
    };

    /// <param name="restoreTo">Window to refocus before pasting (the app dictation began in), or
    /// <see cref="IntPtr.Zero"/> to paste into whatever currently has focus.</param>
    /// <param name="keepInClipboard">When true, leave the transcript on the clipboard instead of
    /// restoring the user's previous clipboard contents.</param>
    /// <param name="pressEnter">When true, send Enter after the paste (handy for chat/search boxes).</param>
    /// <param name="method">Delivery method (<see cref="PasteMethod"/>). Default <c>Auto</c> = the smart
    /// ladder: WM_PASTE for editors → synthetic Ctrl+V if injection works → clipboard mode otherwise.</param>
    public static PasteResult PasteAtCursor(string text, IntPtr restoreTo = default,
        bool keepInClipboard = false, bool pressEnter = false, PasteMethod method = PasteMethod.Auto)
    {
        if (string.IsNullOrEmpty(text) || method == PasteMethod.None) return PasteResult.Pasted;

        // Land the paste in the app dictation began in, not the Jot window. Never re-target our own.
        bool restore = restoreTo != IntPtr.Zero && !IsOwnWindow(restoreTo);
        bool focusOk = true;
        if (restore)
        {
            focusOk = ForceForeground(restoreTo);
            Thread.Sleep(40); // let the focus change settle before pasting
        }
        Jot.Services.JotLog.Info(
            $"paste: target={(restore ? DescribeWindow(restoreTo) : "current-focus")} " +
            $"focusRestored={focusOk} foregroundNow={DescribeWindow(GetForegroundWindow())} method={method}");

        // TYPE: no clipboard involved — synthesise the characters directly (works in some apps that reject a paste).
        if (method == PasteMethod.Type)
        {
            ReleaseModifiers();
            WaitForRealModifiersReleased();
            SendUnicodeText(text);
            if (pressEnter) { Thread.Sleep(60); SendEnter(); }
            return PasteResult.Pasted;
        }

        // Clipboard-based methods: snapshot the user's ENTIRE clipboard (all formats) so a dictation never
        // destroys a copied image/file list, then place our transcript. Restored after paste unless clipboard-mode.
        System.Windows.DataObject? saved = keepInClipboard ? null : SnapshotClipboard();
        bool clipOk = SetClipboardText(text);
        // Let the clipboard write settle before the chord (Handy's paste_delay_ms, default 60). Pasting the
        // instant after SetText can hand the target the PREVIOUS clipboard on machines where a clipboard
        // manager / DLP agent hooks the change and delays it becoming readable.
        if (method != PasteMethod.Clipboard) Thread.Sleep(ClipboardSettleMs);

        PasteResult result;
        switch (method)
        {
            case PasteMethod.Clipboard:
                Jot.Services.JotLog.Info($"paste: clipboardSet={clipOk} method=clipboard-only (user setting)");
                result = PasteResult.CopiedToClipboard;
                break;
            case PasteMethod.ShiftInsert:
                ReleaseModifiers(); WaitForRealModifiersReleased();
                SendShiftInsert();
                Jot.Services.JotLog.Info($"paste: clipboardSet={clipOk} method=Shift+Insert");
                result = PasteResult.Pasted;
                break;
            default: // Auto and CtrlV are the same thing: send the chord.
                ReleaseModifiers(); WaitForRealModifiersReleased();
                // Hard gate: a V injected while Win is live is a Win+V, i.e. the clipboard-history flyout
                // rather than a paste. If Win refuses to clear, leaving the transcript on the clipboard is
                // the only honest outcome — sending the chord anyway would pop the flyout at the user.
                if (!NeutralizeWin())
                {
                    Jot.Services.JotLog.Warn(
                        $"paste: clipboardSet={clipOk} SKIPPED Ctrl+V — Win key still held; left on clipboard");
                    result = PasteResult.CopiedToClipboard;
                    break;
                }
                uint sent = SendPasteChord();
                if (sent == 0)
                {
                    Jot.Services.JotLog.Warn(
                        $"paste: clipboardSet={clipOk} ABORTED Ctrl+V — Win re-latched mid-chord; left on clipboard");
                    result = PasteResult.CopiedToClipboard;
                    break;
                }
                Jot.Services.JotLog.Info($"paste: clipboardSet={clipOk} method=Ctrl+V(vk eventsSent={sent}/4)");
                result = PasteResult.Pasted;
                break;
        }

        if (pressEnter && result == PasteResult.Pasted)
        {
            Thread.Sleep(60);
            SendEnter();
        }

        // Restore the user's clipboard only when we actually pasted; in clipboard mode KEEP the transcript so
        // the manual Ctrl+V has something to paste.
        if (!keepInClipboard && result == PasteResult.Pasted)
        {
            Task.Delay(150).ContinueWith(_ =>
                Application.Current?.Dispatcher.Invoke(() => RestoreClipboard(saved)));
        }

        return result;
    }

    // DIAGNOSTIC ONLY — deliberately NOT consulted by the paste path (`--injecttest` is its only caller).
    // Probes whether INJECTED keyboard input reaches the input system: corporate endpoint-security can
    // silently drop synthetic keystrokes while SendInput still reports success, so inject a harmless key
    // (F24) and check whether the input system registered it — GetAsyncKeyState is foreground-independent.
    // Cached; probed once. It used to gate an automatic clipboard-mode fallback; that ladder is gone, so this
    // now only answers "is injection blocked on this machine?" when triaging a report.
    private static bool? _injectionWorks;
    public static bool SyntheticInputWorks()
    {
        if (_injectionWorks is bool cached) return cached;
        bool works;
        try
        {
            const byte VK_F24 = 0x87;
            keybd_event(VK_F24, 0, 0, IntPtr.Zero);                 // inject DOWN
            Thread.Sleep(15);
            works = (GetAsyncKeyState(VK_F24) & 0x8000) != 0;       // did the input system register it?
            keybd_event(VK_F24, 0, KEYEVENTF_KEYUP, IntPtr.Zero);   // inject UP
        }
        catch { works = true; } // never break paste over a probe failure — assume input works
        _injectionWorks = works;
        Jot.Services.JotLog.Info($"synthetic-input probe: injection {(works ? "WORKS" : "is BLOCKED on this machine")}");
        return works;
    }

    /// <summary>
    /// Copies the current selection in the focused app (synthetic Ctrl+C) and returns it, restoring the
    /// user's previous clipboard afterward. Returns "" when nothing is selected. STA thread only.
    /// </summary>
    public static string CaptureSelection(int pollBudgetMs = 600)
    {
        // This path fails silently ("Select some text first") on empty; log every stage so one hands-on
        // test shows WHY (modifier still held? Ctrl+C not honored? nothing selected?).
        bool modsDownAtEntry = AnyModifierDown();
        string? saved = null;
        try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { /* clipboard busy */ }

        // Clear first so we can distinguish "nothing was copied" from "the same text was already there".
        TryClear();
        ReleaseModifiers(); // synthetic key-up for the hotkey's modifier (e.g. Alt) — necessary but NOT
        // sufficient: a synthetic up can't override a modifier the user's finger still physically holds
        // (confirmed: with real Alt held, synthetic release + Ctrl+C reads as Ctrl+Alt+C, unbound to
        // Copy, so nothing is captured). WM_HOTKEY fires the instant the combo completes — before
        // release — so this is the normal fast-tap case. Actively wait for REAL hardware state to clear.
        long modWaitMs = WaitForRealModifiersReleased();
        bool modsStillDown = AnyModifierDown(); // true here = we timed out with a key physically held
        SendKeyChord(SCAN_CONTROL, SCAN_C);

        // Poll for the copy to land instead of a single fixed wait: slow apps (browsers, Electron,
        // Office) can take well over 100ms to service Ctrl+C, and a too-short wait reads an empty
        // clipboard and wrongly reports "nothing selected." Retry until text appears or we time out.
        // pollBudgetMs is overridable so a dev self-test can tell a real timeout apart from a real failure.
        string captured = "";
        int polled = 0;
        for (; polled < pollBudgetMs; polled += 30)
        {
            Thread.Sleep(30);
            try { if (Clipboard.ContainsText()) { captured = Clipboard.GetText(); if (captured.Length > 0) break; } }
            catch { /* clipboard busy — retry */ }
        }

        Jot.Services.JotLog.Info(
            $"rewrite-capture: modsAtEntry={modsDownAtEntry} modWaitMs={modWaitMs} modsStillDown={modsStillDown} " +
            $"savedClip={(saved is null ? "none" : saved.Length + "ch")} pollMs={polled} capturedLen={captured.Length}");

        // Restore the user's clipboard.
        if (saved is not null) SetClipboardText(saved); else TryClear();
        return captured;
    }

    /// <summary>True if Alt, Ctrl, Shift or Win is physically down right now (real hardware state).
    /// Win is included because a held Win key silently re-targets our Ctrl+V to the shell — see
    /// <see cref="NeutralizeWin"/>.</summary>
    private static bool AnyModifierDown() =>
        (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
        || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
        || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
        || WinDown();

    private static bool SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(20); } // clipboard is a shared global resource; retry briefly
        }
        return false; // a clipboard manager/policy held it locked for all 5 tries — caller logs this
    }

    private static void TryClear()
    {
        try { Clipboard.Clear(); } catch { /* best effort */ }
    }

    // Capture the WHOLE clipboard (every format) so a dictation restores it intact — not just plain text.
    // Per-format best-effort: some formats (delay-rendered, stream/handle-backed) don't round-trip and are
    // skipped rather than failing the snapshot. Returns null when the clipboard is empty/unreadable.
    private static System.Windows.DataObject? SnapshotClipboard()
    {
        try
        {
            System.Windows.IDataObject? current = Clipboard.GetDataObject();
            if (current is null) return null;
            var copy = new System.Windows.DataObject();
            bool any = false;
            foreach (string fmt in current.GetFormats())
            {
                try
                {
                    object? data = current.GetData(fmt);
                    if (data is not null) { copy.SetData(fmt, data); any = true; }
                }
                catch { /* a format that won't round-trip (delay-render/stream) — skip it */ }
            }
            return any ? copy : null;
        }
        catch { return null; }
    }

    // Put the user's captured clipboard back (copy:true persists it past our exit); if there was nothing to
    // restore, clear our transcript so it doesn't linger.
    private static void RestoreClipboard(System.Windows.DataObject? snapshot)
    {
        try
        {
            if (snapshot is not null) Clipboard.SetDataObject(snapshot, true);
            else TryClear();
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The Ctrl+V chord, byte-for-byte what Handy sends on Windows (enigo's <c>queue_key</c> path):
    /// VIRTUAL-KEY driven with the scan code carried alongside, as FOUR separate SendInput calls with a
    /// real hold between the V click and the Ctrl release.
    /// </summary>
    /// <remarks>
    /// Three details here are load-bearing; do not "simplify" them back:
    /// <list type="bullet">
    /// <item>Virtual key, NOT <c>KEYEVENTF_SCANCODE</c>. wScan is populated (apps that read it get a sane
    /// value) but the flag is off, so Windows dispatches on wVk. Scan-coded V is re-mapped through the
    /// TARGET thread's keyboard layout, which is how the same chord means different things per app.</item>
    /// <item>Separate SendInput calls, not one batch. A single batch is delivered atomically, so a target
    /// that polls <c>GetKeyState(VK_CONTROL)</c> while handling V can observe Ctrl already released and
    /// treat the keystroke as a bare "v".</item>
    /// <item>The <see cref="ChordHoldMs"/> sleep. Same reason — the modifier must still be down while a
    /// slow target services the V.</item>
    /// </list>
    /// </remarks>
    /// <returns>Events accepted (4 = all); <c>0</c> means the chord was ABORTED because Win came back
    /// between the caller's gate and the V — see the re-check below.</returns>
    private static uint SendPasteChord()
    {
        uint sent = 0;
        sent += SendInput(1, [VkChordInput(VK_CONTROL, keyUp: false)], Marshal.SizeOf<INPUT>());
        // Last possible instant to check. A physically held Win auto-repeats, so it can re-latch after the
        // caller's gate; injecting V now would be a Win+V. Back out cleanly instead of popping the flyout.
        if (WinDown())
        {
            SendInput(1, [VkChordInput(VK_CONTROL, keyUp: true)], Marshal.SizeOf<INPUT>());
            return 0;
        }
        sent += SendInput(1, [VkChordInput(VK_V, keyUp: false)], Marshal.SizeOf<INPUT>());
        sent += SendInput(1, [VkChordInput(VK_V, keyUp: true)], Marshal.SizeOf<INPUT>());
        Thread.Sleep(ChordHoldMs);
        sent += SendInput(1, [VkChordInput(VK_CONTROL, keyUp: true)], Marshal.SizeOf<INPUT>());
        return sent; // 4 = every event accepted; anything less means something dropped them
    }

    // wVk + wScan both set, KEYEVENTF_SCANCODE deliberately NOT set — see SendPasteChord.
    private static INPUT VkChordInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC_EX),
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    // Shift+Insert — the paste combo consoles/terminals honour when Ctrl+V doesn't. VK-based (Insert is an
    // extended key that VK injection handles cleanly).
    private static void SendShiftInsert()
    {
        const ushort VK_SHIFT = 0x10, VK_INSERT = 0x2D;
        var inputs = new[]
        {
            VkInput(VK_SHIFT, keyUp: false), VkInput(VK_INSERT, keyUp: false),
            VkInput(VK_INSERT, keyUp: true), VkInput(VK_SHIFT, keyUp: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>Injects key-ups for Alt/Ctrl/Shift/Win so a still-held global-hotkey modifier (e.g. the Alt
    /// of "Alt+/") doesn't corrupt the synthetic Ctrl+C/Ctrl+V we're about to send.</summary>
    /// <remarks>
    /// The <see cref="SendMenuMask"/> call is load-bearing and must come FIRST. Releasing Alt or Win with a
    /// bare key-up turns the user's real key-down into a lone down→up tap, which is exactly the gesture the
    /// shell watches for: measured 3/3, an unmasked Win release opens the Start menu, which steals foreground
    /// and loses the paste. Masking first makes the release inert — measured 3/3 clean.
    /// </remarks>
    private static void ReleaseModifiers()
    {
        // Only mask when a menu-arming modifier is actually held; a mask keystroke is harmless but pointless
        // otherwise, and this keeps the common no-modifier paste to a single SendInput.
        if (MenuModifierDown()) SendMenuMask();

        var ups = new[]
        {
            ScanInput(SCAN_ALT, keyUp: true),
            ScanInput(SCAN_CONTROL, keyUp: true),
            ScanInput(SCAN_SHIFT, keyUp: true),
            VkInput(VK_LWIN, keyUp: true),
            VkInput(VK_RWIN, keyUp: true),
        };
        SendInput((uint)ups.Length, ups, Marshal.SizeOf<INPUT>());
    }

    /// <summary>True if a modifier that arms a shell menu (Alt or either Win) is physically down.</summary>
    private static bool MenuModifierDown() =>
        (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || WinDown();

    /// <summary>True if either Win key is physically down right now.</summary>
    private static bool WinDown() =>
        (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    // A press+release of an inert key, so a subsequent Alt/Win key-up no longer reads as a lone tap.
    private static void SendMenuMask()
    {
        var mask = new[]
        {
            VkInput(MENU_MASK_VK, keyUp: false, extraInfo: MenuMaskMarker),
            VkInput(MENU_MASK_VK, keyUp: true, extraInfo: MenuMaskMarker),
        };
        SendInput((uint)mask.Length, mask, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Drives the Win key to logically-released and reports whether it got there. Retries because a
    /// physically held Win auto-repeats: each repeat re-latches the key after we let go of it.
    /// </summary>
    /// <remarks>
    /// Why this exists at all: Windows routes Win+Ctrl+V to its Win+V handler, so with Win held the paste
    /// chord opens the clipboard-history flyout instead of pasting (measured — foreground goes to
    /// ControlCenterWindow, nothing lands). The extra Ctrl does NOT protect the chord.
    /// </remarks>
    private static bool NeutralizeWin(int timeoutMs = 300)
    {
        if (!WinDown()) return true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            SendMenuMask();
            var ups = new[] { VkInput(VK_LWIN, keyUp: true), VkInput(VK_RWIN, keyUp: true) };
            SendInput((uint)ups.Length, ups, Marshal.SizeOf<INPUT>());
            Thread.Sleep(15);
            if (!WinDown()) return true;
        }
        while (sw.ElapsedMilliseconds < timeoutMs);
        return false;
    }

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Actively waits (bounded) for Alt/Ctrl/Shift to be genuinely, physically released —
    /// <see cref="ReleaseModifiers"/>'s synthetic key-up alone isn't sufficient when the user's finger
    /// is still on the key (confirmed empirically against real Notepad: with Alt still physically held,
    /// a synthetic release + Ctrl+C still reads as Ctrl+Alt+C to the target app). Bounded so a user who
    /// genuinely holds the modifier for a while doesn't hang the capture indefinitely — falls through
    /// and lets the caller's own clipboard-poll timeout be the final backstop.</summary>
    private static long WaitForRealModifiersReleased(int timeoutMs = 400)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!AnyModifierDown()) return sw.ElapsedMilliseconds;
            Thread.Sleep(10);
        }
        return sw.ElapsedMilliseconds;
    }

    /// <summary>Presses+releases a single key by VIRTUAL-KEY code rather than scan code — dev/self-test
    /// use only. Toggle keys (NumLock/CapsLock) route through a legacy extended-key scan-code
    /// translation table that virtual-key injection sidesteps, giving a cleaner read on whether the
    /// OS's own toggle-state handling fires independently of <c>RegisterHotKey</c>.</summary>
    internal static void SendVirtualKeyPress(ushort vk)
    {
        var inputs = new[] { VkInput(vk, keyUp: false), VkInput(vk, keyUp: true) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT VkInput(ushort vk, bool keyUp, IntPtr extraInfo = default) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0, dwExtraInfo = extraInfo },
        },
    };

    /// <summary>Presses a scan-coded key WITHOUT releasing it — dev/self-test use, to simulate a
    /// hotkey's modifier (e.g. Alt) still being physically held down at the moment its action runs,
    /// which is the real, common case for a live hotkey press but never happens when a dev hook
    /// invokes capture logic directly with no real keypress involved.</summary>
    internal static void SendScanKeyDown(ushort scan) => SendInput(1, [ScanInput(scan, keyUp: false)], Marshal.SizeOf<INPUT>());

    /// <summary>Releases a key pressed via <see cref="SendScanKeyDown"/>.</summary>
    internal static void SendScanKeyUp(ushort scan) => SendInput(1, [ScanInput(scan, keyUp: true)], Marshal.SizeOf<INPUT>());


    /// <summary>Types arbitrary text into whatever has focus via KEYEVENTF_UNICODE (layout-independent
    /// synthetic character input — no scan-code/VK mapping needed). Dev/self-test use, to populate a
    /// real external app (e.g. Notepad) with known text for an end-to-end selection-capture test.</summary>
    internal static void SendUnicodeText(string text)
    {
        foreach (char c in text)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } },
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } },
            };
            SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
        }
    }

    internal static uint SendKeyChord(params ushort[] scanCodes)
    {
        var inputs = new INPUT[scanCodes.Length * 2];
        for (int i = 0; i < scanCodes.Length; i++)
            inputs[i] = ScanInput(scanCodes[i], keyUp: false);
        for (int i = 0; i < scanCodes.Length; i++)
            inputs[scanCodes.Length + i] = ScanInput(scanCodes[scanCodes.Length - 1 - i], keyUp: true);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()); // events actually inserted (0 = blocked)
    }

    private static void SendEnter()
    {
        var inputs = new[]
        {
            ScanInput(SCAN_RETURN, false),
            ScanInput(SCAN_RETURN, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT ScanInput(ushort scan, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wScan = scan,
                dwFlags = KEYEVENTF_SCANCODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            },
        },
    };

    /// <summary>Forces a window to the foreground (dev/self-test use). Same mechanism the paste path
    /// uses to return focus to the origin app.</summary>
    internal static void FocusWindow(IntPtr hWnd) => ForceForeground(hWnd);

    // A background process can't just call SetForegroundWindow (Windows foreground-lock blocks it,
    // silently). Attaching our input queue to the target window's thread lifts the lock long enough
    // to hand it focus — the standard workaround — so the synthetic Ctrl+V lands in the right app.
    private static bool ForceForeground(IntPtr hWnd)
    {
        // Retry a few times: the foreground-lock lift via AttachThreadInput can miss on the first try under
        // load. Returns whether the target actually ended up foreground — false typically means the OS
        // denied it (e.g. an elevated target window that a non-elevated Jot can't focus).
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);
            uint thisThread = GetCurrentThreadId();
            bool attached = targetThread != thisThread && AttachThreadInput(thisThread, targetThread, true);
            try
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(thisThread, targetThread, false);
            }
            if (GetForegroundWindow() == hWnd) return true;
            Thread.Sleep(30);
        }
        return GetForegroundWindow() == hWnd;
    }

    private static bool IsOwnWindow(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid == GetCurrentProcessId();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // MOUSEINPUT is the largest union member; it MUST be present so that sizeof(INPUT) is the
        // 40 bytes Windows expects on x64. Without it, cbSize is too small and SendInput silently
        // no-ops (returns 0) — i.e. nothing ever gets typed.
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
