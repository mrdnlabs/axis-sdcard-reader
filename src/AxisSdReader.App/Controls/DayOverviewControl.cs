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

    /// <summary>
    /// Axis-second bounds of the local day containing the playhead: its start (local midnight) and its
    /// real length in axis seconds. The length is 86400 on a normal day but 82800/90000 across a
    /// daylight-saving change, since a UTC-based axis makes a local day no longer a fixed 24h.
    /// </summary>
    private (double Start, double Length) DayBounds()
    {
        var midnight = TimeAxis.ToDateTime(CenterSeconds).Date;
        var start = TimeAxis.ToSeconds(midnight);
        return (start, TimeAxis.ToSeconds(midnight.AddDays(1)) - start);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var fraction = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        var (start, length) = DayBounds();
        JumpRequested?.Invoke(start + fraction * length);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var midnight = TimeAxis.ToDateTime(CenterSeconds).Date;
        var dayStart = TimeAxis.ToSeconds(midnight);
        var day = TimeAxis.ToSeconds(midnight.AddDays(1)) - dayStart; // 86400, or 82800/90000 across DST
        var strip = new Rect(0, 0, width, StripHeight);
        dc.DrawRoundedRectangle(Theme.Brush("Subtle2"), new Pen(Theme.Brush("TlBorder"), 1), Inset(strip, 0.5), 5, 5);

        dc.PushClip(new RectangleGeometry(strip));

        // 6/12/18h gridlines, placed at where each LOCAL hour actually falls on the axis (so they track
        // the footage under them on DST days rather than at fixed 1/4 fractions).
        var gridPen = new Pen(Theme.Brush("TlLine"), 1);
        foreach (var h in new[] { 6, 12, 18 })
        {
            var x = Math.Round((TimeAxis.ToSeconds(midnight.AddHours(h)) - dayStart) / day * width) + 0.5;
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
                if (e < 0 || s > day)
                {
                    continue;
                }

                var x1 = Math.Max(0, s) / day * width;
                var x2 = Math.Min(day, e) / day * width;
                dc.DrawRoundedRectangle(segBrush, null, new Rect(x1, 4, Math.Max(1.5, x2 - x1), 10), 2, 2);
            }
        }

        // Export-range mark (thin).
        if (SelInSeconds is { } inSec && SelOutSeconds is { } outSec && outSec > inSec)
        {
            var s = Math.Max(0, inSec - dayStart);
            var e = Math.Min(day, outSec - dayStart);
            if (e > 0 && s < day)
            {
                dc.DrawRectangle(Theme.Brush("Sel"), null, new Rect(s / day * width, StripHeight - 3.5, Math.Max(2, (e - s) / day * width), 2.5));
            }
        }

        dc.Pop();

        // Viewport rectangle for the zoomed window.
        var viewLeft = (CenterSeconds - SpanSeconds / 2 - dayStart) / day * width;
        var viewRight = (CenterSeconds + SpanSeconds / 2 - dayStart) / day * width;
        viewLeft = Math.Clamp(viewLeft, 0, width);
        viewRight = Math.Clamp(viewRight, 0, width);
        if (viewRight - viewLeft >= 1)
        {
            dc.DrawRoundedRectangle(Theme.Brush("AccentTint"), new Pen(Theme.Brush("Accent"), 1.5),
                new Rect(viewLeft, -1, Math.Max(8, viewRight - viewLeft), StripHeight + 2), 4, 4);
        }

        // Cursor tick at the current time.
        var cx = Math.Clamp((CenterSeconds - dayStart) / day, 0, 1) * width;
        dc.DrawRectangle(Theme.Brush("Playhead"), null, new Rect(cx - 0.75, 0, 1.5, StripHeight));
    }

    private static Rect Inset(Rect r, double by) =>
        new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));
}
