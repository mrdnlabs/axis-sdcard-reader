using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxisSdReader.Core.Agent;

/// <summary>
/// The request an agent bridge sends (over the app's named pipe, or in-process for a card image). One flat
/// shape for every operation; each operation reads the fields it needs and validates them. Times are
/// absolute instants (ISO 8601 with offset on the wire).
/// </summary>
public sealed class AgentRequest
{
    /// <summary>status · cameras · recordings · coverage · locate · slices · view · show</summary>
    public string Op { get; set; } = "";

    public string? Camera { get; set; }

    public string? Source { get; set; }

    public string? RecordingId { get; set; }

    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? To { get; set; }

    public DateTimeOffset? Time { get; set; }

    public List<DateTimeOffset>? Times { get; set; }

    /// <summary>continuous · motion · manual · scheduled · other</summary>
    public List<string>? Kinds { get; set; }

    public int? Offset { get; set; }

    public int? Limit { get; set; }

    public DateTimeOffset? SelectFrom { get; set; }

    public DateTimeOffset? SelectTo { get; set; }
}

/// <summary>The reply: <see cref="Result"/> on success (shape depends on the operation), else <see cref="Error"/>.</summary>
public sealed class AgentResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public JsonElement? Result { get; set; }

    public static AgentResponse Success<T>(T result) =>
        new() { Ok = true, Result = JsonSerializer.SerializeToElement(result, AgentJson.Options) };

    public static AgentResponse Failure(string error) => new() { Ok = false, Error = error };

    public T Read<T>() => Ok && Result is { } result
        ? result.Deserialize<T>(AgentJson.Options) ?? throw new InvalidDataException("Empty result.")
        : throw new AgentException(Error ?? "The request failed.");
}

/// <summary>An agent request that failed with a message meant for the agent (and the user).</summary>
public sealed class AgentException(string message) : Exception(message);

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    /// <summary>Indented output for showing results to a model.</summary>
    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}

public sealed record AgentCardInfo(
    string Name,
    string Medium,
    bool Encrypted,
    bool Protected,
    string Protection,
    int Recordings,
    int Cameras,
    DateTimeOffset? FirstRecording,
    DateTimeOffset? LastRecording,
    int UnreadableEntries);

public sealed record AgentStatus(
    string Mode,
    bool CardOpen,
    AgentCardInfo? Card,
    AgentView? View,
    string? Message);

public sealed record AgentSource(string Source, int Recordings, string? Resolution, string? Codec);

public sealed record AgentCamera(
    string Id,
    string Name,
    string? Model,
    IReadOnlyList<AgentSource> Sources,
    int Recordings,
    DateTimeOffset? First,
    DateTimeOffset? Last);

public sealed record AgentRecording(
    string Id,
    string Camera,
    string Source,
    string Kind,
    string? Trigger,
    DateTimeOffset Start,
    DateTimeOffset End,
    double DurationSeconds,
    bool Exact,
    int Chunks,
    long Bytes,
    bool Interrupted,
    string? Codec,
    string? Resolution);

public sealed record AgentRecordingPage(int Total, int Offset, IReadOnlyList<AgentRecording> Items);

public sealed record AgentSpan(string Kind, DateTimeOffset Start, DateTimeOffset End, double Seconds);

public sealed record AgentCoverage(
    string Camera,
    string Source,
    DateTimeOffset From,
    DateTimeOffset To,
    double CoveredSeconds,
    IReadOnlyList<AgentSpan> Spans,
    IReadOnlyList<AgentSpan> Gaps,
    bool Exact,
    bool Truncated);

/// <summary>What plays at one requested moment — and, when footage exists, where to fetch it.</summary>
public sealed record AgentMediaPoint(
    DateTimeOffset Requested,
    bool Found,
    string? RecordingId,
    string? Kind,
    string? Url,
    double OffsetSeconds,
    DateTimeOffset? PreviousEnd,
    DateTimeOffset? NextStart);

/// <summary>One recording's part of a time range: its chunk URLs in order plus trims from the first chunk.</summary>
public sealed record AgentSlice(
    string RecordingId,
    string Kind,
    DateTimeOffset Start,
    DateTimeOffset End,
    double Seconds,
    IReadOnlyList<string> ChunkUrls,
    double TrimStartSeconds,
    double TrimEndSeconds,
    string? Codec);

/// <summary>What the app is showing right now (app mode only).</summary>
public sealed record AgentView(
    string? Camera,
    string? CameraName,
    string? Source,
    DateTimeOffset? Time,
    bool Playing,
    string? RecordingId,
    string? Kind,
    DateTimeOffset? SelectionFrom,
    DateTimeOffset? SelectionTo);
