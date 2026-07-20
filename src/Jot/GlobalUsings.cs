// Project-wide WinForms interop (tray NotifyIcon) drags System.Drawing/System.Windows.Forms into
// the implicit usings, making these names ambiguous with WPF. Pin them to WPF; the few WinForms
// types needed use explicit Forms./Drawing. aliases in App.
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
global using FontFamily = System.Windows.Media.FontFamily;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
global using Binding = System.Windows.Data.Binding;
global using UserControl = System.Windows.Controls.UserControl;
