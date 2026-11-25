using System.ComponentModel;
using AxisSdReader.Core.Agent;

namespace AxisSdReader.Mcp;

// Structured results (MCP `structuredContent`), one type per tool, each advertised to clients as the tool's
// `outputSchema`. The same data is also returned as JSON text, for clients that predate structured output.

[Description("What the server is connected to and what is open.")]
public sealed record StatusOutput(
    [property: Description("\"app\" (connected to the running app) or \"image\" (reading a card image directly).")] string Mode,
    [property: Description("Whether a card (or card image) is open.")] bool CardOpen,
    [property: Description("The open card, when there is one.")] AgentCardInfo? Card,
    [property: Description("What the app is showing (app mode only).")] AgentView? View,
    [property: Description("Why nothing is open, when that is the case.")] string? Message,
    [property: Description("Whether FFmpeg is available, which frames, contact sheets and clips need.")] bool FfmpegAvailable);

[Description("The cameras on the card.")]
public sealed record CamerasOutput(IReadOnlyList<AgentCamera> Cameras);

[Description("A decoded frame.")]
public sealed record FrameOutput(
    [property: Description("The requested moment.")] DateTimeOffset Time,
    [property: Description("The recording the frame came from.")] string? RecordingId,
    [property: Description("That recording's type: continuous, motion, manual, scheduled or other.")] string? Kind,
    [property: Description("MIME type of the image content.")] string MimeType);

[Description("One cell of a contact sheet, in reading order.")]
public sealed record SheetCellOutput(
    [property: Description("1-based position: left to right, top to bottom.")] int Index,
    DateTimeOffset Time,
    [property: Description("False when there was no footage at that moment (a placeholder cell).")] bool Found,
    string? RecordingId,
    string? Kind);

[Description("A contact sheet: evenly spaced frames across a period.")]
public sealed record ContactSheetOutput(DateTimeOffset From, DateTimeOffset To, int Columns, IReadOnlyList<SheetCellOutput> Cells);

[Description("One exported video file.")]
public sealed record ClipFileOutput(
    [property: Description("Local path of the file.")] string Path,
    [property: Description("file:// URI of the file.")] string Uri,
    string MimeType,
    long Bytes,
    string RecordingId,
    string Kind,
    DateTimeOffset Start,
    DateTimeOffset End);

[Description("The files an export produced (one per recording overlapping the range).")]
public sealed record ClipsOutput(IReadOnlyList<ClipFileOutput> Files);

[Description("What the app is showing.")]
public sealed record AppViewOutput(AgentView View, [property: Description("Whether the frame under the playhead is included as an image.")] bool FrameIncluded);
