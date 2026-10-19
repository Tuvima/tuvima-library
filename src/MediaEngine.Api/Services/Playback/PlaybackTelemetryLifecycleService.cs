using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class PlaybackTelemetryLifecycleService(
    IHttpContextAccessor http,
    PlayerCatalogueScope catalogue,
    IPlaybackTelemetryRepository repository,
    IClientAuthorizationRepository clients,
    IIdentityRepository identities,
    PersonalActivityRecorder activity,
    TimeProvider clock)
{
    public async Task<PlaybackTelemetryTransition?> CloseAsync(
        Guid playerSessionId,
        string reason,
        CancellationToken ct)
    {
        var transition = await repository.CloseAsync(playerSessionId, clock.GetUtcNow(), reason, ct).ConfigureAwait(false);
        if (transition is not null)
        {
            await activity.RecordClosedAsync([transition], ct).ConfigureAwait(false);
        }

        return transition;
    }

    public async Task<IReadOnlyList<PlaybackTelemetryTransition>> ObserveAsync(
        Guid playerSessionId,
        Guid assetId,
        string state,
        double positionSeconds,
        double? durationSeconds,
        long? sequence,
        double playbackRate,
        bool isExplicitSeek,
        bool hasPlaybackEnded,
        string? completionReason,
        string? client,
        PlaybackConnectionContextDto? connection,
        CancellationToken ct)
    {
        if (http.HttpContext is not { } context)
        {
            throw new PlayerResourceDeniedException();
        }

        var authority = await context.RequestServices.GetRequiredService<IRequestAuthorityResolver>()
            .ResolveAsync(context, ct).ConfigureAwait(false);
        if (AuthorityValidity.ValidateHuman(authority) is not null ||
            authority.AccountId is not { } accountId || authority.ActiveProfileId is not { } profileId)
        {
            throw new PlayerResourceDeniedException();
        }

        var asset = await catalogue.RequireAssetContextAsync(assetId, ct).ConfigureAwait(false);
        if (!Guid.TryParse(asset.LibraryId, out var libraryId) || libraryId == Guid.Empty)
        {
            throw new PlayerResourceDeniedException();
        }

        var feature = FeatureFor(asset.MediaType);
        if (feature == default)
        {
            throw new PlayerResourceDeniedException();
        }

        var clientFacts = await ClientFactsAsync(authority, client, ct).ConfigureAwait(false);
        // The current player heartbeat does not carry a server-attested record of
        // the transport that was actually selected. A manifest recommendation or
        // source probe is not an observed delivery fact, so these values remain
        // explicitly unknown until the stream host supplies trusted telemetry.
        var delivery = new PlaybackDeliveryFacts(
            PlaybackTelemetryDeliveryModes.Unknown,
            ConnectionType: PlaybackConnectionTypes.Unknown);
        var observation = new PlaybackTelemetryObservation(
            playerSessionId, accountId, profileId, authority.ApplicationId, clientFacts.DeviceId,
            authority.SessionId, assetId, libraryId, feature, asset.MediaType, state,
            clock.GetUtcNow(), positionSeconds, durationSeconds, sequence, playbackRate,
            isExplicitSeek, hasPlaybackEnded, completionReason, delivery, clientFacts.Facts);
        var transitions = await repository.ObserveAsync(observation, ct).ConfigureAwait(false);
        // The person's own activity record follows the session; it is best effort and never fails playback.
        await activity.RecordPlaybackAsync(transitions, asset, clientFacts.Device, ct).ConfigureAwait(false);
        return transitions;
    }

    private async Task<(PlaybackClientFacts Facts, Guid? DeviceId, PersonalActivityDevice Device)> ClientFactsAsync(
        RequestAuthority authority,
        string? fallbackClient,
        CancellationToken ct)
    {
        if (authority.DeviceId is { } deviceId)
        {
            var device = await clients.GetDeviceAsync(deviceId, ct).ConfigureAwait(false);
            if (device is not null && device.AccountId == authority.AccountId &&
                device.ProfileId == authority.ActiveProfileId && device.ApplicationId == authority.ApplicationId)
            {
                return (new(device.ClientName, device.ClientVersion), device.Id,
                    new PersonalActivityDevice(device.DeviceName, device.DeviceClass));
            }
        }
        if (authority.SessionId is { } sessionId)
        {
            var session = await identities.GetSessionByIdAsync(sessionId, ct).ConfigureAwait(false);
            if (session is not null && session.AccountId == authority.AccountId && session.ActiveProfileId == authority.ActiveProfileId)
            {
                return (new(session.Client, null), null, new PersonalActivityDevice(session.Client, "web"));
            }
        }
        var fallback = string.IsNullOrWhiteSpace(fallbackClient) ? null : fallbackClient;
        return (new(fallback, null), null, new PersonalActivityDevice(fallback, null));
    }

    private static AccountFeatureId FeatureFor(string mediaType) =>
        DisplayMediaRules.IsReadKind(mediaType) ? AccountFeatureId.Read :
        DisplayMediaRules.IsWatchKind(mediaType) ? AccountFeatureId.Watch :
        DisplayMediaRules.IsListenKind(mediaType) ? AccountFeatureId.Listen :
        default;
}
