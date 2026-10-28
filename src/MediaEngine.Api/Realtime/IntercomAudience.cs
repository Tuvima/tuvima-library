using System.Collections.Concurrent;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
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
    IProfileAccessRepository profileAccess,
    IApplicationEventResourceResolver resources,
    TimeProvider timeProvider,
    IVaultLibraryRepository vaultLibraries,
    INsfwAccessRepository nsfw,
    IVaultPathClassifier vaultPaths,
    LibraryVisibilityRule libraryVisibility) : IIntercomAudienceAuthorizer
{
    /// <summary>
    /// Events whose payload names a file, a folder or an item, so the audience decides by where it sits: an item or path in a Vault
    /// library reaches only an unlocked session. Every event in <see cref="SignalREvents"/> is either listed here or in
    /// <see cref="VaultFreeEvents"/> with the reason it cannot carry Vault content; a test fails for a new one that is neither.
    /// </summary>
    public static readonly IReadOnlySet<string> ClassifiedEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        SignalREvents.IngestionStarted,
        SignalREvents.IngestionHashed,
        SignalREvents.IngestionCompleted,
        SignalREvents.IngestionFailed,
        SignalREvents.IngestionItemProgress,
        SignalREvents.MediaAdded,
        SignalREvents.MediaRemoved,
        SignalREvents.MetadataHarvested,
        SignalREvents.HydrationStageCompleted,
        SignalREvents.ReviewItemCreated,
        SignalREvents.ReviewItemResolved,
        SignalREvents.FolderHealthChanged,
        SignalREvents.WatchFolderActive,
        SignalREvents.InitialSweepStarted,
        SignalREvents.LibraryItemsRemoved,
    };

    /// <summary>Events that cannot carry anything about a Vault item, and why. Counts and provider or model state name no item.</summary>
    public static readonly IReadOnlyDictionary<string, string> VaultFreeEvents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SignalREvents.IngestionQueueProgress] = "Totals and state only; no file or title. Vault files in the totals are an open item for the ingestion packets.",
        [SignalREvents.MediaOperationChanged] = "An operation kind, status and counts; no file or title.",
        [SignalREvents.CapabilityStateChanged] = "A capability kind and state by id; no file or title.",
        [SignalREvents.PersonEnriched] = "Shared People only; Vault performers live in their own records and are never published.",
        [SignalREvents.FictionalEntityEnriched] = "Universe lore only; a Vault work never joins a Universe.",
        [SignalREvents.ProviderStatusChanged] = "A provider's health.",
        [SignalREvents.ProviderRecoveryFlush] = "A provider's queued retries as a count.",
        [SignalREvents.ProviderActivity] = "Provider request counters.",
        [SignalREvents.ModelStateChanged] = "A Local AI model's state.",
        [SignalREvents.ModelDownloadProgress] = "A model download's progress.",
        [SignalREvents.UniverseEnrichmentProgress] = "Universe numbers; a Vault work never joins a Universe.",
        [SignalREvents.WritebackConfigChanged] = "A setting changed; no item.",
        [SignalREvents.RetagSweepProgress] = "Counts only.",
        [SignalREvents.RetagSweepCompleted] = "Counts only.",
        [SignalREvents.InitialSweepProgress] = "Counts only.",
        [SignalREvents.InitialSweepCompleted] = "Counts only.",
    };

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

        // Everything is read from the profile the session is acting as, and only while it is still in the user's own household.
        var accountTask = accounts.GetByIdAsync(connection.AccountId, ct);
        var profileTask = accounts.GetActiveProfileAccessAsync(connection.AccountId, session.ActiveProfileId, ct);
        await Task.WhenAll(accountTask, profileTask).ConfigureAwait(false);
        var account = await accountTask.ConfigureAwait(false);
        var profile = await profileTask.ConfigureAwait(false);
        if (account?.IsEnabled != true || profile is null)
        {
            return false;
        }

        // A child (restricted) profile never gets administrator authority, as on Engine requests.
        var administrator = new RequestAuthority(PrincipalKind.Human, true, account.Id, profile.ProfileId, AccountEnabled: true)
            .WithActiveProfile(profile).IsEffectiveAdministrator;

        var assetId = AssetId(eventName, payload);

        // The Vault reaches nobody who has not switched Vault mode on in this very session, administrators included (Videos spec D17).
        // Whether an event is about a Vault item is read from the item's library, never from what the event says about it, and an
        // event that names only a path is read from where the path sits.
        if (await IsAboutVaultAsync(payload, assetId, ct).ConfigureAwait(false))
        {
            return await nsfw.IsUnlockedAsync(session.Id, session.ActiveProfileId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false)
                && (await profileAccess.GetFeatureGrantsAsync(profile.ProfileId, ct).ConfigureAwait(false)).Contains(AccountFeatureId.Watch);
        }

        // Titles deleted from the library carry only ids of things that no longer exist, so every signed-in person's open screens
        // must drop them, not just administrators'. A deletion never includes a Vault item (its plan leaves them out), and an id
        // that can still be classified as Vault was judged above, so a locked session is never told about Vault items (review L13).
        if (eventName == SignalREvents.LibraryItemsRemoved)
        {
            return true;
        }

        if (assetId is { } id)
        {
            if (administrator)
            {
                return true;
            }

            var provenance = await resources.ResolveAsync(id, ct).ConfigureAwait(false);
            if (provenance is null)
            {
                return false;
            }

            // The area first (cheap, and it decides whether hiding can matter), then the one library visibility rule.
            var hasArea = (await profileAccess.GetFeatureGrantsAsync(profile.ProfileId, ct).ConfigureAwait(false)).Contains(provenance.FeatureId);
            var isVault = (await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false)).Contains(provenance.LibraryId.ToString("D"));
            var isHidden = hasArea && !isVault && await libraryVisibility.IsHiddenAsync(profile.ProfileId, provenance.LibraryId, ct).ConfigureAwait(false);
            return LibraryVisibilityRule.MaySee(false, isVault, hasArea, isHidden);
        }

        if (!administrator)
        {
            return false;
        }

        var protection = await profileAccess.GetAdminProtectionAsync(profile.ProfileId, ct).ConfigureAwait(false);
        if (protection?.IsEnabled != true)
        {
            return true;
        }

        var unlock = await profileAccess.GetAdminUnlockAsync(
            session.Id, profile.ProfileId, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        return unlock is not null && unlock.ProtectionVersion == protection.ProtectionVersion;
    }

    private async ValueTask<bool> IsAboutVaultAsync<T>(T payload, Guid? assetId, CancellationToken ct) where T : notnull
    {
        foreach (var subjectId in SubjectIds(payload, assetId))
        {
            if (await vaultLibraries.IsInVaultAsync(subjectId, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        foreach (var path in Paths(payload))
        {
            if (await vaultPaths.IsVaultPathAsync(path, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<Guid> SubjectIds<T>(T payload, Guid? assetId) where T : notnull
    {
        if (assetId is { } asset)
        {
            yield return asset;
        }

        if (payload is LibraryItemsRemovedEvent removed)
        {
            foreach (var removedId in removed.WorkIds.Concat(removed.AssetIds))
            {
                yield return removedId;
            }
        }

        var other = payload switch
        {
            ReviewItemCreatedEvent value => value.EntityId,
            ReviewItemResolvedEvent value => value.EntityId,
            ReviewItemResolvedSupplementaryEvent value => value.EntityId,
            EntityReviewResolvedEvent value => value.EntityId,
            ManualMetadataHarvestedEvent value => value.EntityId,
            RetailMetadataHarvestedEvent value => value.EntityId,
            WikidataMetadataHarvestedEvent value => value.EntityId,
            ReclassifiedMetadataHarvestedEvent value => value.EntityId,
            IngestionItemProgressEvent { MediaAssetId: { } progressAsset } => progressAsset,
            _ => (Guid?)null,
        };
        if (other is { } id)
        {
            yield return id;
        }
    }

    private static IEnumerable<string> Paths<T>(T payload) where T : notnull
    {
        switch (payload)
        {
            case IngestionStartedEvent value: yield return value.FilePath; break;
            case IngestionHashedEvent value: yield return value.FilePath; break;
            case IngestionCompletedEvent value: yield return value.FilePath; break;
            case AutoOrganizedIngestionCompletedEvent value: yield return value.Path; break;
            case IngestionFailedEvent value: yield return value.FilePath; break;
            case IngestionItemProgressEvent value: yield return value.FilePath; break;
            case MediaRemovedEvent value: yield return value.FilePath; break;
            case FolderHealthChangedEvent value: yield return value.Path; break;
            case WatchFolderActiveEvent value: yield return value.WatchDirectory; break;
            case InitialSweepStartedEvent value:
                foreach (var root in value.Roots)
                {
                    yield return root;
                }

                break;
        }
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
