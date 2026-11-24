using System.Collections;
using AxisSdReader.App.ViewModels;
using AxisSdReader.Core.Axis;

namespace AxisSdReader.App.Controls;

/// <summary>
/// A timeline's recordings as drawing layers: per kind (lowest overlay priority first, so higher kinds land on top),
/// the time spans where at least one recording of that kind runs, overlapping ones merged — built once per change
/// to the collection. A repaint finds the visible spans by binary search and draws at most one shape per pixel run,
/// so what it costs depends on the screen, not on how many recordings a card holds.
/// </summary>
internal sealed class TimelineLayers
{
    private TimelineLayers(List<Layer> layers) => Layers = layers;

    public IReadOnlyList<Layer> Layers { get; }

    /// <summary>One kind's spans, disjoint and in time order (so their ends are in order too).</summary>
    public sealed record Layer(RecordingKind Kind, double[] Starts, double[] Ends);

    public static TimelineLayers Build(IEnumerable? source)
    {
        // Only spans that can be drawn (and searched: the ends must stay in order), however a card's times came out.
        var segments = (source?.OfType<TimelineSegment>() ?? [])
            .Where(s => double.IsFinite(s.StartSeconds) && double.IsFinite(s.EndSeconds));
        var layers = segments
            .GroupBy(s => s.Kind)
            .OrderBy(g => RecordingTypeClassifier.OverlayPriority(g.Key))
            .Select(g =>
            {
                var starts = new List<double>();
                var ends = new List<double>();
                foreach (var segment in g.OrderBy(s => s.StartSeconds))
                {
                    var end = Math.Max(segment.StartSeconds, segment.EndSeconds);

                    // Recordings that overlap are one span; ones that merely touch keep their own (the seam shows).
                    if (starts.Count > 0 && segment.StartSeconds < ends[^1])
                    {
                        ends[^1] = Math.Max(ends[^1], end);
                    }
                    else
                    {
                        starts.Add(segment.StartSeconds);
                        ends.Add(end);
                    }
                }

                return new Layer(g.Key, starts.ToArray(), ends.ToArray());
            })
            .ToList();
        return new TimelineLayers(layers);
    }

    /// <summary>
    /// Calls <paramref name="draw"/>(kind, x1, x2, start, end) for each visible run of each layer, in drawing order.
    /// A span that starts less than a pixel after the current run's start joins it (no screen could show them
    /// apart), so each layer draws at most one shape per pixel column; spans further apart keep their own shapes,
    /// seams and labels.
    /// </summary>
    public void ForEachRun(double left, double right, Func<double, double> toX, Action<RecordingKind, double, double, double, double> draw)
    {
        foreach (var layer in Layers)
        {
            var open = false;
            double runX1 = 0, runX2 = 0, runStart = 0, runEnd = 0;
            for (var i = FirstEndingAtOrAfter(layer.Ends, left); i < layer.Starts.Length && layer.Starts[i] <= right; i++)
            {
                var x1 = toX(layer.Starts[i]);
                var x2 = toX(layer.Ends[i]);
                if (open && x1 < runX1 + 1)
                {
                    runX2 = Math.Max(runX2, x2);
                    runEnd = Math.Max(runEnd, layer.Ends[i]);
                    continue;
                }

                if (open)
                {
                    draw(layer.Kind, runX1, runX2, runStart, runEnd);
                }

                open = true;
                (runX1, runX2, runStart, runEnd) = (x1, x2, layer.Starts[i], layer.Ends[i]);
            }

            if (open)
            {
                draw(layer.Kind, runX1, runX2, runStart, runEnd);
            }
        }
    }

    private static int FirstEndingAtOrAfter(double[] ends, double value)
    {
        int low = 0, high = ends.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (ends[mid] < value)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }
}
