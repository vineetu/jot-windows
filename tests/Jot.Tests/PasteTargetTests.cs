using Jot.Delivery;
using Xunit;

namespace Jot.Tests;

/// <summary>
/// The message-paste routing decision: which focused-control window classes get WM_PASTE (a window message
/// that survives corporate keystroke-injection filters) vs. fall back to synthetic Ctrl+V. Notepad — the
/// case that failed on the locked-down machine — must route to WM_PASTE.
/// </summary>
public class PasteTargetTests
{
    [Theory]
    [InlineData("Edit", true)]                          // classic Notepad, many Win32 editors
    [InlineData("RichEditD2DPT", true)]                 // Windows 11 Notepad
    [InlineData("RICHEDIT50W", true)]                   // WordPad / RichEdit apps
    [InlineData("Scintilla", true)]                     // Notepad++ and Scintilla-based editors
    [InlineData("Chrome_RenderWidgetHostHWND", false)]  // browsers -> Ctrl+V fallback
    [InlineData("HwndWrapper[App;;guid]", false)]       // WPF -> Ctrl+V fallback
    [InlineData("Notepad", false)]                      // the top-level FRAME, not the edit control
    [InlineData("", false)]
    public void IsPasteableEditClass_RoutesStandardEditorsToMessagePaste(string cls, bool expected)
        => Assert.Equal(expected, TextInjector.IsPasteableEditClass(cls));
}
