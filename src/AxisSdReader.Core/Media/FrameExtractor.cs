using System.Globalization;
using System.Text;

namespace AxisSdReader.Core.Media;

public enum FrameFormat
{
    Jpeg,
    Png,
}

/// <summary>One cell of a contact sheet: footage at an offset within a chunk URL, or a placeholder.</summary>
/// <param name="Url">The chunk to decode; null renders a "no footage" placeholder cell.</param>
/// <param name="Offset">Position within that chunk.</param>
/// <param name="Label">Caption burned into the cell (timestamp etc.).</param>
public sealed record SheetCell(Uri? Url, TimeSpan Offset, string Label);

/// <summary>
/// Decodes still frames from recordings with FFmpeg — single frames and labelled contact sheets — reading
/// the media over a loopback URL. Intended for an unelevated process (the MCP bridge): decoding untrusted
/// video is the riskiest thing done with card data, so it never happens with administrator rights.
/// </summary>
public static class FrameExtractor
{
    public const int MaxWidth = 1920;

    /// <summary>The tallest a frame image may be.</summary>
    public const int MaxHeight = 1920;

    /// <summary>The longest side a contact sheet may have: its cells shrink to fit.</summary>
    public const int MaxSheetSide = 2400;

    /// <summary>
    /// The first frame at or after <paramref name="offset"/> into the chunk, scaled down (never up) to at most
    /// <paramref name="maxWidth"/> pixels wide, optionally with a caption burned in at the bottom left.
    /// </summary>
    public static async Task<byte[]> ExtractFrameAsync(string ffmpeg, Uri url, TimeSpan offset, int maxWidth, FrameFormat format,
        string? label = null, CancellationToken cancellationToken = default)
    {
        RequireLeaseUrl(url);
        await Decoders.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ExtractAtAsync(ffmpeg, url, offset, maxWidth, format, label, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (offset > NearEnd)
        {
            // A seek into the last moments of a chunk can land after its final frame and decode nothing: take the
            // nearest frame just before instead (the caption says it is approximate).
            return await ExtractAtAsync(ffmpeg, url, offset - NearEnd, maxWidth, format,
                label is null ? null : "~" + label, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Decoders.Release();
        }
    }

    /// <summary>Decoders running at once in this process, whatever asks for them (frames, every contact sheet's cells):
    /// each decodes untrusted video, so their number - and with it the memory they can take - is bounded.</summary>
    private static readonly SemaphoreSlim Decoders = new(4, 4);

    /// <summary>Input options for decoding card video: two decoder threads, and frames no larger than 8K (a crafted
    /// stream can declare enormous ones; FFmpeg's default limit is effectively none).</summary>
    private static readonly string[] DecoderLimits = ["-threads", "2", "-max_pixels", "33177600"];

    private static readonly TimeSpan NearEnd = TimeSpan.FromSeconds(0.75);

    /// <summary>The longest a whole contact sheet may take; each FFmpeg run has its own, shorter limits too.</summary>
    private static readonly TimeSpan SheetDeadline = TimeSpan.FromMinutes(3);

    /// <summary>FFmpeg is only ever pointed at a media URL from our own loopback server.</summary>
    private static void RequireLeaseUrl(Uri url)
    {
        if (!LocalMediaServer.IsLeaseUrl(url))
        {
            throw new ArgumentException("Frames can only be read from the app's own loopback media URLs.", nameof(url));
        }
    }

    private static async Task<byte[]> ExtractAtAsync(string ffmpeg, Uri url, TimeSpan offset, int maxWidth, FrameFormat format,
        string? label, CancellationToken cancellationToken)
    {
        // Scaled down to fit both ways: a crafted (or portrait) frame can be far taller than it is wide, and an image much
        // taller than a few thousand pixels is no use to a model (some refuse it).
        maxWidth = Math.Clamp(maxWidth, 64, MaxWidth);
        var filter = new StringBuilder(
            $"scale=w='min({maxWidth},iw)':h='min({MaxHeight},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2");
        if (label is { Length: > 0 } && DrawText(label, 20) is { } text)
        {
            filter.Append(',').Append(text);
        }

        List<string> args =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-protocol_whitelist", "http,tcp", "-format_whitelist", "matroska",
            .. DecoderLimits,
            "-ss", Sec(offset), "-i", url.AbsoluteUri,
            "-frames:v", "1", "-an", "-sn", "-dn", "-vf", filter.ToString(),
        ];
        args.AddRange(Encoder(format));
        args.Add("pipe:1");

        return await RunForImageAsync(ffmpeg, args, format, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A grid of labelled frames (one per cell, in order, filled left to right, top to bottom) as one JPEG.
    /// Cells are letterboxed to a common 16:9 size so mixed resolutions tile cleanly.
    /// </summary>
    /// <param name="cellDone">Reported once per decoded cell (from any thread), for progress.</param>
    public static async Task<byte[]> ContactSheetAsync(string ffmpeg, IReadOnlyList<SheetCell> cells, int columns, int cellWidth,
        CancellationToken cancellationToken = default, IProgress<int>? cellDone = null)
    {
        if (cells.Count == 0)
        {
            throw new ArgumentException("No cells.", nameof(cells));
        }

        foreach (var cell in cells)
        {
            if (cell.Url is { } url)
            {
                RequireLeaseUrl(url);
            }
        }

        columns = Math.Clamp(columns, 1, Math.Min(8, cells.Count));
        var rows = (cells.Count + columns - 1) / columns;

        // Cells shrink until the sheet fits MaxSheetSide both ways (4-pixel padding between cells, 4 around them); at
        // the smallest cell, 96 x 54, even a single column of 36 fits.
        var widest = (MaxSheetSide - 8) / columns - 4;
        var tallest = (MaxSheetSide - 8) / rows - 4;
        cellWidth = Math.Clamp(Math.Min(Math.Min(cellWidth, widest), tallest * 16 / 9) / 2 * 2, 96, 960);
        var cellHeight = cellWidth * 9 / 16 / 2 * 2;

        // Decode cells a few at a time: each is its own short FFmpeg run with a seek into its chunk. The whole
        // sheet has a deadline too, so damaged footage can't keep a request busy for long.
        var frames = new byte[cells.Count][];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SheetDeadline);
        var tasks = cells.Select(async (cell, i) =>
        {
            await Decoders.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                frames[i] = await RenderCellAsync(ffmpeg, cell, cellWidth, cellHeight, retried: false, deadline.Token).ConfigureAwait(false);
                cellDone?.Report(i);
            }
            finally
            {
                Decoders.Release();
            }
        });
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Decoding the contact sheet took too long; ask for fewer cells or a shorter range.");
        }

        // Tile: feed the cell JPEGs as an MJPEG stream and let the tile filter lay them out.
        using var concatenated = new MemoryStream();
        foreach (var frame in frames)
        {
            concatenated.Write(frame);
        }

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-threads", "2",
            "-f", "image2pipe", "-c:v", "mjpeg", "-i", "pipe:0",
            "-vf", $"tile={columns}x{rows}:padding=4:margin=4:color=0x0b0e13",
            "-frames:v", "1", "-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "3", "pipe:1",
        };
        var result = await FfmpegRunner.RunAsync(ffmpeg, args, new FfmpegRunOptions
        {
            StandardInput = concatenated.ToArray(),
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromSeconds(60),
        }, cancellationToken).ConfigureAwait(false);

        return CheckImage(result, FrameFormat.Jpeg, "contact sheet");
    }

    private static async Task<byte[]> RenderCellAsync(string ffmpeg, SheetCell cell, int width, int height, bool retried,
        CancellationToken cancellationToken)
    {
        // Every cell ends in the same pixel format: the tile step decodes the cells as one MJPEG stream, and a
        // cell whose format differs (a placeholder vs. 4:4:4 video, say) makes FFmpeg restart the filter mid-sheet,
        // silently dropping the cells before it.
        const string Uniform = "format=yuvj420p";
        var fit = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=0x101418";
        var caption = DrawText(cell.Label, Math.Clamp(height / 13, 11, 22));
        var vf = (caption is null ? fit : fit + "," + caption) + "," + Uniform;

        List<string> args;
        if (cell.Url is null)
        {
            // Placeholder: a dark cell saying there is no footage at that time (no untrusted input involved).
            var note = DrawText("no footage", Math.Clamp(height / 10, 12, 28), center: true);
            args =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-f", "lavfi", "-i", $"color=c=0x101418:s={width}x{height}:d=1",
                "-frames:v", "1", "-vf", (note is null ? (caption ?? "null") : (caption is null ? note : note + "," + caption)) + "," + Uniform,
            ];
        }
        else
        {
            args =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-protocol_whitelist", "http,tcp", "-format_whitelist", "matroska",
                .. DecoderLimits,
                "-ss", Sec(cell.Offset), "-i", cell.Url.AbsoluteUri,
                "-frames:v", "1", "-an", "-sn", "-dn", "-vf", vf,
            ];
        }

        args.AddRange(["-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "4", "pipe:1"]);
        try
        {
            return await RunForImageAsync(ffmpeg, args, FrameFormat.Jpeg, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (cell.Url is not null && cell.Offset > NearEnd && !retried)
        {
            // Nothing decoded in the chunk's last moments: use the frame just before (caption marked approximate).
            // Once only — a chunk that decodes nothing there either is treated as unreadable below.
            return await RenderCellAsync(ffmpeg, cell with { Offset = cell.Offset - NearEnd, Label = "~" + cell.Label }, width, height,
                retried: true, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) when (cell.Url is not null)
        {
            // Undecodable at that spot (e.g. a damaged chunk): keep the sheet, mark the cell.
            return await RenderCellAsync(ffmpeg, cell with { Url = null, Label = cell.Label + " (unreadable)" }, width, height,
                retried: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> RunForImageAsync(string ffmpeg, IReadOnlyList<string> args, FrameFormat format, CancellationToken cancellationToken)
    {
        var result = await FfmpegRunner.RunAsync(ffmpeg, args, new FfmpegRunOptions
        {
            CaptureStandardOutput = true,
            Timeout = TimeSpan.FromSeconds(45),
            StallTimeout = TimeSpan.FromSeconds(20),
        }, cancellationToken).ConfigureAwait(false);

        return CheckImage(result, format, "frame");
    }

    private static byte[] CheckImage(FfmpegRunResult result, FrameFormat format, string what)
    {
        var data = result.StandardOutput;
        var valid = format == FrameFormat.Png
            ? data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G'
            : data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8;
        if (result.Succeeded && valid)
        {
            return data;
        }

        var detail = result.TimedOut
            ? "timed out"
            : Agent.AgentService.Clean(LocalMediaServer.RedactTokens(result.ErrorTail).Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(), 200);
        throw new IOException($"Could not decode a {what}{(detail is { Length: > 0 } ? ": " + detail : ".")}");
    }

    private static string[] Encoder(FrameFormat format) => format == FrameFormat.Png
        ? ["-f", "image2pipe", "-c:v", "png"]
        : ["-f", "image2pipe", "-c:v", "mjpeg", "-q:v", "3"];

    /// <summary>A drawtext filter for a caption, or null when no usable font is installed. The text is
    /// restricted to a safe character set and escaped for the filtergraph.</summary>
    private static string? DrawText(string label, int fontSize, bool center = false)
    {
        var font = FontPath.Value;
        if (font is null)
        {
            return null;
        }

        var safe = new StringBuilder();
        foreach (var c in label)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '.' or '/' or '(' or ')' or '+' or '~')
            {
                safe.Append(c);
            }
            else if (c == ':')
            {
                safe.Append(@"\:");
            }
        }

        if (safe.Length == 0)
        {
            return null;
        }

        var position = center ? "x=(w-tw)/2:y=(h-th)/2" : "x=8:y=h-th-8";
        return $"drawtext=fontfile='{font}':text='{safe}':{position}:fontsize={fontSize}:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=4";
    }

    private static readonly Lazy<string?> FontPath = new(() =>
    {
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (var name in new[] { "consola.ttf", "arial.ttf", "segoeui.ttf", "DejaVuSansMono.ttf" })
        {
            var path = Path.Combine(fonts, name);
            if (File.Exists(path))
            {
                // filtergraph escaping: forward slashes, and the drive colon escaped
                return path.Replace('\\', '/').Replace(":", @"\:");
            }
        }

        return null;
    });

    private static string Sec(TimeSpan t) => Math.Max(0, t.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
}
