using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// What asking the Engine for a cast link came to (unified player v2 D28): the session when the Engine made one, or the reason it did not.
/// </summary>
/// <param name="Session">The Engine's answer, or null when the call failed.</param>
/// <param name="StatusCode">The HTTP status, or null when the call never got an answer.</param>
/// <param name="Reason">A short reason for the log: the Engine's message or reason phrase, or the kind of error.</param>
public sealed record CastSessionAttempt(CastSessionDto? Session, int? StatusCode, string? Reason)
{
    /// <summary>True when the Engine made a session a receiver can start now.</summary>
    public bool IsReady => Session is { Status: CastSessionStatuses.Ready, MediaPath: { Length: > 0 } };
}
