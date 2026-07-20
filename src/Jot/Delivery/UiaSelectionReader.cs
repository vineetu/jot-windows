using System.Text;
using System.Windows.Automation;
using Jot.Services;

namespace Jot.Delivery;

/// <summary>
/// Reads the focused app's text selection via UI Automation's <see cref="TextPattern"/> — no synthetic
/// keystroke, no clipboard. Primary path for Rewrite: sending no keys sidesteps the fragility of
/// <see cref="TextInjector.CaptureSelection"/> (still-held Alt corrupting Ctrl+C, clipboard races,
/// fixed timing waits). Works for native Windows text surfaces — Notepad, WordPad,
/// Win32/WinForms/WPF/UWP edit controls, and Microsoft Word.
///
/// NOT universal: Chromium apps (Chrome, Edge, VS Code, Slack, Discord, Electron) build their a11y tree
/// lazily and may expose nothing on first read, and Java apps need the Access Bridge — the caller falls
/// back to the clipboard Ctrl+C path.
/// </summary>
public static class UiaSelectionReader
{
    /// <summary>
    /// Returns the focused app's selected text via UI Automation, or null when there's no selection or
    /// the app doesn't expose UIA text (→ caller should fall back to the clipboard path). Cross-process
    /// UIA calls can block, so the read runs on a background MTA thread bounded by <paramref name="timeoutMs"/>
    /// — a hung provider (e.g. a cold Chromium warming up its a11y tree) can never freeze the UI thread.
    /// </summary>
    public static string? TryReadSelection(int timeoutMs = 350)
    {
        string? result = null;
        Exception? error = null;

        var worker = new Thread(() =>
        {
            try { result = ReadSelectionCore(); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name = "Jot.UiaSelectionRead",
        };
        // MTA: UIA client calls must not run on our STA UI thread — cross-process calls there can
        // deadlock against a target app that's pumping messages waiting on us.
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();

        if (!worker.Join(timeoutMs))
        {
            JotLog.Warn($"uia-selection: read timed out after {timeoutMs}ms — falling back to clipboard");
            return null; // worker is abandoned (background thread); the fallback path takes over
        }
        if (error is not null)
        {
            JotLog.Warn($"uia-selection: read failed ({error.GetType().Name}: {error.Message})");
            return null;
        }
        return string.IsNullOrEmpty(result) ? null : result;
    }

    private static string? ReadSelectionCore()
    {
        AutomationElement? focused;
        try { focused = AutomationElement.FocusedElement; }
        catch (ElementNotAvailableException) { return null; }
        if (focused is null) return null;

        // 1) The focused element itself may be the text control.
        string? text = ReadFromElement(focused);
        if (!string.IsNullOrEmpty(text)) return text;

        // 2) Focus is often on a container (a Document/pane/custom host), not the text leaf. Find the
        //    first descendant that advertises TextPattern and read its selection instead.
        try
        {
            var cond = new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty, true);
            AutomationElement? textEl = focused.FindFirst(TreeScope.Element | TreeScope.Descendants, cond);
            if (textEl is not null)
            {
                text = ReadFromElement(textEl);
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch (ElementNotAvailableException) { /* focus moved mid-read — give up, caller falls back */ }

        return null;
    }

    /// <summary>Reads the selection from one element's TextPattern, or null if it has none/empty.</summary>
    private static string? ReadFromElement(AutomationElement element)
    {
        try
        {
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object patternObj)
                || patternObj is not TextPattern text)
                return null;

            // GetSelection() returns disjoint ranges (e.g. multi-select). A caret with no selection
            // comes back as a single *degenerate* (empty) range, so concatenating and checking for
            // non-empty text is the reliable "is anything actually selected?" test.
            var ranges = text.GetSelection();
            if (ranges is null || ranges.Length == 0) return null;

            var sb = new StringBuilder();
            foreach (var range in ranges)
                sb.Append(range.GetText(-1)); // -1 = the whole range, no length cap

            string selected = sb.ToString();
            return selected.Length == 0 ? null : selected;
        }
        catch (InvalidOperationException) { return null; } // "text container does not support selection"
        catch (ElementNotAvailableException) { return null; }
    }
}
