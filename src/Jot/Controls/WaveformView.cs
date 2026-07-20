using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jot.Controls;

/// <summary>
/// The pill's reactive waveform: a horizontal line that stays flat when silent and squiggles with live
/// mic amplitude while speaking. Keeps a short history ring and scrolls it right-to-left, with a slowly
/// advancing phase so the wave flows. Cheap: one frozen <see cref="StreamGeometry"/> polyline at ~30 fps,
/// and it only ticks while <see cref="Active"/>.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const int PointCount = 56;

    private readonly double[] _history = new double[PointCount];
    private readonly DispatcherTimer _timer;
    private double _level;   // latest incoming RMS level (0..1)
    private double _phase;   // animates the wave sideways
    private bool _active;
    private Brush _lineBrush = Brushes.White;
    private double _lineThickness = 2.0;

    public WaveformView()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Advance();
    }

    public Brush LineBrush
    {
        get => _lineBrush;
        set { _lineBrush = value; InvalidateVisual(); }
    }

    public double LineThickness
    {
        get => _lineThickness;
        set { _lineThickness = value; InvalidateVisual(); }
    }

    /// <summary>Start/stop the animation. Stopping relaxes the line back to flat.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                Array.Clear(_history);
                _level = 0;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Feed the newest mic level (0..1). Caller marshals onto the UI thread.</summary>
    public void PushLevel(double level) => _level = Math.Clamp(level, 0, 1);

    private void Advance()
    {
        Array.Copy(_history, 1, _history, 0, PointCount - 1);
        _history[PointCount - 1] = _level;
        _level *= 0.72;  // decay so a pause relaxes toward a straight line
        _phase += 0.35;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double mid = h / 2.0;
        double maxAmp = mid - _lineThickness;

        var pen = new Pen(_lineBrush, _lineThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            for (int i = 0; i < PointCount; i++)
            {
                double x = w * i / (PointCount - 1);
                double amp = _history[i] * maxAmp;            // silence -> 0 -> flat centerline
                double y = mid + amp * Math.Sin(i * 0.6 + _phase);
                if (i == 0) ctx.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                else ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
            }
        }
        geo.Freeze();

        dc.DrawGeometry(null, pen, geo);
    }
}
