// WinForms interop is enabled project-wide (for the tray NotifyIcon), which pulls
// System.Drawing / System.Windows.Forms into the implicit usings and makes these names
// ambiguous with WPF. Jot is a WPF app, so pin them to their WPF meanings here; the few
// WinForms types we need are referenced through explicit aliases (Forms./Drawing.) in App.
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using Pen = System.Windows.Media.Pen;
global using Point = System.Windows.Point;
global using FontFamily = System.Windows.Media.FontFamily;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
