using AxisSdReader.Core.Axis;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Timeline;

namespace AxisSdReader.Core.Agent;

/// <summary>
/// Answers agent requests about one open card — cameras, recordings, coverage, and which media plays when —
/// for both the app (behind its named pipe) and a card image opened directly by the MCP bridge. It hands
/// out only metadata and short-lived loopback URLs (from <see cref="LocalMediaServer"/>) for the chunks a
/// request needs; it never writes files and never accepts paths. Requests are serialized.
/// </summary>
public sealed class AgentService : IDisposable
{
    public const int MaxTimesPerRequest = 64;
    public const int MaxRecordingsPerPage = 500;
    public static readonly TimeSpan MaxCoverageRange = TimeSpan.FromDays(31);
    public static readonly TimeSpan MaxSliceRange = TimeSpan.FromHours(2);
    private const int RefineChunkBudget = 8000;

    private readonly CardCatalog _catalog;
    private readonly Func<AgentCardInfo> _cardInfo;
    private readonly Func<IReadOnlyList<Recording>, CancellationToken, Task> _loadMetadata;
    private readonly Func<RecordingChunk, Stream> _openChunk;
    private readonly LocalMediaServer _media;
    private readonly string _mode;
    private const int MaxCachedLeases = 1024;

    /// <summary>The most media URLs one reply may carry: well inside the lease cache, so a reply never evicts its own
    /// URLs. (The media server holds more leases than this cache, so only the cache's own rule ever revokes one.)</summary>
    private const int MaxChunksPerReply = 160;

    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<long, MediaLease> _chunkLeases = []; // chunk inode -> lease
    private bool _disposed;

    /// <param name="catalog">The card's recordings on the time axis.</param>
    /// <param name="cardInfo">Summary of the card for status replies.</param>
    /// <param name="loadMetadata">Loads chunk metadata for recordings, holding the card's I/O gate as needed.</param>
    /// <param name="openChunk">Opens a chunk for streaming (a gate-serialized stream).</param>
    /// <param name="media">The loopback server that serves chunk URLs.</param>
    /// <param name="mode">"app" or "image", echoed in status.</param>
    public AgentService(CardCatalog catalog, Func<AgentCardInfo> cardInfo,
        Func<IReadOnlyList<Recording>, CancellationToken, Task> loadMetadata, Func<RecordingChunk, Stream> openChunk,
        LocalMediaServer media, string mode)
    {
        _catalog = catalog;
        _cardInfo = cardInfo;
        _loadMetadata = loadMetadata;
        _openChunk = openChunk;
        _media = media;
        _mode = mode;
    }

    public CardCatalog Catalog => _catalog;

    /// <summary>Handles one request; failures come back as <see cref="AgentResponse.Failure"/>, never thrown.</summary>
    public async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return request.Op switch
            {
                "status" => AgentResponse.Success(new AgentStatus(_mode, true, _cardInfo(), null, null)),
                "cameras" => AgentResponse.Success(Cameras()),
                "recordings" => AgentResponse.Success(await RecordingsAsync(request, cancellationToken).ConfigureAwait(false)),
                "coverage" => AgentResponse.Success(await CoverageAsync(request, cancellationToken).ConfigureAwait(false)),
                "locate" => AgentResponse.Success(await LocateAsync(request, cancellationToken).ConfigureAwait(false)),
                "slices" => AgentResponse.Success(await SlicesAsync(request, cancellationToken).ConfigureAwait(false)),
                _ => AgentResponse.Failure($"Unknown operation '{request.Op}'."),
            };
        }
        catch (AgentException ex)
        {
            return AgentResponse.Failure(ex.Message);
        }
        catch (CardClosedException)
        {
            return AgentResponse.Failure("The card was closed.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentResponse.Failure("The card was closed.");
        }
        catch (ObjectDisposedException)
        {
            return AgentResponse.Failure("The card was closed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Never let a malformed request surface internals (or make the elevated app log on demand).
            return AgentResponse.Failure("The request could not be completed.");
        }
        finally
        {
            _serial.Release();
        }
    }

    // --- operations -------------------------------------------------------------------------------------

    // How many cameras and sources one reply lists: far more than any real card has, few enough that a crafted one
    // can't flood a reply (or the agent's context) with names it chose.
    private const int MaxListedCameras = 256;
    private const int MaxListedSources = 64;

    private List<AgentCamera> Cameras() => _catalog.Cameras.Take(MaxListedCameras).Select(camera =>
    {
        var segments = _catalog.AllSegments(camera);
        var bySource = camera.Recordings.GroupBy(r => r.SourceToken, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Count: g.Count(), Sample: g.First()), StringComparer.Ordinal); // one pass
        var sources = camera.Sources.Take(MaxListedSources).Select(source =>
        {
            bySource.TryGetValue(source, out var info);
            return new AgentSource(Clean(source) ?? "", info.Count, Resolution(info.Sample), Codec(info.Sample));
        }).ToList();

        return new AgentCamera(camera.Serial, camera.Name, Clean(camera.Model), sources, camera.Recordings.Count,
            segments.Count > 0 ? At(segments.Min(s => s.StartSeconds)) : null,
            segments.Count > 0 ? At(segments.Max(s => s.EndSeconds)) : null);
    }).ToList();

    private async Task<AgentRecordingPage> RecordingsAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var camera = Camera(request);
        IEnumerable<TimeSegment> segments = request.Source is { Length: > 0 } source
            ? _catalog.Segments(camera, CheckSource(camera, source))
            : _catalog.AllSegments(camera);

        if (request.From is { } from)
        {
            var f = Seconds(from);
            segments = segments.Where(s => s.EndSeconds > f);
        }

        if (request.To is { } to)
        {
            var t = Seconds(to);
            segments = segments.Where(s => s.StartSeconds < t);
        }

        var kinds = Kinds(request);
        if (kinds is not null)
        {
            segments = segments.Where(s => kinds.Contains(s.Recording.Kind));
        }

        var all = segments.OrderBy(s => s.StartSeconds).ToList();
        var offset = Math.Max(0, request.Offset ?? 0);
        var limit = Math.Clamp(request.Limit ?? 50, 1, MaxRecordingsPerPage);
        var page = all.Skip(offset).Take(limit).ToList();

        // Make the page's times exact when that is cheap.
        await RefineAsync(page, cancellationToken).ConfigureAwait(false);
        return new AgentRecordingPage(all.Count, offset, page.Select(ToRecording).ToList());
    }

    private async Task<AgentCoverage> CoverageAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var camera = Camera(request);
        var source = Source(camera, request);
        var (from, to) = Range(request, MaxCoverageRange, "coverage");
        var segments = _catalog.Segments(camera, source);
        var exact = await RefineAsync(CardCatalog.Overlapping(segments, from, to, margin: 3600), cancellationToken).ConfigureAwait(false);
        _catalog.Resort(camera, source);

        var overlapping = CardCatalog.Overlapping(segments, from, to);
        var kinds = Kinds(request);
        var counted = kinds is null ? overlapping : overlapping.Where(s => kinds.Contains(s.Recording.Kind)).ToList();
        var spans = CardCatalog.Coverage(counted, from, to);
        var gaps = CardCatalog.Gaps(counted, from, to);

        const int maxSpans = 1000;
        var covered = CardCatalog.Merge(counted.Select(s => (s.StartSeconds, s.EndSeconds)), from, to).Sum(m => m.End - m.Start);
        return new AgentCoverage(camera.Serial, source, At(from), At(to), Math.Round(covered, 1),
            spans.Take(maxSpans).Select(s => new AgentSpan(KindName(s.Kind), At(s.StartSeconds), At(s.EndSeconds), Math.Round(s.Seconds, 1))).ToList(),
            gaps.Take(maxSpans).Select(g => new AgentSpan("gap", At(g.Start), At(g.End), Math.Round(g.End - g.Start, 1))).ToList(),
            exact, spans.Count > maxSpans || gaps.Count > maxSpans);
    }

    private async Task<List<AgentMediaPoint>> LocateAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var times = request.Times is { Count: > 0 } list ? list : request.Time is { } single ? [single] : null;
        if (times is null)
        {
            throw new AgentException("Give a time (or times) to locate.");
        }

        if (times.Count > MaxTimesPerRequest)
        {
            throw new AgentException($"At most {MaxTimesPerRequest} times per request.");
        }

        var camera = Camera(request);
        var source = Source(camera, request);
        IReadOnlyList<TimeSegment> segments = _catalog.Segments(camera, source);
        if (request.RecordingId is { Length: > 0 } id)
        {
            var only = _catalog.FindRecording(id) ?? throw new AgentException($"No recording '{id}' on this card.");
            segments = [only];
        }

        var seconds = times.Select(Seconds).ToList();

        // Exact placement near each moment. Estimates can be hours out for a recording without its XML (the
        // folder time is camera-local), so widen once if the first pass finds nothing.
        await RefineAsync(seconds.SelectMany(t => CardCatalog.Overlapping(segments, t, t, margin: 3600)).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        if (seconds.Any(t => CardCatalog.Locate(segments, t) is null))
        {
            await RefineAsync(seconds.SelectMany(t => CardCatalog.Overlapping(segments, t, t, margin: 15 * 3600)).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
        }

        _catalog.Resort(camera, source);

        // Each hit carries the chunk it was located in, so a chunk list replaced meanwhile can't shift it.
        var hits = seconds.Select(t => (Time: t, Hit: CardCatalog.Locate(segments, t))).ToList();
        var chunks = hits.Where(h => h.Hit is not null)
            .Select(h => h.Hit!.Value.Chunk)
            .Distinct()
            .ToList();
        var urls = Lease(chunks);

        var ordered = segments.OrderBy(s => s.StartSeconds).ToList();
        return hits.Zip(times).Select(pair =>
        {
            var (t, hit) = pair.First;
            if (hit is { } h)
            {
                return new AgentMediaPoint(pair.Second, true, h.Segment.Recording.Id.Raw, KindName(h.Segment.Recording.Kind),
                    urls[h.Chunk].AbsoluteUri, Math.Round(h.Offset.TotalSeconds, 3), null, null);
            }

            var previous = ordered.LastOrDefault(s => s.EndSeconds <= t);
            var next = ordered.FirstOrDefault(s => s.StartSeconds > t);
            return new AgentMediaPoint(pair.Second, false, null, null, null, 0,
                previous is null ? null : At(previous.EndSeconds), next is null ? null : At(next.StartSeconds));
        }).ToList();
    }

    private async Task<List<AgentSlice>> SlicesAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        var camera = Camera(request);
        var source = Source(camera, request);
        var (from, to) = Range(request, MaxSliceRange, "a clip");
        IReadOnlyList<TimeSegment> segments = _catalog.Segments(camera, source);
        if (request.RecordingId is { Length: > 0 } id)
        {
            segments = [_catalog.FindRecording(id) ?? throw new AgentException($"No recording '{id}' on this card.")];
        }

        await RefineAsync(CardCatalog.Overlapping(segments, from, to, margin: 3600), cancellationToken).ConfigureAwait(false);
        _catalog.Resort(camera, source);

        var kinds = Kinds(request);
        var slices = CardCatalog.Overlapping(segments, from, to)
            .Where(s => s.Recording.Chunks.Count > 0 && (kinds is null || kinds.Contains(s.Recording.Kind)))
            .OrderBy(s => s.StartSeconds)
            .Select(s => s.SliceFor(from, to))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        var chunks = slices.SelectMany(s => s.Chunks).Distinct().ToList();
        if (chunks.Count > MaxChunksPerReply)
        {
            throw new AgentException("That range spans too many recordings at once; ask for a shorter range or one type of recording.");
        }

        var urls = Lease(chunks);
        return slices.Select(s =>
        {
            var start = TimeAxis.ToSeconds(s.WallClockStart);
            return new AgentSlice(s.Recording.Id.Raw, KindName(s.Recording.Kind), At(start), At(start + s.Duration.TotalSeconds),
                Math.Round(s.Duration.TotalSeconds, 3), s.Chunks.Select(c => urls[c].AbsoluteUri).ToList(),
                Math.Round(s.TrimStart.TotalSeconds, 3), Math.Round(s.TrimEnd.TotalSeconds, 3), Codec(s.Recording));
        }).ToList();
    }

    // --- helpers ----------------------------------------------------------------------------------------

    /// <summary>Loads metadata for the unrefined segments (within budget) and refines them. Returns whether every
    /// segment given is now exact.</summary>
    private async Task<bool> RefineAsync(IReadOnlyCollection<TimeSegment> segments, CancellationToken cancellationToken)
    {
        var pending = segments.Where(s => !s.IsRefined).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        var budget = RefineChunkBudget;
        var chosen = new List<TimeSegment>();
        foreach (var segment in pending)
        {
            budget -= segment.Recording.Chunks.Count;
            if (budget < 0)
            {
                break;
            }

            chosen.Add(segment);
        }

        if (chosen.Count > 0)
        {
            await _loadMetadata(chosen.Select(s => s.Recording).ToList(), cancellationToken).ConfigureAwait(false);
            foreach (var segment in chosen)
            {
                segment.Refine();
            }
        }

        return chosen.Count == pending.Count;
    }

    /// <summary>
    /// A URL for each chunk. Each chunk gets one lease, reused (and its expiry extended) while it stays live, so
    /// repeating a request costs nothing new; the cache is bounded, and the oldest leases are revoked beyond it.
    /// </summary>
    private Dictionary<RecordingChunk, Uri> Lease(IReadOnlyList<RecordingChunk> chunks)
    {
        var map = new Dictionary<RecordingChunk, Uri>();
        lock (_chunkLeases)
        {
            foreach (var chunk in chunks)
            {
                if (map.ContainsKey(chunk))
                {
                    continue;
                }

                // Touch first, then check: once touched a live lease can't expire before this reply is used, and the
                // check catches one the sweeper revoked in between (never hand out a dead URL).
                if (_chunkLeases.TryGetValue(chunk.Inode, out var lease) && lease.IsLive)
                {
                    lease.Touch();
                }

                if (lease is null || !lease.IsLive)
                {
                    var source = new MediaSource("c0.mkv", chunk.SizeBytes, () => _openChunk(chunk));
                    lease = _media.CreateLease([source], TimeSpan.FromMinutes(60)); // a long export reaches its last chunk late
                    _chunkLeases[chunk.Inode] = lease;
                }

                map[chunk] = lease.Urls[0];
            }

            if (_chunkLeases.Count > MaxCachedLeases)
            {
                foreach (var stale in _chunkLeases.Where(kv => !kv.Value.IsLive).Select(kv => kv.Key).ToList())
                {
                    _chunkLeases.Remove(stale);
                }

                // Never one this reply hands out, or one a transfer (another request's) is reading right now.
                var inUse = map.Values.ToHashSet();
                foreach (var (inode, lease) in _chunkLeases.OrderBy(kv => kv.Value.ExpiresTicks)
                             .Where(kv => !inUse.Contains(kv.Value.Urls[0]) && !kv.Value.IsBusy)
                             .Take(Math.Max(0, _chunkLeases.Count - MaxCachedLeases)).ToList())
                {
                    lease.Dispose();
                    _chunkLeases.Remove(inode);
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Card-controlled text (trigger names, model strings, source tokens, device names) is passed to agents only
    /// as short, single-line, printable text: it is data from a possibly hostile card, and must not be able to
    /// smuggle long instructions or control characters into an agent's context.
    /// </summary>
    public static string? Clean(string? text, int max = 64)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(Math.Min(text.Length, max));
        foreach (var c in text)
        {
            if (builder.Length >= max)
            {
                break;
            }

            // Control and format characters (zero-width, bidi overrides, BOM) become spaces: nothing card text
            // shows an agent may be invisible or reorder what's around it.
            var category = char.GetUnicodeCategory(c);
            builder.Append(char.IsControl(c) || char.IsSurrogate(c) || category is System.Globalization.UnicodeCategory.Format
                               or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                ? ' '
                : c);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : text.Length > max ? cleaned + "…" : cleaned;
    }

    private CatalogCamera Camera(AgentRequest request) =>
        _catalog.FindCamera(request.Camera, out var error) ?? throw new AgentException(error ?? "Unknown camera.");

    private static string Source(CatalogCamera camera, AgentRequest request) =>
        request.Source is { Length: > 0 } source ? CheckSource(camera, source) : camera.DefaultSource;

    private static string CheckSource(CatalogCamera camera, string source)
    {
        // Exact first: sources are grouped exactly, so "A" and "a" can both exist and each must stay reachable.
        var wanted = source.Trim();
        var match = camera.Sources.FirstOrDefault(s => s.Equals(wanted, StringComparison.Ordinal)) ??
                    camera.Sources.FirstOrDefault(s => s.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new AgentException(
            $"{camera.Name} has no source '{Clean(source, 24)}'. Its sources: {ListSome(camera.Sources.Select(s => Clean(s, 24) ?? ""))}.");
    }

    /// <summary>A few names for a message (card-chosen ones above all): the first <paramref name="max"/>, then a count.</summary>
    public static string ListSome(IEnumerable<string> names, int max = 32)
    {
        var all = names.ToList();
        return all.Count <= max ? string.Join(", ", all) : $"{string.Join(", ", all.Take(max))} and {all.Count - max} more";
    }

    private static (double From, double To) Range(AgentRequest request, TimeSpan max, string what)
    {
        if (request.From is not { } from || request.To is not { } to)
        {
            throw new AgentException("Give both a start (from) and an end (to) time.");
        }

        if (to <= from)
        {
            throw new AgentException("The end time must be after the start time.");
        }

        if (to - from > max)
        {
            throw new AgentException($"That range is too long for {what} (max {FormatSpan(max)}).");
        }

        return (Seconds(from), Seconds(to));
    }

    private static HashSet<RecordingKind>? Kinds(AgentRequest request)
    {
        if (request.Kinds is not { Count: > 0 } names)
        {
            return null;
        }

        if (names.Count > 16)
        {
            throw new AgentException("Too many recording types.");
        }

        var kinds = new HashSet<RecordingKind>();
        foreach (var name in names)
        {
            kinds.Add(ParseKind(name) ?? throw new AgentException(
                $"Unknown recording type '{Clean(name, 24)}'. Use continuous, motion, manual, scheduled or other."));
        }

        return kinds;
    }

    public static RecordingKind? ParseKind(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "continuous" => RecordingKind.Continuous,
        "motion" or "event" => RecordingKind.Event,
        "manual" => RecordingKind.Manual,
        "scheduled" => RecordingKind.Scheduled,
        "other" => RecordingKind.Other,
        _ => null,
    };

    public static string KindName(RecordingKind kind) => kind switch
    {
        RecordingKind.Continuous => "continuous",
        RecordingKind.Event => "motion",
        RecordingKind.Manual => "manual",
        RecordingKind.Scheduled => "scheduled",
        _ => "other",
    };

    private AgentRecording ToRecording(TimeSegment segment)
    {
        var r = segment.Recording;
        return new AgentRecording(r.Id.Raw, r.Id.CameraSerial, Clean(r.SourceToken) ?? "", KindName(r.Kind), Clean(r.Trigger),
            At(segment.StartSeconds), At(segment.EndSeconds), Math.Round(segment.DurationSeconds, 3), segment.IsRefined,
            r.Chunks.Count, r.TotalSizeBytes, r.WasInterrupted, Codec(r), Resolution(r));
    }

    public static string? Codec(Recording? recording) => recording?.VideoCodecId switch
    {
        null => null,
        "V_AV1" => "AV1",
        "V_MPEG4/ISO/AVC" => "H.264",
        "V_MPEGH/ISO/HEVC" => "H.265",
        var other => Clean(other, 32),
    };

    public static string? Resolution(Recording? recording) =>
        recording?.Info is { Width: { } w, Height: { } h } ? $"{w}x{h}" : null;

    /// <summary>An axis position as an instant in the machine's local time zone.</summary>
    public static DateTimeOffset At(double seconds) => new DateTimeOffset(TimeAxis.ToUtc(seconds)).ToLocalTime();

    public static double Seconds(DateTimeOffset time) => TimeAxis.ToSeconds(time.UtcDateTime);

    private static string FormatSpan(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{span.TotalDays:0} days" : span.TotalHours >= 1 ? $"{span.TotalHours:0} hours" : $"{span.TotalMinutes:0} minutes";

    /// <summary>Revokes every URL this service handed out.</summary>
    public void RevokeLeases()
    {
        lock (_chunkLeases)
        {
            foreach (var lease in _chunkLeases.Values)
            {
                lease.Dispose();
            }

            _chunkLeases.Clear();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        RevokeLeases();
    }
}
