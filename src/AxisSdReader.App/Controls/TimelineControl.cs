using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AxisSdReader.App.Controls;

/// <summary>A recording segment on the day timeline, in seconds-into-day.</summary>
public sealed record TimelineSegment(double StartSeconds, double EndSeconds);

/// <summary>
/// Renders a full 24-hour day (0–86400s) as a horizontal track: 3-hour gridlines,
/// blue recording segments with gaps, a translucent yellow export-range band, a playhead,
/// and monospace hour labels below the track. Clicking the track raises <see cref="Seek"/>
/// with the clicked time in seconds-into-day.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double Day = 86400.0;
    private const double TrackHeight = 46.0;
    private const double LabelGap = 3.0;
    private const double LabelHeight = 16.0;

    private static readonly Brush TrackFill = Frozen("#F0F2F5");
    private static readonly Pen TrackBorder = FrozenPen("#E4E7EC", 1);
    private static readonly Pen GridPen = FrozenPen("#E0E4EA", 1);
    private static readonly Brush SegmentFill = Frozen("#2F6FED");
    private static readonly Pen SegmentInset = FrozenPen("#2EFFFFFF", 1); // white hairline, ~18% alpha
    private static readonly Brush SelectionFill = Frozen("#52F7C948"); // ~32% alpha
    private static readonly Pen SelectionBorder = FrozenPen("#F7C948", 1.5);
    private static readonly Brush PlayheadFill = Frozen("#12203A");
    private static readonly Pen PlayheadHalo = FrozenPen("#99FFFFFF", 1);
    private static readonly Brush LabelBrush = Frozen("#A2ABB5");
    private static readonly Typeface MonoFace = new("Consolas");

    public event Action<double>? Seek;

    public static readonly DependencyProperty SegmentsSourceProperty = DependencyProperty.Register(
        nameof(SegmentsSource), typeof(IEnumerable), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentSecondsProperty = DependencyProperty.Register(
        nameof(CurrentSeconds), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelInSecondsProperty = DependencyProperty.Register(
        nameof(SelInSeconds), typeof(double?), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelOutSecondsProperty = DependencyProperty.Register(
        nameof(SelOutSeconds), typeof(double?), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? SegmentsSource
    {
        get => (IEnumerable?)GetValue(SegmentsSourceProperty);
        set => SetValue(SegmentsSourceProperty, value);
    }

    public double CurrentSeconds
    {
        get => (double)GetValue(CurrentSecondsProperty);
        set => SetValue(CurrentSecondsProperty, value);
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

    public TimelineControl()
    {
        MinHeight = TrackHeight + LabelGap + LabelHeight;
        Cursor = Cursors.Hand;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, TrackHeight + LabelGap + LabelHeight);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var x = e.GetPosition(this).X;
        var fraction = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
        Seek?.Invoke(fraction * Day);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
        {
            var x = e.GetPosition(this).X;
            var fraction = Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1);
            Seek?.Invoke(fraction * Day);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var trackRect = new Rect(0, 0, width, TrackHeight);
        dc.DrawRoundedRectangle(TrackFill, TrackBorder, Inset(trackRect, 0.5), 7, 7);

        // 3-hour gridlines
        for (var h = 3; h < 24; h += 3)
        {
            var x = Math.Round(h * 3600 / Day * width) + 0.5;
            dc.DrawLine(GridPen, new Point(x, 1), new Point(x, TrackHeight - 1));
        }

        // Recording segments
        foreach (var segment in EnumerateSegments())
        {
            var left = segment.StartSeconds / Day * width;
            var segWidth = Math.Max(2, (segment.EndSeconds - segment.StartSeconds) / Day * width);
            var rect = new Rect(left, 8, segWidth, 30);
            dc.DrawRoundedRectangle(SegmentFill, null, rect, 3, 3);
            dc.DrawRoundedRectangle(null, SegmentInset, Inset(rect, 0.5), 3, 3);
        }

        // Export selection band
        if (SelInSeconds is { } inSec && SelOutSeconds is { } outSec && outSec > inSec)
        {
            var left = inSec / Day * width;
            var bandWidth = Math.Max(2, (outSec - inSec) / Day * width);
            dc.DrawRoundedRectangle(SelectionFill, SelectionBorder, new Rect(left, 4, bandWidth, TrackHeight - 8), 4, 4);
        }

        // Playhead
        var px = Math.Clamp(CurrentSeconds, 0, Day) / Day * width;
        dc.DrawRectangle(PlayheadFill, null, new Rect(px - 1, -2, 2, TrackHeight + 4));
        dc.DrawLine(PlayheadHalo, new Point(px - 1.5, -2), new Point(px - 1.5, TrackHeight + 2));
        dc.DrawLine(PlayheadHalo, new Point(px + 1.5, -2), new Point(px + 1.5, TrackHeight + 2));

        // Hour labels
        var labelY = TrackHeight + LabelGap;
        for (var h = 0; h <= 24; h += 3)
        {
            var text = new FormattedText($"{h:00}:00", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 10, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var x = h * 3600 / Day * width;
            var tx = h == 0 ? 0 : h == 24 ? width - text.Width : x - text.Width / 2;
            dc.DrawText(text, new Point(Math.Clamp(tx, 0, width - text.Width), labelY));
        }
    }

    private IEnumerable<TimelineSegment> EnumerateSegments()
    {
        if (SegmentsSource is null)
        {
            yield break;
        }

        foreach (var item in SegmentsSource)
        {
            if (item is TimelineSegment segment)
            {
                yield return segment;
            }
        }
    }

    private static Rect Inset(Rect r, double by) =>
        new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(string hex, double thickness)
    {
        var pen = new Pen(Frozen(hex), thickness);
        pen.Freeze();
        return pen;
    }
}
