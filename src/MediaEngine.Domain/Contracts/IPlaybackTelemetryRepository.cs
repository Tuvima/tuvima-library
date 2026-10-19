using MediaEngine.Domain.Playback;

namespace MediaEngine.Domain.Contracts;

public interface IPlaybackTelemetryRepository
{
    Task<IReadOnlyList<PlaybackTelemetryTransition>> ObserveAsync(
        PlaybackTelemetryObservation observation,
        CancellationToken ct = default);

    Task<PlaybackTelemetryTransition?> CloseAsync(
        Guid playerSessionId,
        DateTimeOffset endedAt,
        string reason,
        CancellationToken ct = default);

    Task<IReadOnlyList<PlaybackTelemetryTransition>> CloseStaleAsync(
        DateTimeOffset staleBefore,
        CancellationToken ct = default);

    Task<PlaybackTelemetryPage> GetActiveAsync(
        PlaybackTelemetryReadScope scope,
        int limit,
        string? cursor,
        CancellationToken ct = default);

    Task<PlaybackTelemetryPage> GetHistoryAsync(
        PlaybackTelemetryReadScope scope,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        CancellationToken ct = default);

    Task<PlaybackTelemetryAggregate> GetPlaybackAggregateAsync(
        PlaybackTelemetryReadScope scope,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default);

    Task<IReadOnlyList<PlaybackTelemetryGroupAggregate>> GetGroupAggregatesAsync(
        PlaybackTelemetryReadScope scope,
        string dimension,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct = default);
}
