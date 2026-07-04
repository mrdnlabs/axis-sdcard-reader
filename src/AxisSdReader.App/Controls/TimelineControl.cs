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
/// The detail track: a scrolling window of time beneath a stationary center cursor
/// (DVR-style). Drag to scrub, click to jump, mouse-wheel to step through the fixed zoom
/// spans. Wide-enough segments carry an inline "start · duration" label. All colors come
/// from the theme tokens and refresh on theme change.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    /// <summary>Fixed zoom spans (seconds across the full track width): 5m · 15m · 30m · 1h · 3h · 6h · 24h.</summary>
    public static readonly double[] Spans = [300, 900, 1800, 3600, 10800, 21600, 86400];

    private const double HandleZone = 13;   // space above the track for the top triangle handle
    private const double TrackHeight = 52;
    private const double LabelGap = 3;
    private const double LabelHeight = 15;
    private const double ClickDragThresholdPx = 4;

    private static readonly Typeface MonoFace = new("Consolas");

    private bool _dragging;
    private double _dragStartX;
    private double _dragStartCenter;
    private double _dragTotalDeltaPx;

    public event Action<double>? Scrubbing;
    public event Action<double>? ScrubCommitted;

    public static readonly DependencyProperty SegmentsSourceProperty = DependencyProperty.Register(
        nameof(SegmentsSource), typeof(IEnumerable), typeof(TimelineControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterSecondsProperty = DependencyProperty.Register(
        nameof(CenterSeconds), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpanSecondsProperty = DependencyProperty.Register(
        nameof(SpanSeconds), typeof(double), typeof(TimelineControl),
        new FrameworkPropertyMetadata(1800.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

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

    public TimelineControl()
    {
        MinHeight = HandleZone + TrackHeight + LabelGap + LabelHeight;
        Cursor = Cursors.SizeWE;
        ToolTip = "Drag to scrub · click to jump · scroll to zoom";
        Theme.Changed += InvalidateVisual;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(width, HandleZone + TrackHeight + LabelGap + LabelHeight);
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
        var spp = SpanSeconds / Math.Max(1, ActualWidth);
        var newCenter = _dragStartCenter - deltaPx * spp;
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
            var x = e.GetPosition(this).X;
            var spp = SpanSeconds / Math.Max(1, ActualWidth);
            ScrubCommitted?.Invoke(CenterSeconds + (x - ActualWidth / 2) * spp);
        }
        else
        {
            ScrubCommitted?.Invoke(CenterSeconds);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        StepSpan(e.Delta > 0 ? -1 : +1);
        e.Handled = true;
    }

    /// <summary>Steps to the next/previous fixed zoom span (also used by the zoom stepper buttons).</summary>
    public void StepSpan(int direction)
    {
        var index = NearestSpanIndex(SpanSeconds) + direction;
        index = Math.Clamp(index, 0, Spans.Length - 1);
        SetCurrentValue(SpanSecondsProperty, Spans[index]);
    }

    public static int NearestSpanIndex(double span)
    {
        var best = 0;
        for (var i = 1; i < Spans.Length; i++)
        {
            if (Math.Abs(Spans[i] - span) < Math.Abs(Spans[best] - span))
            {
                best = i;
            }
        }

        return best;
    }

    // --- rendering ------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var span = SpanSeconds;
        var spp = span / width;
        var leftSeconds = CenterSeconds - span / 2;
        var rightSeconds = CenterSeconds + span / 2;
        var trackTop = HandleZone;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var trackFill = Theme.Brush("Subtle2");
        var trackBorder = new Pen(Theme.Brush("TlBorder"), 1);
        var gridPen = new Pen(Theme.Brush("TlLine"), 1);
        var dayPen = new Pen(Theme.Brush("Border2"), 1);
        var accent = Theme.Brush("Accent");
        var selFill = Theme.Brush("SelFill");
        var selPen = new Pen(Theme.Brush("Sel"), 1.5);
        var playhead = Theme.Brush("Playhead");
        var playheadHalo = new Pen(Theme.Brush("PlayheadHalo"), 1);
        var faint = Theme.Brush("Faint");

        var trackRect = new Rect(0, trackTop, width, TrackHeight);
        dc.DrawRoundedRectangle(trackFill, trackBorder, Inset(trackRect, 0.5), 7, 7);

        dc.PushClip(new RectangleGeometry(trackRect));

        // Gridlines + collect tick label positions. Anchor ticks to LOCAL wall-clock boundaries
        // (multiples of `step` from local midnight), not to UTC-second multiples: on the UTC axis the
        // latter drift off round local times — and never coincide with midnight — in half-hour-offset
        // zones and at wide zoom. Every TickStep divides 86400, so boundaries stay aligned across days.
        var step = TickStep(span);
        var labels = new List<(double X, string Text, bool Day)>();
        var startLocal = TimeAxis.ToDateTime(leftSeconds);
        var localMidnight = startLocal.Date;
        var secondsIntoDay = Math.Floor((startLocal - localMidnight).TotalSeconds / step) * step;
        for (var boundary = localMidnight.AddSeconds(secondsIntoDay); ; boundary = boundary.AddSeconds(step))
        {
            var t = TimeAxis.ToSeconds(boundary);
            if (t > rightSeconds + step)
            {
                break;
            }

            var x = Math.Round((t - leftSeconds) / spp) + 0.5;
            if (x < -60 || x > width + 60)
            {
                continue;
            }

            var isMidnight = boundary.TimeOfDay == TimeSpan.Zero;
            dc.DrawLine(isMidnight ? dayPen : gridPen, new Point(x, trackTop + 1), new Point(x, trackTop + TrackHeight - 1));
            labels.Add((x, isMidnight ? boundary.ToString("MMM d") : boundary.ToString(span <= 900 ? "HH:mm:ss" : "HH:mm"), isMidnight));
        }

        // Segments with inline labels when wide enough.
        foreach (var segment in EnumerateSegments())
        {
            if (segment.EndSeconds < leftSeconds || segment.StartSeconds > rightSeconds)
            {
                continue;
            }

            var x1 = (segment.StartSeconds - leftSeconds) / spp;
            var x2 = (segment.EndSeconds - leftSeconds) / spp;
            var rect = new Rect(x1, trackTop + 8, Math.Max(2, x2 - x1), 34);
            dc.DrawRoundedRectangle(accent, null, rect, 4, 4);

            if (rect.Width >= 90)
            {
                var start = TimeAxis.ToDateTime(segment.StartSeconds);
                var dur = TimeSpan.FromSeconds(segment.EndSeconds - segment.StartSeconds);
                var durText = dur.TotalHours >= 1 ? $"{(int)dur.TotalHours}h {dur.Minutes}m" : $"{Math.Max(1, (int)dur.TotalMinutes)}m";
                var text = new FormattedText($"{start:HH:mm} · {durText}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoFace, 11, Brushes.White, dpi)
                {
                    MaxTextWidth = Math.Max(0, rect.Width - 16),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(text, new Point(Math.Max(rect.X + 8, 8), trackTop + 8 + 17 - text.Height / 2));
            }
        }

        // Export selection band.
        if (SelInSeconds is { } inSec && SelOutSeconds is { } outSec && outSec > inSec &&
            outSec > leftSeconds && inSec < rightSeconds)
        {
            var x1 = (inSec - leftSeconds) / spp;
            var x2 = (outSec - leftSeconds) / spp;
            dc.DrawRoundedRectangle(selFill, selPen,
                new Rect(x1, trackTop + 4, Math.Max(2, x2 - x1), TrackHeight - 8), 4, 4);
        }

        dc.Pop();

        // Tick labels below the track.
        var labelY = trackTop + TrackHeight + LabelGap;
        foreach (var (x, textStr, isDay) in labels)
        {
            var text = new FormattedText(textStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                MonoFace, 10, isDay ? Theme.Brush("Muted2") : faint, dpi);
            dc.DrawText(text, new Point(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, width - text.Width)), labelY));
        }

        // Stationary center cursor with accent triangle handles (drawn over everything).
        var px = width / 2;
        dc.DrawLine(playheadHalo, new Point(px - 1.5, trackTop - 4), new Point(px - 1.5, trackTop + TrackHeight + 4));
        dc.DrawLine(playheadHalo, new Point(px + 1.5, trackTop - 4), new Point(px + 1.5, trackTop + TrackHeight + 4));
        dc.DrawRectangle(playhead, null, new Rect(px - 1, trackTop - 4, 2, TrackHeight + 8));

        var top = trackTop - 4;
        dc.DrawGeometry(accent, null, Triangle(new Point(px - 6, top - 9), new Point(px + 6, top - 9), new Point(px, top)));
        var bottom = trackTop + TrackHeight + 4;
        dc.DrawGeometry(accent, null, Triangle(new Point(px - 6, bottom + 9), new Point(px + 6, bottom + 9), new Point(px, bottom)));
    }

    private static double TickStep(double span) => span switch
    {
        <= 300 => 30,
        <= 900 => 120,
        <= 1800 => 300,
        <= 3600 => 600,
        <= 10800 => 1800,
        <= 21600 => 3600,
        _ => 3 * 3600,
    };

    private static StreamGeometry Triangle(Point a, Point b, Point c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(b, true, false);
            ctx.LineTo(c, true, false);
        }

        g.Freeze();
        return g;
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
}
