using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Editing;

/// <summary>Which series choice to ask about: the same two fields a <see cref="EditSessionChangeKinds.VideoSeries"/> change carries.</summary>
public sealed class EditorVideoSeriesPlacementRequestDto
{
    /// <summary>The series or section the video would join; null for a new series or a video on its own.</summary>
    [JsonPropertyName("target_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetId { get; init; }

    /// <summary>An <see cref="EditSessionVideo"/> token: a new series, or "on its own".</summary>
    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; init; }
}

/// <summary>
/// What choosing a series would do to the video's file (universal editor v6, D24). A read: nothing is moved. In a Managed folder the file
/// follows its series into the series' folder; anywhere else the file stays and the choice lives in Library only.
/// </summary>
public sealed class EditorVideoSeriesPlacementDto
{
    /// <summary>True when the file sits in a Managed folder and will move to <see cref="ToPath"/> when the change is saved.</summary>
    [JsonPropertyName("managed")]
    public bool Managed { get; init; }

    [JsonPropertyName("from_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromPath { get; init; }

    [JsonPropertyName("to_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToPath { get; init; }

    /// <summary>Why the file will stay where it is (a Read-only folder, no single folder for that series); null when it moves or needs no move.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>
    /// True when <see cref="Reason"/> stops the change from being saved at all (a name taken in the series' folder, a name that cannot be a
    /// folder name). Otherwise the reason only explains why the file stays where it is and the series choice can still be saved.
    /// </summary>
    [JsonPropertyName("blocked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Blocked { get; init; }
}
