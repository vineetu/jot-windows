using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Jot.Controls;

/// <summary>
/// A single Fluent settings row: title + optional description on the left, a control on the right.
/// The control is the row's XAML content, so panes read like System Settings rows.
/// </summary>
[ContentProperty(nameof(RowContent))]
public partial class SettingRow : UserControl
{
    public SettingRow() => InitializeComponent();

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty RowContentProperty =
        DependencyProperty.Register(nameof(RowContent), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? RowContent { get => GetValue(RowContentProperty); set => SetValue(RowContentProperty, value); }
}
