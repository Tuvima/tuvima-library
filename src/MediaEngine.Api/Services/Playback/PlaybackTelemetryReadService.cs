using MediaEngine.Api.Security;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Playback;

namespace MediaEngine.Api.Services.Playback;

public sealed class PlaybackTelemetryReadService(
    IHttpContextAccessor http,
    IRequestAuthorityResolver authorities,
    IAuthorizationEvaluator evaluator,
    IProfileAccessRepository profileAccess,
    LibraryVisibilityRule libraryVisibility,
    IPlaybackTelemetryRepository repository)
{
    public async Task<PlaybackTelemetryPageDto?> GetActiveAsync(
        ApplicationPermissionId permission, int limit, string? cursor, CancellationToken ct)
    {
        var scope = await ScopeAsync(permission, ct).ConfigureAwait(false);
        if (!scope.IsAllowed)
        {
            return null;
        }

        return Map(await repository.GetActiveAsync(scope, limit, cursor, ct).ConfigureAwait(false));
    }

    public async Task<PlaybackTelemetryPageDto?> GetHistoryAsync(
        ApplicationPermissionId permission, DateTimeOffset? from, DateTimeOffset? to,
        int limit, string? cursor, CancellationToken ct)
    {
        var scope = await ScopeAsync(permission, ct).ConfigureAwait(false);
        if (!scope.IsAllowed)
        {
            return null;
        }

        return Map(await repository.GetHistoryAsync(scope, from, to, limit, cursor, ct).ConfigureAwait(false));
    }

    public async Task<PlaybackAnalyticsDto?> GetPlaybackAsync(
        ApplicationPermissionId permission, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var scope = await ScopeAsync(permission, ct).ConfigureAwait(false);
        if (!scope.IsAllowed)
        {
            return null;
        }

        var value = await repository.GetPlaybackAggregateAsync(scope, from, to, ct).ConfigureAwait(false);
        return new PlaybackAnalyticsDto
        {
            SessionCount = value.SessionCount,
            CompletedCount = value.CompletedCount,
            PlayedDurationSeconds = value.PlayedDurationSeconds,
            UniqueAccounts = value.UniqueAccounts,
            UniqueProfiles = value.UniqueProfiles,
            UniqueAssets = value.UniqueAssets,
            DirectPlayCount = value.DirectPlayCount,
            RemuxCount = value.RemuxCount,
            TranscodeCount = value.TranscodeCount,
        };
    }

    public async Task<PlaybackAnalyticsGroupsDto?> GetGroupsAsync(
        ApplicationPermissionId permission, string dimension, DateTimeOffset? from,
        DateTimeOffset? to, int limit, CancellationToken ct)
    {
        var scope = await ScopeAsync(permission, ct).ConfigureAwait(false);
        if (!scope.IsAllowed)
        {
            return null;
        }

        var values = await repository.GetGroupAggregatesAsync(scope, dimension, from, to, limit, ct).ConfigureAwait(false);
        return new(values.Select(value => new PlaybackAnalyticsGroupDto
        {
            AccountId = value.AccountId,
            ProfileId = value.ProfileId,
            LibraryId = value.LibraryId,
            DeviceId = value.DeviceId,
            SessionCount = value.SessionCount,
            CompletedCount = value.CompletedCount,
            PlayedDurationSeconds = value.PlayedDurationSeconds,
            LastPlayedAt = value.LastPlayedAt,
        }).ToList());
    }

    private async Task<PlaybackTelemetryReadScope> ScopeAsync(ApplicationPermissionId permission, CancellationToken ct)
    {
        if (http.HttpContext is not { } context)
        {
            return PlaybackTelemetryReadScope.Denied;
        }

        var authority = await authorities.ResolveAsync(context, ct).ConfigureAwait(false);
        if (authority.IsEffectiveAdministrator)
        {
            return new(true, true, null, null, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet());
        }

        if (authority.PrincipalKind is not (PrincipalKind.DelegatedUserClient or PrincipalKind.ServiceApplication))
        {
            return PlaybackTelemetryReadScope.Denied;
        }

        var decision = await evaluator.EvaluateAsync(authority,
            new AuthorizationRequirement(ApplicationPermission: permission), null, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return PlaybackTelemetryReadScope.Denied;
        }

        if (authority.PrincipalKind == PrincipalKind.ServiceApplication)
        {
            return new(true, true, null, null, true, new HashSet<Guid>(), AccountFeatureId.All.ToHashSet());
        }

        if (authority.AccountId is not { } accountId || authority.ActiveProfileId is not { } profileId)
        {
            return PlaybackTelemetryReadScope.Denied;
        }

        // Read access follows the profile being acted as.
        var features = await profileAccess.GetFeatureGrantsAsync(profileId, ct).ConfigureAwait(false);
        var hidden = await libraryVisibility.LoadHiddenAsync(profileId, ct).ConfigureAwait(false);
        return new(true, false, accountId, profileId, false, hidden, features);
    }

    private static PlaybackTelemetryPageDto Map(PlaybackTelemetryPage page) =>
        new(page.Items.Select(Map).ToList(), page.NextCursor);

    private static PlaybackTelemetrySessionDto Map(PlaybackTelemetrySession value) => new()
    {
        Id = value.Id,
        PlayerSessionId = value.PlayerSessionId,
        AccountId = value.AccountId,
        ProfileId = value.ProfileId,
        ApplicationId = value.ApplicationId,
        DeviceId = value.DeviceId,
        AssetId = value.AssetId,
        LibraryId = value.LibraryId,
        FeatureId = value.Feature.Value,
        MediaType = value.MediaType,
        State = value.State,
        StartedAt = value.StartedAt,
        LastObservedAt = value.LastObservedAt,
        EndedAt = value.EndedAt,
        StartedPositionSeconds = value.StartedPositionSeconds,
        LastPositionSeconds = value.LastPositionSeconds,
        DurationSeconds = value.DurationSeconds,
        PlayedDurationSeconds = value.PlayedDurationSeconds,
        CompletionReason = value.CompletionReason,
        DeliveryMode = value.Delivery.Mode,
        Container = value.Delivery.Container,
        VideoCodec = value.Delivery.VideoCodec,
        AudioCodec = value.Delivery.AudioCodec,
        Width = value.Delivery.Width,
        Height = value.Delivery.Height,
        BitrateKbps = value.Delivery.BitrateKbps,
        ConnectionType = value.Delivery.ConnectionType,
        ClientName = value.Client.Name,
        ClientVersion = value.Client.Version,
    };
}
