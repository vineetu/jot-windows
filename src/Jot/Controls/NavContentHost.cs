using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Jot.Controls;

/// <summary>
/// Attached behavior that caps a page-root element's height to its hosting <see cref="NavigationView"/>.
/// WPF-UI's NavigationView measures hosted pages with <b>infinite height</b>, so a page-level
/// <c>ScrollViewer</c> (or a Grid whose list row is <c>*</c>) never receives a bounded height and can't
/// scroll — content just grows past the window. Setting <c>ctl:NavContentHost.FillHeight="True"</c> on
/// the page root binds its Height to the NavigationView's (bounded) ActualHeight so scrolling works.
/// </summary>
public static class NavContentHost
{
    public static readonly DependencyProperty FillHeightProperty =
        DependencyProperty.RegisterAttached(
            "FillHeight", typeof(bool), typeof(NavContentHost),
            new PropertyMetadata(false, OnFillHeightChanged));

    public static void SetFillHeight(DependencyObject o, bool value) => o.SetValue(FillHeightProperty, value);
    public static bool GetFillHeight(DependencyObject o) => (bool)o.GetValue(FillHeightProperty);

    private static void OnFillHeightChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement fe || e.NewValue is not true) return;
        fe.Loaded += (_, _) =>
        {
            DependencyObject? d = fe;
            while (d is not null and not NavigationView)
                d = VisualTreeHelper.GetParent(d);
            if (d is FrameworkElement host)
                fe.SetBinding(FrameworkElement.HeightProperty,
                    new Binding(nameof(FrameworkElement.ActualHeight)) { Source = host });
        };
    }
}
