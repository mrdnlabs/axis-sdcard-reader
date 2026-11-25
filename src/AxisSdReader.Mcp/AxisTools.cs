using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AxisSdReader.Core.Agent;
using AxisSdReader.Core.Export;
using AxisSdReader.Core.Media;
using AxisSdReader.Core.Security;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AxisSdReader.Mcp;

/// <summary>Settings for the bridge.</summary>
public sealed record BridgeOptions(string ClipsDirectory, string? FfmpegPath, int MaxClipMinutes);

/// <summary>Recording types, as tools accept them (a closed set, so clients see an enum in the schema).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecordingType>))]
public enum RecordingType
{
    [JsonStringEnumMemberName("continuous")] Continuous,
    [JsonStringEnumMemberName("motion")] Motion,
    [JsonStringEnumMemberName("manual")] Manual,
    [JsonStringEnumMemberName("scheduled")] Scheduled,
    [JsonStringEnumMemberName("other")] Other,
}

[JsonConverter(typeof(JsonStringEnumConverter<ImageFormatOption>))]
public enum ImageFormatOption
{
    [JsonStringEnumMemberName("jpeg")] Jpeg,
    [JsonStringEnumMemberName("png")] Png,
}

[JsonConverter(typeof(JsonStringEnumConverter<ClipFormatOption>))]
public enum ClipFormatOption
{
    [JsonStringEnumMemberName("mp4")] Mp4,
    [JsonStringEnumMemberName("mkv")] Mkv,
}

/// <summary>
/// The MCP tools. Each returns a <see cref="CallToolResult"/> with structured content matching the tool's
/// advertised output schema, the same data as JSON text (for clients that predate structured output), and
/// images where the tool shows footage. Failures come back as <c>isError</c> results with a message meant for
/// the agent to act on — never as exceptions, which an MCP client would only see as a generic error.
/// </summary>
[McpServerToolType]
public sealed partial class AxisTools(IAgentChannel channel, BridgeOptions options)
{
    /// <summary>The order tools are listed in (the spec asks for a deterministic order).</summary>
    public static readonly string[] Order =
        ["get_status", "list_cameras", "list_recordings", "get_coverage", "get_frame", "get_contact_sheet", "export_clip", "get_app_view", "show_in_app"];

    // --- card & recordings -----------------------------------------------------------------------------

    [McpServerTool(Name = "get_status", Title = "Get status", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(StatusOutput))]
    [Description("Check the connection to the Axis SD Card Reader and what is open: the card (name, encrypted, " +
                 "read-only protection), how many cameras and recordings it has and the date range of its footage, " +
                 "and — in app mode — what the app is showing right now. Call this first.")]
    public async Task<CallToolResult> GetStatus(CancellationToken cancellationToken = default)
    {
        var reply = await channel.SendAsync(new AgentRequest { Op = "status" }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var status = reply.Read<AgentStatus>();
        var ffmpegAvailable = Ffmpeg() is not null;
        var summary = status.CardOpen && status.Card is { } card
            ? $"{card.Name} ({card.Medium}{(card.Encrypted ? ", encrypted" : "")}): {card.Cameras} camera(s), {card.Recordings} recording(s)" +
              (card.FirstRecording is { } f && card.LastRecording is { } l ? $", {Format(f)} to {Format(l)}." : ".")
            : status.Message ?? "No card is open.";
        var ffmpeg = ffmpegAvailable
            ? ""
            : " FFmpeg was not found, so frames, contact sheets and clips are unavailable (install it, or pass --ffmpeg).";
        return Result($"Mode: {(channel.Mode == "app" ? "connected to the app" : "reading a card image")}. {summary}{ffmpeg}",
            new StatusOutput(status.Mode, status.CardOpen, status.Card, status.View, status.Message, ffmpegAvailable));
    }

    [McpServerTool(Name = "list_cameras", Title = "List cameras", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(CamerasOutput))]
    [Description("List the cameras on the card: id (serial), name (as shown in the app, e.g. \"Camera 3F2A\"), " +
                 "model when known, recording sources (lenses of multi-sensor cameras), and first/last footage times. " +
                 "Other tools accept a camera by full serial, its last four characters, or its name.")]
    public async Task<CallToolResult> ListCameras(CancellationToken cancellationToken = default)
    {
        var reply = await channel.SendAsync(new AgentRequest { Op = "cameras" }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var cameras = reply.Read<List<AgentCamera>>();
        return Result($"{cameras.Count} camera(s).", new CamerasOutput(cameras));
    }

    [McpServerTool(Name = "list_recordings", Title = "List recordings", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AgentRecordingPage))]
    [Description("List recordings with their type (continuous, motion, manual, scheduled, other), start and end " +
                 "time, duration and id. Filter by camera, source, time window (recordings overlapping it) and type. " +
                 "\"motion\" means event-triggered: motion detection, analytics, alarms, I/O. Results are paged.")]
    public async Task<CallToolResult> ListRecordings(
        [Description("Camera serial, last four characters, or name. Optional when the card has one camera.")] string? camera = null,
        [Description("Recording source (lens) for multi-sensor cameras; omit for all sources.")] string? source = null,
        [Description("Window start, local time, e.g. 2026-07-02T08:00:00 (an offset or Z is also accepted).")] string? from = null,
        [Description("Window end, local time.")] string? to = null,
        [Description("Only these types.")] RecordingType[]? kinds = null,
        [Description("Skip this many results (paging).")][Range(0, int.MaxValue)] int offset = 0,
        [Description("At most this many results.")][Range(1, 500)] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryTime(from, "from", out var fromTime, out var error) || !TryTime(to, "to", out var toTime, out error))
        {
            return Error(error);
        }

        var reply = await channel.SendAsync(new AgentRequest
        {
            Op = "recordings", Camera = camera, Source = source, From = fromTime, To = toTime,
            Kinds = Kinds(kinds), Offset = offset, Limit = limit,
        }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var page = reply.Read<AgentRecordingPage>();
        var more = page.Offset + page.Items.Count < page.Total ? $" Use offset={page.Offset + page.Items.Count} for more." : "";
        return Result($"{page.Items.Count} of {page.Total} recording(s).{more}", page);
    }

    [McpServerTool(Name = "get_coverage", Title = "Get coverage", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AgentCoverage))]
    [Description("Show when footage exists between two times for one camera source: merged spans per recording type " +
                 "and the gaps between them. Use it to find motion events in a day, or to see what a period holds " +
                 "before asking for frames. Up to 31 days per call.")]
    public async Task<CallToolResult> GetCoverage(
        [Description("Range start, local time, e.g. 2026-07-02T00:00:00.")] string from,
        [Description("Range end, local time.")] string to,
        [Description("Camera serial, last four characters, or name. Optional when the card has one camera.")] string? camera = null,
        [Description("Recording source (lens); defaults to the camera's first source.")] string? source = null,
        [Description("Only these types.")] RecordingType[]? kinds = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryTime(from, "from", out var fromTime, out var error, required: true) ||
            !TryTime(to, "to", out var toTime, out error, required: true))
        {
            return Error(error);
        }

        var reply = await channel.SendAsync(new AgentRequest
        {
            Op = "coverage", Camera = camera, Source = source, From = fromTime, To = toTime, Kinds = Kinds(kinds),
        }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var coverage = reply.Read<AgentCoverage>();
        var total = (coverage.To - coverage.From).TotalSeconds;
        var motion = coverage.Spans.Where(s => s.Kind == "motion").ToList();
        var summary = $"{FormatDuration(coverage.CoveredSeconds)} of footage in {FormatDuration(total)} " +
                      $"({coverage.Spans.Count} span(s), {coverage.Gaps.Count} gap(s){(motion.Count > 0 ? $", {motion.Count} motion span(s)" : "")})." +
                      (coverage.Exact ? "" : " Some times are estimates (too many recordings to load exactly); narrow the range for exact times.");
        return Result(summary, coverage);
    }

    // --- seeing footage --------------------------------------------------------------------------------

    [McpServerTool(Name = "get_frame", Title = "Get frame", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(FrameOutput))]
    [Description("See the video at a moment: returns the frame (an image) from the recording that plays at that time " +
                 "— the one the app's timeline shows on top where recordings overlap — with the timestamp burned in. " +
                 "If there's no footage then, says when the nearest recordings end and start.")]
    public async Task<CallToolResult> GetFrame(
        [Description("The moment, local time, e.g. 2026-07-02T08:15:03 (fractions of a second allowed).")] string time,
        [Description("Camera serial, last four characters, or name. Optional when the card has one camera.")] string? camera = null,
        [Description("Recording source (lens); defaults to the camera's first source.")] string? source = null,
        [Description("Take the frame from this recording id instead of the one on top.")] string? recordingId = null,
        [Description("Width in pixels. Larger shows more detail but costs more tokens.")][Range(64, 1920)] int width = 960,
        [Description("Image format.")] ImageFormatOption format = ImageFormatOption.Jpeg,
        CancellationToken cancellationToken = default)
    {
        if (!TryTime(time, "time", out var when, out var error, required: true))
        {
            return Error(error);
        }

        if (Ffmpeg() is not { } ffmpeg)
        {
            return Error(NoFfmpeg);
        }

        var png = format == ImageFormatOption.Png;
        var reply = await channel.SendAsync(new AgentRequest
        {
            Op = "locate", Camera = camera, Source = source, RecordingId = recordingId, Times = [when!.Value],
        }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var point = reply.Read<List<AgentMediaPoint>>().Single();
        if (!point.Found || point.Url is null)
        {
            return Error(Gap(point));
        }

        try
        {
            var image = await FrameExtractor.ExtractFrameAsync(ffmpeg, new Uri(point.Url), TimeSpan.FromSeconds(point.OffsetSeconds),
                width, png ? FrameFormat.Png : FrameFormat.Jpeg, Format(point.Requested, seconds: true), cancellationToken);
            var mime = png ? "image/png" : "image/jpeg";
            return Result($"Frame at {Format(point.Requested, seconds: true)} from {point.Kind} recording {point.RecordingId}.",
                new FrameOutput(point.Requested, point.RecordingId, point.Kind, mime),
                ImageContentBlock.FromBytes(image, mime));
        }
        catch (IOException ex)
        {
            return Error($"No frame could be decoded there: {ex.Message}");
        }
    }

    [McpServerTool(Name = "get_contact_sheet", Title = "Get contact sheet", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ContactSheetOutput))]
    [Description("See a whole period at a glance: one image with evenly spaced, timestamped frames across the footage " +
                 "between two times (gaps are skipped, so every cell shows video). The quickest way to find when " +
                 "something happened — then use get_frame, or a narrower sheet, around the interesting cells.")]
    public async Task<CallToolResult> GetContactSheet(
        [Description("Range start, local time.")] string from,
        [Description("Range end, local time.")] string to,
        [Description("Camera serial, last four characters, or name. Optional when the card has one camera.")] string? camera = null,
        [Description("Recording source (lens); defaults to the camera's first source.")] string? source = null,
        [Description("Number of frames.")][Range(1, 36)] int count = 12,
        [Description("Frames per row.")][Range(1, 8)] int columns = 4,
        [Description("Width of each frame in pixels.")][Range(96, 960)] int cellWidth = 384,
        [Description("Only sample footage of these types.")] RecordingType[]? kinds = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryTime(from, "from", out var fromTime, out var error, required: true) ||
            !TryTime(to, "to", out var toTime, out error, required: true))
        {
            return Error(error);
        }

        if (Ffmpeg() is not { } ffmpeg)
        {
            return Error(NoFfmpeg);
        }

        count = Math.Clamp(count, 1, 36);
        var coverageReply = await channel.SendAsync(new AgentRequest
        {
            Op = "coverage", Camera = camera, Source = source, From = fromTime, To = toTime, Kinds = Kinds(kinds),
        }, cancellationToken);
        if (!coverageReply.Ok)
        {
            return Error(coverageReply.Error);
        }

        var coverage = coverageReply.Read<AgentCoverage>();
        if (coverage.Spans.Count == 0)
        {
            return Error($"No footage between {Format(fromTime!.Value)} and {Format(toTime!.Value)}.");
        }

        var times = Sample(coverage.Spans, count);
        var locateReply = await channel.SendAsync(new AgentRequest
        {
            Op = "locate", Camera = camera, Source = coverage.Source, Times = times,
        }, cancellationToken);
        if (!locateReply.Ok)
        {
            return Error(locateReply.Error);
        }

        var points = locateReply.Read<List<AgentMediaPoint>>();
        var multiDay = (toTime!.Value - fromTime!.Value).TotalHours > 20;
        var cells = points.Select((p, i) => new SheetCell(
            p.Found && p.Url is not null ? new Uri(p.Url) : null,
            TimeSpan.FromSeconds(p.OffsetSeconds),
            $"{i + 1}  {Format(p.Requested, seconds: true, dateless: !multiDay)}")).ToList();

        try
        {
            // Cells decode in parallel; report under a lock so the progress values go out strictly increasing.
            var done = 0;
            var gate = new object();
            var cellProgress = progress is null ? null : new SyncProgress<int>(_ =>
            {
                lock (gate)
                {
                    done++;
                    progress.Report(new ProgressNotificationValue { Progress = done, Total = cells.Count + 1, Message = $"Decoded {done} of {cells.Count} frames" });
                }
            });
            var sheet = await FrameExtractor.ContactSheetAsync(ffmpeg, cells, columns, cellWidth, cancellationToken, cellProgress);
            progress?.Report(new ProgressNotificationValue { Progress = cells.Count + 1, Total = cells.Count + 1, Message = "Contact sheet ready" });

            var legend = new StringBuilder($"{cells.Count} frames from {Format(fromTime.Value)} to {Format(toTime.Value)}, left to right, top to bottom:\n");
            foreach (var (point, i) in points.Select((p, i) => (p, i)))
            {
                legend.Append(CultureInfo.InvariantCulture, $"{i + 1}. {Format(point.Requested, seconds: true)}")
                      .Append(point.Found ? $" — {point.Kind} recording {point.RecordingId}\n" : " — no footage\n");
            }

            var output = new ContactSheetOutput(fromTime.Value, toTime.Value, Math.Clamp(columns, 1, Math.Min(8, cells.Count)),
                points.Select((p, i) => new SheetCellOutput(i + 1, p.Requested, p.Found, p.RecordingId, p.Kind)).ToList());
            return Result(legend.ToString(), output, ImageContentBlock.FromBytes(sheet, "image/jpeg"), includeJson: false);
        }
        catch (IOException ex)
        {
            return Error($"The contact sheet could not be made: {ex.Message}");
        }
    }

    [McpServerTool(Name = "export_clip", Title = "Export clip", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(ClipsOutput))]
    [Description("Save the footage between two times as video files (lossless copy of the camera's streams, no " +
                 "re-encoding) and return their paths. One file per recording overlapping the range — continuous and " +
                 "motion recordings can overlap, so filter with kinds or recordingId to get just one. Files go to the " +
                 "server's clips folder; nothing on the card changes.")]
    public async Task<CallToolResult> ExportClip(
        [Description("Clip start, local time.")] string from,
        [Description("Clip end, local time.")] string to,
        [Description("Camera serial, last four characters, or name. Optional when the card has one camera.")] string? camera = null,
        [Description("Recording source (lens); defaults to the camera's first source.")] string? source = null,
        [Description("Only this recording.")] string? recordingId = null,
        [Description("Only these types.")] RecordingType[]? kinds = null,
        [Description("mp4 plays almost anywhere; mkv keeps every original track.")] ClipFormatOption format = ClipFormatOption.Mp4,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryTime(from, "from", out var fromTime, out var error, required: true) ||
            !TryTime(to, "to", out var toTime, out error, required: true))
        {
            return Error(error);
        }

        if (Ffmpeg() is not { } ffmpeg)
        {
            return Error(NoFfmpeg);
        }

        if ((toTime!.Value - fromTime!.Value).TotalMinutes > options.MaxClipMinutes)
        {
            return Error($"Clips are limited to {options.MaxClipMinutes} minutes; ask for a shorter range.");
        }

        // One export at a time: each file's limits assume it is the one filling the drive. And each call has a
        // deadline, whatever its files' own limits add up to.
        await ClipExports.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(30) + (toTime.Value - fromTime.Value) * 8);
            return await ExportClipExclusiveAsync(fromTime.Value, toTime.Value, camera, source, recordingId, kinds,
                format == ClipFormatOption.Mkv, ffmpeg, progress, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error("The export took far longer than this clip should, so it was stopped.");
        }
        finally
        {
            ClipExports.Release();
        }
    }

    private static readonly SemaphoreSlim ClipExports = new(1, 1);

    private async Task<CallToolResult> ExportClipExclusiveAsync(DateTimeOffset fromTime, DateTimeOffset toTime, string? camera,
        string? source, string? recordingId, RecordingType[]? kinds, bool mkv, string ffmpeg,
        IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var request = new AgentRequest
        {
            Op = "slices", Camera = camera, Source = source, RecordingId = recordingId, From = fromTime, To = toTime,
            Kinds = Kinds(kinds),
        };
        var reply = await channel.SendAsync(request, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var slices = reply.Read<List<AgentSlice>>();
        if (slices.Count == 0)
        {
            return Error($"No footage between {Format(fromTime)} and {Format(toTime)}.");
        }

        if (slices.Count > MaxClipFiles)
        {
            return Error($"That range covers {slices.Count} recordings; one call exports at most {MaxClipFiles}. Filter with kinds " +
                         "or recordingId, or ask for a shorter range.");
        }

        Directory.CreateDirectory(options.ClipsDirectory);
        var folder = GuardedFile.ResolveDirectory(options.ClipsDirectory);
        using var clipsFolder = GuardedFile.OpenFolder(folder);

        // The clips folder holds at most so much, old clips and new: an agent asking again and again can't fill the drive.
        var used = FolderBytes(folder);
        if (used >= MaxClipsFolderBytes)
        {
            return Error($"The clips folder {folder} already holds {used / (double)(1L << 30):0.0} GB, the most this server " +
                         "writes there. Delete clips that are no longer needed, then ask again.");
        }

        var links = new List<ContentBlock>();
        var files = new List<ClipFileOutput>();
        var lines = new StringBuilder();
        var mime = mkv ? "video/x-matroska" : "video/mp4";

        // This call's files are held until it ends, and kept only if every one of them was written: a request that fails
        // part way leaves nothing behind (an agent would otherwise ask again and pile up copies).
        var written = new List<GuardedOutputFile>();
        var complete = false;
        try
        {
            for (var i = 0; i < slices.Count; i++)
            {
                var slice = slices[i];
                if (i > 0)
                {
                    // Fresh URLs for each file after the first: the app revokes chunk URLs left idle, and a file late in a
                    // long request may only start after that.
                    var renewed = await channel.SendAsync(request, cancellationToken);
                    if (!renewed.Ok ||
                        renewed.Read<List<AgentSlice>>().FirstOrDefault(s => s.RecordingId == slice.RecordingId && s.Start == slice.Start) is not { } fresh)
                    {
                        return Error("The card changed while the clips were being exported." + (written.Count > 0 ? " None were kept." : ""));
                    }

                    slice = fresh;
                }

                var serial = slice.RecordingId.Split('_').LastOrDefault() ?? "";
                var cameraTag = serial.Length >= 4 ? "Camera-" + Safe(serial[^4..]) : "camera";
                var baseName = $"{slice.Start.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}_{cameraTag}_{Safe(slice.Kind)}";

                // However much a crafted card packs into these minutes, a file stops where such footage would, within the
                // clips folder's room, and never takes the last of the drive.
                if (ExportLimits.For(TimeSpan.FromSeconds(slice.TrimEndSeconds - slice.TrimStartSeconds), null, folder) is not { } fits ||
                    MaxClipsFolderBytes - used < (1L << 20))
                {
                    return Error($"There isn't enough room for the clips in {folder}." + (written.Count > 0 ? " None were kept." : ""));
                }

                var limits = fits with { MaxBytes = Math.Min(fits.MaxBytes, MaxClipsFolderBytes - used) };
                var target = CreateTarget(clipsFolder, baseName, mkv ? "mkv" : "mp4");
                written.Add(target);
                var index = i;
                var fileProgress = progress is null ? null : new SyncProgress<FfmpegProgress>(p => progress.Report(new ProgressNotificationValue
                {
                    Progress = (float)((index + Math.Clamp(p.Fraction, 0, 1)) * 100 / slices.Count),
                    Total = 100,
                    Message = slices.Count > 1 ? $"Exporting file {index + 1} of {slices.Count}" : "Exporting",
                }));

                await FfmpegExporter.ExportAsync(slice.ChunkUrls.Select(u => new Uri(u)).ToList(),
                    TimeSpan.FromSeconds(slice.TrimStartSeconds), TimeSpan.FromSeconds(slice.TrimEndSeconds),
                    mkv ? ExportContainer.Mkv : ExportContainer.Mp4, target.Path, limits, fileProgress, cancellationToken, ffmpeg);

                // The size of the file created here, read through its pinned handle (not whatever the path names now).
                var bytes = target.Length;
                if (bytes == 0)
                {
                    throw new IOException("FFmpeg wrote nothing.");
                }

                used += bytes;
                var uri = new Uri(target.Path).AbsoluteUri;
                lines.Append(CultureInfo.InvariantCulture,
                    $"{target.Path} — {slice.Kind}, {Format(slice.Start, seconds: true)} to {Format(slice.End, seconds: true)}, {bytes / 1048576.0:0.0} MB\n");
                links.Add(new ResourceLinkBlock { Uri = uri, Name = Path.GetFileName(target.Path), MimeType = mime, Size = bytes });
                files.Add(new ClipFileOutput(target.Path, uri, mime, bytes, slice.RecordingId, slice.Kind, slice.Start, slice.End));
            }

            complete = true;
            return Result($"Exported {slices.Count} file(s):\n{lines}", new ClipsOutput(files), [.. links], includeJson: false);
        }
        catch (IOException ex)
        {
            return Error($"Export failed: {ex.Message}" + (written.Count > 1 ? " None of this request's clips were kept." : ""));
        }
        finally
        {
            foreach (var file in written)
            {
                if (!complete)
                {
                    file.DeleteIfUnchanged(); // never leave a partial (or a part of a failed request) behind
                }

                file.Dispose();
            }
        }
    }

    // What the clips folder may hold in all.
    private const long MaxClipsFolderBytes = 20L << 30;

    private static long FolderBytes(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles().Sum(f => f.Length);
        }
        catch
        {
            return long.MaxValue; // unknown counts as full
        }
    }

    // --- the app ---------------------------------------------------------------------------------------

    [McpServerTool(Name = "get_app_view", Title = "Get app view", ReadOnly = true, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AppViewOutput))]
    [Description("See what the user is looking at in the app right now: camera, source, playhead time, whether it " +
                 "is playing, the export range if one is marked — and the frame under the playhead. App mode only.")]
    public async Task<CallToolResult> GetAppView(CancellationToken cancellationToken = default)
    {
        var reply = await channel.SendAsync(new AgentRequest { Op = "view" }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var view = reply.Read<AgentView>();
        var text = view.Time is { } t
            ? $"The app shows {view.CameraName ?? "a camera"} (source {view.Source}) at {Format(t, seconds: true)}, " +
              $"{(view.Playing ? "playing" : "paused")}" +
              (view.SelectionFrom is { } a && view.SelectionTo is { } b ? $", export range {Format(a, seconds: true)} to {Format(b, seconds: true)}." : ".")
            : "No card is open in the app.";

        ImageContentBlock? frame = null;
        if (view.Time is { } time && Ffmpeg() is { } ffmpeg)
        {
            var located = await channel.SendAsync(new AgentRequest
            {
                Op = "locate", Camera = view.Camera, Source = view.Source, Times = [time],
            }, cancellationToken);
            if (located.Ok && located.Read<List<AgentMediaPoint>>().Single() is { Found: true, Url: { } url } point)
            {
                try
                {
                    frame = ImageContentBlock.FromBytes(await FrameExtractor.ExtractFrameAsync(ffmpeg, new Uri(url),
                        TimeSpan.FromSeconds(point.OffsetSeconds), 960, FrameFormat.Jpeg, Format(time, seconds: true), cancellationToken), "image/jpeg");
                }
                catch (IOException)
                {
                    // the view text still stands on its own
                }
            }
        }

        return Result(text, new AppViewOutput(view, frame is not null), frame is null ? [] : [frame]);
    }

    [McpServerTool(Name = "show_in_app", Title = "Show in app", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(AgentView))]
    [Description("Point the user at something: moves the app (paused) to a camera, source and time, and optionally " +
                 "marks an export range there. It doesn't play, export or change anything on the card. App mode only.")]
    public async Task<CallToolResult> ShowInApp(
        [Description("The moment to show, local time.")] string time,
        [Description("Camera serial, last four characters, or name; defaults to the camera the app is showing.")] string? camera = null,
        [Description("Recording source (lens).")] string? source = null,
        [Description("Optional export range start to mark.")] string? selectFrom = null,
        [Description("Optional export range end to mark.")] string? selectTo = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryTime(time, "time", out var when, out var error, required: true) ||
            !TryTime(selectFrom, "selectFrom", out var a, out error) || !TryTime(selectTo, "selectTo", out var b, out error))
        {
            return Error(error);
        }

        var reply = await channel.SendAsync(new AgentRequest
        {
            Op = "show", Camera = camera, Source = source, Time = when, SelectFrom = a, SelectTo = b,
        }, cancellationToken);
        if (!reply.Ok)
        {
            return Error(reply.Error);
        }

        var view = reply.Read<AgentView>();
        return Result($"The app now shows {view.CameraName} at {(view.Time is { } t ? Format(t, seconds: true) : "?")}.", view);
    }

    // --- helpers ---------------------------------------------------------------------------------------

    private const string NoFfmpeg =
        "FFmpeg isn't available to this server, so it can't decode video. Install FFmpeg (it must be on PATH), or start " +
        "the server with --ffmpeg <path to ffmpeg.exe> — for example the one in the app's ffmpeg folder.";

    private string? Ffmpeg() => FfmpegLocator.Find(options.FfmpegPath);

    private static List<string>? Kinds(RecordingType[]? kinds) =>
        kinds is { Length: > 0 } ? kinds.Distinct().Select(k => k.ToString().ToLowerInvariant()).ToList() : null;

    [GeneratedRegex(@"\d{4}-\d{1,2}-\d{1,2}")]
    private static partial Regex HasDate();

    /// <summary>Parses a time argument: ISO 8601 or "yyyy-MM-dd HH:mm[:ss]"; without an offset it is local time.</summary>
    internal static bool TryTime(string? text, string name, out DateTimeOffset? value, out string? error, bool required = false)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (required)
            {
                error = $"'{name}' is required (a date and time, e.g. 2026-07-02T08:15:00).";
                return false;
            }

            return true;
        }

        if (text.Length > 64 || !HasDate().IsMatch(text) ||
            !DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            error = $"'{name}' ({(text.Length > 64 ? text[..64] + "…" : text)}) isn't a date and time. Use e.g. 2026-07-02T08:15:00 (local time) or add an offset like +02:00.";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Evenly spaced times across the covered spans (merged), sampled at the middle of each share.</summary>
    internal static List<DateTimeOffset> Sample(IReadOnlyList<AgentSpan> spans, int count)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, span.End > merged[^1].End ? span.End : merged[^1].End);
            }
            else
            {
                merged.Add((span.Start, span.End));
            }
        }

        var total = merged.Sum(m => (m.End - m.Start).TotalSeconds);
        var times = new List<DateTimeOffset>(count);
        for (var i = 0; i < count; i++)
        {
            var target = total * (i + 0.5) / count;
            foreach (var (start, end) in merged)
            {
                var length = (end - start).TotalSeconds;
                if (target <= length)
                {
                    times.Add(start.AddSeconds(target));
                    break;
                }

                target -= length;
            }
        }

        return times;
    }

    private static string Gap(AgentMediaPoint point)
    {
        var text = new StringBuilder($"No footage at {Format(point.Requested, seconds: true)}.");
        if (point.PreviousEnd is { } before)
        {
            text.Append($" The previous recording ends at {Format(before, seconds: true)}.");
        }

        if (point.NextStart is { } after)
        {
            text.Append($" The next one starts at {Format(after, seconds: true)}.");
        }

        return text.ToString();
    }

    // Files one export_clip call may write (each overlapping recording is one).
    private const int MaxClipFiles = 8;

    private static GuardedOutputFile CreateTarget(GuardedFolder folder, string baseName, string extension)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? $"{baseName}.{extension}" : $"{baseName} ({n}).{extension}";
            try
            {
                return folder.CreateExclusive(name);
            }
            catch (GuardedFileException ex) when (ex.IsAlreadyExists && n < 1000)
            {
            }
        }
    }

    private static string Safe(string text)
    {
        var cleaned = new string(text.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length > 0 ? cleaned[..Math.Min(32, cleaned.Length)] : "camera";
    }

    internal static string Format(DateTimeOffset time, bool seconds = false, bool dateless = false)
    {
        var local = time.ToLocalTime();
        var clock = local.Millisecond != 0 && seconds ? "HH:mm:ss.f" : seconds ? "HH:mm:ss" : "HH:mm";
        return local.ToString(dateless ? clock : "yyyy-MM-dd " + clock, CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1)));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds:00}s" : $"{span.Seconds}s";
    }

    /// <summary>
    /// A successful result: <paramref name="data"/> as structured content (matching the tool's output schema), a
    /// text block with the summary — plus, unless the summary already says everything, the same data as JSON for
    /// clients that don't read structured content — and any extra content (images, resource links).
    /// </summary>
    private static CallToolResult Result<T>(string summary, T data, ContentBlock[]? extra = null, bool includeJson = true)
    {
        var structured = JsonSerializer.SerializeToElement(data, StructuredJson);
        var content = new List<ContentBlock>
        {
            new TextContentBlock { Text = includeJson ? summary + "\n" + JsonSerializer.Serialize(data, AgentJson.Pretty) : summary },
        };
        content.AddRange(extra ?? []);
        return new CallToolResult { Content = content, StructuredContent = structured };
    }

    private static CallToolResult Result<T>(string summary, T data, ContentBlock extra, bool includeJson = true) =>
        Result(summary, data, [extra], includeJson);

    /// <summary>Structured content is serialized with the SDK's own options (so property names match the output
    /// schema it generated), keeping nulls so every property the schema lists is present.</summary>
    private static readonly JsonSerializerOptions StructuredJson = new(McpJsonUtilities.DefaultOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static CallToolResult Error(string? message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message ?? "The request failed." }],
    };

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread (no SynchronizationContext hop).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
