namespace AxisSdReader.Core.Axis.Matroska;

/// <summary>Metadata extracted from the headers of a Matroska (MKV) file.</summary>
/// <param name="DateUtc">Segment DateUTC (recording start of this file/chunk), if present.</param>
/// <param name="Duration">Segment duration. From the Info element when present; otherwise
/// derived by scanning cluster/block timestamps (lower bound: up to the start of the last frame).</param>
/// <param name="VideoCodecId">Matroska codec ID of the first video track, e.g. <c>V_MPEG4/ISO/AVC</c> (H.264) or <c>V_MPEGH/ISO/HEVC</c> (H.265).</param>
/// <param name="PixelWidth">Video width, if a video track was found.</param>
/// <param name="PixelHeight">Video height, if a video track was found.</param>
/// <param name="WritingApp">The application that wrote the file, as recorded in the segment info.</param>
/// <param name="IsTruncated">True when the file ends mid-element, which is normal for
/// recordings interrupted by power loss.</param>
public sealed record MkvMetadata(
    DateTime? DateUtc,
    TimeSpan? Duration,
    string? VideoCodecId,
    int? PixelWidth,
    int? PixelHeight,
    string? WritingApp,
    bool IsTruncated)
{
    public bool IsH264 => VideoCodecId == "V_MPEG4/ISO/AVC";
    public bool IsH265 => VideoCodecId == "V_MPEGH/ISO/HEVC";
    public bool IsAv1 => VideoCodecId == "V_AV1";
}
