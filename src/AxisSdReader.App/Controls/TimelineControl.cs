using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AxisSdReader.App.ViewModels;

namespace AxisSdReader.App.Controls;

/// <summary>A recording segment on the continuous time axis (TimeAxis seconds).</summary>
public sealed record TimelineSegment(double StartSeconds, double EndSeconds);

/// <summary>
/// Scrolling, zoomable timeline with a fixed center playhead. The strip of time slides
/// beneath the playhead: drag to scrub (the view model is notified live and seeks on
/// release), mouse-wheel to zoom from a multi-day overview down to minutes. Time is
/// continuous — midnight is just a stronger tick with a date label.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double TrackHeight = 46.0;
    private const double LabelGap = 3.0;
    private const double LabelHeight = 16.0;
    private const double MinSecondsPerPixel = 0.05;   // ~70s visible at 1400px
    private const double MaxSecondsPerPixel = 450;    // ~7.3 days visible at 1400px
    private const double ClickDragThresholdPx = 4;

    private static readonly Brush TrackFill = Frozen("#F0F2F5");
    private static readonly Pen TrackBorder = FrozenPen("#E4E7EC", 1);
    private static readonly Pen GridPen = FrozenPen("#E0E4EA", 1);
    private static readonly Pen DayGridPen = FrozenPen("#C6CCD6", 1);
    private static readonly Brush SegmentFill = Frozen("#2F6FED");
    private static readonly Pen SegmentInset = FrozenPen("#2EFFFFFF", 1);
    private static readonly Brush SelectionFill = Frozen("#52F7C948");
    private static readonly Pen SelectionBorder = FrozenPen("#F7C948", 1.5);
    private static readonly Brush PlayheadFill = Frozen("#12203A");
    private static readonly Pen PlayheadHalo = FrozenPen("#99FFFFFF", 1);
    private static readonly Brush LabelBrush = Frozen("#A2ABB5");
    private static readonly Brush DayLabelBrush = Frozen("#6B747F");
    private static readonly Typeface MonoFace = new("Consolas");
    private static readonly Typeface MonoBoldFace = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly double[] TickSteps =
        [60, 120, 300, 600, 900, 1800, 3600, 2 * 3600, 3 * 3600, 6 * 3600, 12 * 3600, 86400];

    private bool _dragging;
    private double _dragStartX;
    private double _dragStartCenter;
    private double _dragTotalDeltaPx;

    /// <summary>Raised while dragging with the previewed center time (display only).</summary>
    public event Action<double>? Scrubbing;

    /// <summary>Raised when a scrub/click completes with the final time to seek to.</summary>
    public event Action<double>? ScrubCommitted;

    public static readonly DependencyProperty SegmentsSourceProperty = DependencyProperty.Register(
        nameof(SegmentsSource), typeof(IEnumerable), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Time under the fixed center playhead (TimeAxis seconds).</summary>
    public static readonly DependencyProperty CenterSecondsProperty = DependencyProperty.Register(
        nameof(CenterSeconds), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondsPerPixelProperty = DependencyProperty.Register(
        nameof(SecondsPerPixel), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public double CenterSeconds
    {
        get => (double)GetValue(CenterSecondsProperty);
        set => SetValue(CenterSecondsProperty, value);
    }

    public double SecondsPerPixel
    {
        get => (double)GetValue(SecondsPerPixelProperty);
        set => SetValue(SecondsPerPixelProperty, value);
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
        Cursor = Cursors.SizeWE;
        ToolTip = "Drag to scrub · scroll to zoom";
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, TrackHeight + LabelGap + LabelHeight);
    }

    // --- interaction ----------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartCenter = CenterSeconds;
        _dragTotalDeltaPx = 0;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
        {
            return;
        }

        var deltaPx = e.GetPosition(this).X - _dragStartX;
        _dragTotalDeltaPx = Math.Max(_dragTotalDeltaPx, Math.Abs(deltaPx));

        // Dragging the strip right moves the view into the past (content follows the hand).
        // SetCurrentValue keeps the CenterSeconds binding alive for after the drag.
        var newCenter = _dragStartCenter - deltaPx * SecondsPerPixel;
        SetCurrentValue(CenterSecondsProperty, newCenter);
        Scrubbing?.Invoke(newCenter);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();

        if (_dragTotalDeltaPx < ClickDragThresholdPx)
        {
            // A plain click: bring the clicked time to the playhead.
            var x = e.GetPosition(this).X;
            var clicked = CenterSeconds + (x - ActualWidth / 2) * SecondsPerPixel;
            ScrubCommitted?.Invoke(clicked);
        }
        else
        {
            ScrubCommitted?.Invoke(CenterSeconds);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var factor = Math.Pow(1.3, -e.Delta / 120.0);
        SetCurrentValue(SecondsPerPixelProperty,
            Math.Clamp(SecondsPerPixel * factor, MinSecondsPerPixel, MaxSecondsPerPixel));
        e.Handled = true;
    }

    // --- rendering ------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var spp = SecondsPerPixel;
        var leftSeconds = CenterSeconds - width / 2 * spp;
        var rightSeconds = CenterSeconds + width / 2 * spp;

        dc.PushClip(new RectangleGeometry(new Rect(0, -2, width, TrackHeight + LabelGap + LabelHeight + 4)));

        var trackRect = new Rect(0, 0, width, TrackHeight);
        dc.DrawRoundedRectangle(TrackFill, TrackBorder, Inset(trackRect, 0.5), 7, 7);

        // Ticks: pick the smallest step giving >= 90px label spacing; midnights always shown.
        var step = TickSteps.FirstOrDefault(s => s / spp >= 90, 86400);
        var labelY = TrackHeight + LabelGap;
        var firstTick = Math.Floor(leftSeconds / step) * step;
        for (var t = firstTick; t <= rightSeconds + step; t += step)
        {
            var x = Math.Round((t - leftSeconds) / spp) + 0.5;
            if (x < -50 || x > width + 50)
            {
                continue;
            }

            var time = TimeAxis.ToDateTime(t);
            var isMidnight = time.TimeOfDay == TimeSpan.Zero;
            dc.DrawLine(isMidnight ? DayGridPen : GridPen, new Point(x, 1), new Point(x, TrackHeight - 1));

            var label = isMidnight ? time.ToString("MMM d") : time.ToString("HH:mm");
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                isMidnight ? MonoBoldFace : MonoFace, 10, isMidnight ? DayLabelBrush : LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x - text.Width / 2, labelY));
        }

        // Recording segments.
        foreach (var segment in EnumerateSegments())
        {
            if (segment.EndSeconds < leftSeconds || segment.StartSeconds > rightSeconds)
            {
                continue;
            }

            var x1 = (segment.StartSeconds - leftSeconds) / spp;
            var x2 = (segment.EndSeconds - leftSeconds) / spp;
            var rect = new Rect(x1, 8, Math.Max(2, x2 - x1), 30);
            dc.DrawRoundedRectangle(SegmentFill, null, rect, 3, 3);
            dc.DrawRoundedRectangle(null, SegmentInset, Inset(rect, 0.5), 3, 3);
        }

        // Export selection band.
        if (SelInSeconds is { } inSec && SelOutSeconds is { } outSec && outSec > inSec &&
            outSec > leftSeconds && inSec < rightSeconds)
        {
            var x1 = (inSec - leftSeconds) / spp;
            var x2 = (outSec - leftSeconds) / spp;
            dc.DrawRoundedRectangle(SelectionFill, SelectionBorder,
                new Rect(x1, 4, Math.Max(2, x2 - x1), TrackHeight - 8), 4, 4);
        }

        // Fixed center playhead.
        var px = width / 2;
        dc.DrawRectangle(PlayheadFill, null, new Rect(px - 1, -2, 2, TrackHeight + 4));
        dc.DrawLine(PlayheadHalo, new Point(px - 1.5, -2), new Point(px - 1.5, TrackHeight + 2));
        dc.DrawLine(PlayheadHalo, new Point(px + 1.5, -2), new Point(px + 1.5, TrackHeight + 2));

        dc.Pop();
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
