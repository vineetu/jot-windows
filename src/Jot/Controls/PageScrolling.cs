using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Jot.Controls;

/// <summary>
/// App-wide scrolling behaviour, in one place so every page behaves the same and no page needs its
/// own scroll plumbing. Two concerns:
/// <list type="bullet">
/// <item>A read-only <see cref="TextBox"/> (Jot renders transcripts as borderless read-only text
/// boxes so they support select + copy) swallows the mouse wheel, so hovering the text and scrolling
/// does nothing. <see cref="Install"/> registers one class handler that re-routes the wheel to the
/// nearest ancestor <c>ScrollViewer</c> whenever the text box can't consume it.</item>
/// <item><see cref="FindScrollViewer"/> locates a page's scroll owner so the window can drive
/// keyboard paging (PageUp/PageDown/Ctrl+Home/Ctrl+End) uniformly.</item>
/// </list>
/// Combined with <see cref="NavContentHost"/> (which gives each page a bounded height so its scroller
/// actually scrolls), this makes wheel + keyboard scrolling work on every page with zero per-page code.
/// </summary>
public static class PageScrolling
{
    private static bool _installed;

    /// <summary>Registers the global wheel-forwarding class handler. Call once at startup.</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(TextBoxBase),
            UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnTextBoxWheel));
    }

    private static void OnTextBoxWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not TextBoxBase tb) return;

        // Let the text box keep the wheel only if it has its own overflow to scroll in this direction;
        // otherwise (a read-only transcript sized to its content, or an editable box already at the
        // edge) hand the wheel to the nearest ancestor ScrollViewer so the page scrolls.
        bool up = e.Delta > 0;
        bool canScrollItself = tb.ExtentHeight > tb.ViewportHeight + 0.5
            && (up ? tb.VerticalOffset > 0.5
                   : tb.VerticalOffset < tb.ExtentHeight - tb.ViewportHeight - 0.5);
        if (canScrollItself) return;

        e.Handled = true;
        if (VisualTreeHelper.GetParent(tb) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = tb,
            });
    }

    /// <summary>Depth-first search for the page's scroll owner under <paramref name="root"/> — its own
    /// ScrollViewer, or the internal one inside a list. Skips a ScrollViewer that is a text box's content
    /// host (every TextBox template has one): on list pages a search box precedes the list in tree order,
    /// so returning its scroller would swallow paging keys and never move the list. Null if nothing scrolls.</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer sv && sv.TemplatedParent is not TextBoxBase) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is ScrollViewer found)
                return found;
        return null;
    }
}
