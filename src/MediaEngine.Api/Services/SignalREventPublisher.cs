using MediaEngine.Api.Realtime;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace MediaEngine.Api.Services;

/// <summary>
/// Relay service that implements <see cref="IEventPublisher"/> by forwarding
/// authorized events to current Dashboard recipients via
/// <see cref="Intercom"/>.
///
/// The event name becomes the SignalR method name; the payload is serialised
/// to JSON by the default System.Text.Json collection protocol. Blazor clients subscribe
/// using <c>hubConnection.On("IngestionStarted", handler)</c>.
///
/// Safe to use as a Singleton — <see cref="IHubContext{T}"/> is thread-safe
/// and designed for long-lived singleton injection.
/// </summary>
public sealed class SignalREventPublisher : IEventPublisher
{
    private readonly IHubContext<Intercom> _collection;
    private readonly ApplicationEventProjectionPublisher? _external;
    private readonly IntercomAudienceRegistry? _audiences;
    private readonly IIntercomAudienceAuthorizer? _authorization;
    private readonly ILogger<SignalREventPublisher>? _logger;
    private readonly IActivityRunRecorder? _runs;

    public SignalREventPublisher(
        IHubContext<Intercom> collection,
        IntercomAudienceRegistry? audiences = null,
        IIntercomAudienceAuthorizer? authorization = null,
        ApplicationEventProjectionPublisher? external = null,
        ILogger<SignalREventPublisher>? logger = null,
        IActivityRunRecorder? runs = null)
    {
        _runs = runs;
        ArgumentNullException.ThrowIfNull(collection);
        _collection = collection;
        _audiences = audiences;
        _authorization = authorization;
        _external = external;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task PublishAsync<TPayload>(
        string eventName,
        TPayload payload,
        CancellationToken ct = default)
        where TPayload : notnull
    {
        // Persist the external fact before attempting delivery to an ephemeral Dashboard circuit.
        if (_external is not null)
        {
            await _external.ProjectAsync(eventName, payload, ct).ConfigureAwait(false);
        }

        await RecordRunAsync(payload, ct).ConfigureAwait(false);

        foreach (var connection in _audiences?.Snapshot() ?? [])
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (_authorization is not null && await _authorization.CanReceiveAsync(connection, eventName, payload, timeout.Token).ConfigureAwait(false))
                {
                    await _collection.Clients.Client(connection.ConnectionId).SendAsync(eventName, payload, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Dashboard event delivery failed for {EventName} on {ConnectionId}.",
                    eventName, connection.ConnectionId);
            }
        }
    }

    // Activity runs (spec library-activity D21): a removal joins the library's open "removed" run, and when the ingestion queue returns to
    // idle the runs that were being filled close after a short quiet minute instead of waiting out the full five. Never throws.
    private async Task RecordRunAsync<TPayload>(TPayload payload, CancellationToken ct) where TPayload : notnull
    {
        if (_runs is null)
        {
            return;
        }

        try
        {
            switch (payload)
            {
                case MediaRemovedEvent removed:
                    await _runs.RecordRemovedAsync(removed.AssetId, removed.LibraryId, removed.FilePath, ct).ConfigureAwait(false);
                    break;
                case IngestionQueueProgressDto { State: "idle" }:
                    await _runs.CloseQuietRunsAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "The activity run could not be recorded for {PayloadType}.", typeof(TPayload).Name);
        }
    }
}
