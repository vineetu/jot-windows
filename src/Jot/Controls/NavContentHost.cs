using System.Globalization;
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
        // Works whether set in XAML (before load) or applied by the host after a Navigated (already
        // in the tree): bind now if we can find the host, otherwise wait for Loaded.
        if (fe.IsLoaded) BindToHost(fe);
        else
        {
            void OnLoaded(object? _, RoutedEventArgs __) { fe.Loaded -= OnLoaded; BindToHost(fe); }
            fe.Loaded += OnLoaded;
        }
    }

    private static void BindToHost(FrameworkElement fe)
    {
        DependencyObject? d = fe;
        while (d is not null and not NavigationView)
            d = VisualTreeHelper.GetParent(d);
        if (d is not FrameworkElement host) return;

        // Bind Height to the host, minus the page root's own top+bottom margin. Pages that pad with an
        // outer Margin (e.g. a Grid root) would otherwise be laid out at host-height PLUS that margin —
        // taller than the content region — clipping the bottom (a list's last row, a chat input). Pages
        // that pad inside (ScrollViewer root, margin 0) subtract nothing and are unaffected.
        fe.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(FrameworkElement.ActualHeight))
        {
            Source = host,
            Converter = SubtractConverter.Instance,
            ConverterParameter = fe.Margin.Top + fe.Margin.Bottom,
        });
    }

    /// <summary>host.ActualHeight − (page margin) → the height the page root should occupy, never negative.</summary>
    private sealed class SubtractConverter : IValueConverter
    {
        public static readonly SubtractConverter Instance = new();

        public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            double height = value is double h ? h : 0;
            double margin = parameter is double m ? m : 0;
            return Math.Max(0, height - margin);
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
