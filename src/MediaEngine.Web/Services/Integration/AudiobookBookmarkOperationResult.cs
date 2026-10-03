using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

public enum AudiobookBookmarkFailureKind
{
    None,
    Rejected,
    NotFound,
    TransportUnknown,
}

public enum AudiobookBookmarkOperationOutcome
{
    Success,
    DefiniteFailure,
    Unknown,
}

/// <summary>A per-call result; callers never infer an operation result from shared LastError state.</summary>
public sealed record AudiobookBookmarkOperationResult<T>(
    AudiobookBookmarkOperationOutcome Outcome,
    T? Value = default,
    string? Message = null,
    System.Net.HttpStatusCode? StatusCode = null,
    AudiobookBookmarkFailureKind FailureKind = AudiobookBookmarkFailureKind.None)
{
    public static AudiobookBookmarkOperationResult<T> Succeeded(T value) =>
        new(AudiobookBookmarkOperationOutcome.Success, value);

    public static AudiobookBookmarkOperationResult<T> Failed(string message,
        System.Net.HttpStatusCode? statusCode = null,
        AudiobookBookmarkFailureKind failureKind = AudiobookBookmarkFailureKind.Rejected) =>
        new(AudiobookBookmarkOperationOutcome.DefiniteFailure, default, message, statusCode, failureKind);

    public static AudiobookBookmarkOperationResult<T> Unknown(string message, System.Net.HttpStatusCode? statusCode = null) =>
        new(AudiobookBookmarkOperationOutcome.Unknown, default, message, statusCode, AudiobookBookmarkFailureKind.TransportUnknown);
}

public sealed record AudiobookBookmarkReplayResult(
    AudiobookBookmarkOperationOutcome Outcome,
    AudiobookBookmarkDto? Bookmark = null,
    string? Message = null);
