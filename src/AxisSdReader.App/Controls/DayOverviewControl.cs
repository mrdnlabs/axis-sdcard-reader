using System.Collections;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AxisSdReader.App.ViewModels;

namespace AxisSdReader.App.Controls;

/// <summary>
/// The 18px full-day overview strip above the detail track: all of the current day's
/// recording segments as tiny accent bars, 6/12/18h gridlines, a thin export-range mark,
/// a cursor tick at the current time, and a translucent viewport rectangle showing where
/// the zoomed detail window sits within the day. Click anywhere to jump.
/// The "day" shown is the local day containing the playhead.
/// </summary>
public sealed class DayOverviewControl : FrameworkElement
{
    private const double Day = 86400.0;
    private const double StripHeight = 18.0;

    public event Action<double>? JumpRequested;

    public static readonly DependencyProperty SegmentsSourceProperty = DependencyProperty.Register(
        nameof(SegmentsSource), typeof(IEnumerable), typeof(DayOverviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterSecondsProperty = DependencyProperty.Register(
        nameof(CenterSeconds), typeof(double), typeof(DayOverviewControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpanSecondsProperty = DependencyProperty.Register(
        nameof(SpanSeconds), typeof(double), typeof(DayOverviewControl),
        new FrameworkPropertyMetadata(1800.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelInSecondsProperty = DependencyProperty.Register(
        nameof(SelInSeconds), typeof(double?), typeof(DayOverviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelOutSecondsProperty = DependencyProperty.Register(
        nameof(SelOutSeconds), typeof(double?), typeof(DayOverviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? SegmentsSource
    {
        get => (IEnumerable?)GetValue(SegmentsSourceProperty);
        set => SetValue(SegmentsSourceProperty, value);
    }

    public double CenterSeconds
    {
        get => (double)GetValue(CenterSecondsProperty);
        set => SetValue(CenterSecondsProperty, value);
    }

    public double SpanSeconds
    {
        get => (double)GetValue(SpanSecondsProperty);
        set => SetValue(SpanSecondsProperty, value);
    }

    public double? SelInSeconds
    {
        get => (double?)GetValue(SelInSecondsProperty);
        set => SetValue(SelInSecondsProperty, value);
    }

    public double? SelOutSeconds
    {
        get => (double?)GetValue(SelOutSecondsProperty);
        set => SetValue(SelOutSecondsProperty, value);
    }

    public DayOverviewControl()
    {
        MinHeight = StripHeight;
        Cursor = Cursors.Hand;
        ToolTip = "Jump anywhere in the day";
        Theme.Changed += InvalidateVisual;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, StripHeight);
    }

    /// <summary>Axis seconds at local midnight of the day containing the playhead.</summary>
    private double DayStartSeconds()
    {
        var midnight = TimeAxis.ToDateTime(CenterSeconds).Date;
        return TimeAxis.ToSeconds(midnight);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var fraction = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        JumpRequested?.Invoke(DayStartSeconds() + fraction * Day);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var dayStart = DayStartSeconds();
        var strip = new Rect(0, 0, width, StripHeight);
        dc.DrawRoundedRectangle(Theme.Brush("Subtle2"), new Pen(Theme.Brush("TlBorder"), 1), Inset(strip, 0.5), 5, 5);

        dc.PushClip(new RectangleGeometry(strip));

        // 6/12/18h gridlines.
        var gridPen = new Pen(Theme.Brush("TlLine"), 1);
        foreach (var h in new[] { 6, 12, 18 })
        {
            var x = Math.Round(h * 3600 / Day * width) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 1), new Point(x, StripHeight - 1));
        }

        // Recording segments (faded accent).
        var accent = (Theme.Brush("Accent") as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var segBrush = new SolidColorBrush(accent) { Opacity = 0.75 };
        segBrush.Freeze();
        if (SegmentsSource is not null)
        {
            foreach (var item in SegmentsSource)
            {
                if (item is not TimelineSegment segment)
                {
                    continue;
                }

                var s = segment.StartSeconds - dayStart;
                var e = segment.EndSeconds - dayStart;
                if (e < 0 || s > Day)
                {
                    continue;
                }

                var x1 = Math.Max(0, s) / Day * width;
                var x2 = Math.Min(Day, e) / Day * width;
                dc.DrawRoundedRectangle(segBrush, null, new Rect(x1, 4, Math.Max(1.5, x2 - x1), 10), 2, 2);
            }
        }

        // Export-range mark (thin).
        if (SelInSeconds is { } inSec && SelOutSeconds is { } outSec && outSec > inSec)
        {
            var s = Math.Max(0, inSec - dayStart);
            var e = Math.Min(Day, outSec - dayStart);
            if (e > 0 && s < Day)
            {
                dc.DrawRectangle(Theme.Brush("Sel"), null, new Rect(s / Day * width, StripHeight - 3.5, Math.Max(2, (e - s) / Day * width), 2.5));
            }
        }

        dc.Pop();

        // Viewport rectangle for the zoomed window.
        var viewLeft = (CenterSeconds - SpanSeconds / 2 - dayStart) / Day * width;
        var viewRight = (CenterSeconds + SpanSeconds / 2 - dayStart) / Day * width;
        viewLeft = Math.Clamp(viewLeft, 0, width);
        viewRight = Math.Clamp(viewRight, 0, width);
        if (viewRight - viewLeft >= 1)
        {
            dc.DrawRoundedRectangle(Theme.Brush("AccentTint"), new Pen(Theme.Brush("Accent"), 1.5),
                new Rect(viewLeft, -1, Math.Max(8, viewRight - viewLeft), StripHeight + 2), 4, 4);
        }

        // Cursor tick at the current time.
        var cx = Math.Clamp((CenterSeconds - dayStart) / Day, 0, 1) * width;
        dc.DrawRectangle(Theme.Brush("Playhead"), null, new Rect(cx - 0.75, 0, 1.5, StripHeight));
    }

    private static Rect Inset(Rect r, double by) =>
        new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));
}
