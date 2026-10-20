using System.Collections.Concurrent;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Events;

namespace MediaEngine.Api.Realtime;

public sealed record IntercomAudienceConnection(string ConnectionId, Guid SessionId, Guid AccountId);

public sealed class IntercomAudienceRegistry
{
    private readonly ConcurrentDictionary<string, IntercomAudienceConnection> _connections = new(StringComparer.Ordinal);

    public void Add(IntercomAudienceConnection connection) => _connections[connection.ConnectionId] = connection;
    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);
    public IReadOnlyList<IntercomAudienceConnection> Snapshot() => _connections.Values.ToArray();
}

public interface IIntercomAudienceAuthorizer
{
    ValueTask<bool> CanReceiveAsync<T>(IntercomAudienceConnection connection, string eventName, T payload,
        CancellationToken ct = default) where T : notnull;
}

public sealed class IntercomAudienceAuthorizer(
    IIdentityRepository identities,
    IAccountRepository accounts,
    IApplicationEventResourceResolver resources,
    IProfileRepository profiles,
    TimeProvider timeProvider) : IIntercomAudienceAuthorizer
{
    public async ValueTask<bool> CanReceiveAsync<T>(
        IntercomAudienceConnection connection,
        string eventName,
        T payload,
        CancellationToken ct = default) where T : notnull
    {
        var session = await identities.GetSessionByIdAsync(connection.SessionId, ct).ConfigureAwait(false);
        if (session is null || session.AccountId != connection.AccountId || !session.IsActive(timeProvider.GetUtcNow()))
        {
            return false;
        }

        var accountTask = accounts.GetByIdAsync(connection.AccountId, ct);
        var grantTask = accounts.GetGrantAsync(connection.AccountId, session.ActiveProfileId, ct);
        await Task.WhenAll(accountTask, grantTask).ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        var grant = await grantTask.ConfigureAwait(false);
        if (account?.IsEnabled != true || grant?.IsEnabled != true)
        {
            return false;
        }

        // Titles deleted from the library carry only ids of things that no longer exist, so there is nothing to protect:
        // every signed-in person's open screens must drop them, not just administrators'.
        if (eventName == SignalREvents.LibraryItemsRemoved)
        {
            return true;
        }

        // A child (restricted) profile never gets administrator authority, as on Engine requests.
        var activeProfile = await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false);
        var administrator = account.IsAdministrator && grant.AdminEnabled &&
            activeProfile?.Role != ProfileRole.RestrictedProfile;

        var assetId = AssetId(eventName, payload);
        if (assetId is { } id)
        {
            if (administrator)
            {
                return true;
            }

            var provenance = await resources.ResolveAsync(id, ct).ConfigureAwait(false);
            return provenance is not null &&
                await accounts.HasLibraryGrantAsync(account.Id, provenance.LibraryId, ct).ConfigureAwait(false) &&
                await accounts.HasFeatureGrantAsync(account.Id, provenance.FeatureId, ct).ConfigureAwait(false);
        }

        if (!administrator)
        {
            return false;
        }

        var protection = await accounts.GetAdminProtectionAsync(account.Id, grant.ProfileId, ct).ConfigureAwait(false);
        if (protection?.IsEnabled != true)
        {
            return true;
        }

        var unlock = await accounts.GetAdminUnlockAsync(
            session.Id, account.Id, grant.ProfileId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return unlock is not null && unlock.ProtectionVersion == protection.ProtectionVersion;
    }

    private static Guid? AssetId<T>(string eventName, T payload) where T : notnull => (eventName, payload) switch
    {
        (SignalREvents.MediaAdded, MediaAddedEvent value) => value.AssetId ?? value.WorkId,
        (SignalREvents.MediaRemoved, MediaRemovedEvent value) => value.AssetId,
        (SignalREvents.MetadataHarvested, MetadataHarvestedEvent value) => value.EntityId,
        (SignalREvents.HydrationStageCompleted, HydrationStageCompletedEvent value) => value.EntityId,
        _ => null,
    };
}
