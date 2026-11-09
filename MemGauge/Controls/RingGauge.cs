using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace MemGauge.Controls;

/// <summary>
/// 环形占用指示器：外圈轨道 + 圆弧进度，支持不定长转圈动画。
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    private readonly DispatcherTimer _spinTimer;
    private double _spinAngle = -90;

    public RingGauge()
    {
        _spinTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _spinTimer.Tick += (_, _) =>
        {
            _spinAngle += 7;
            if (_spinAngle > 270) _spinAngle = -90;
            InvalidateVisual();
        };
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 ~ 100 的占用百分比。</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush ValueBrush
    {
        get => (Brush)GetValue(ValueBrushProperty);
        set => SetValue(ValueBrushProperty, value);
    }

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(RingGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIndeterminateChanged));

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    private static void OnIndeterminateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        if (e.NewValue is true)
        {
            gauge._spinAngle = -90;
            gauge._spinTimer.Start();
        }
        else
        {
            gauge._spinTimer.Stop();
        }
        gauge.InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2) return;

        var t = Math.Min(Thickness, size / 3);
        var radius = (size - t) / 2 - 0.5;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var trackPen = new Pen(TrackBrush, t);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        var arcPen = new Pen(ValueBrush, t)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };

        if (IsIndeterminate)
        {
            dc.DrawGeometry(null, arcPen, CreateArc(center, radius, _spinAngle, 90));
            return;
        }

        var sweep = Math.Clamp(Value, 0, 100) / 100.0 * 360.0;
        if (sweep < 0.6) return;

        // 满环时圆弧首尾重合会消失，稍微收一点
        dc.DrawGeometry(null, arcPen, CreateArc(center, radius, -90, Math.Min(sweep, 359.9)));
    }

    private static Geometry CreateArc(Point center, double radius, double startAngleDeg, double sweepDeg)
    {
        var start = startAngleDeg * Math.PI / 180.0;
        var end = (startAngleDeg + sweepDeg) * Math.PI / 180.0;

        var p1 = new Point(center.X + radius * Math.Cos(start), center.Y + radius * Math.Sin(start));
        var p2 = new Point(center.X + radius * Math.Cos(end), center.Y + radius * Math.Sin(end));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(p1, isFilled: false, isClosed: false);
            ctx.ArcTo(p2, new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }
}
