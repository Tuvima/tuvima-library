using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Workers;

public sealed partial class RetailMatchWorker
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MusicAlbumManifestLocks = new();

    private async Task PersistAcceptedAppleAlbumManifestAsync(
        WorkLineage lineage,
        IReadOnlyDictionary<string, string> hints,
        IReadOnlyDictionary<string, string> bridgeIds,
        string providerName,
        CancellationToken ct)
    {
        var rootValues = await _canonicalRepo.GetByEntityAsync(lineage.TargetForParentScope, ct)
            .ConfigureAwait(false);
        var collectionId = bridgeIds.GetValueOrDefault(BridgeIdKeys.AppleMusicCollectionId)
            ?? rootValues.FirstOrDefault(value =>
                string.Equals(value.Key, BridgeIdKeys.AppleMusicCollectionId, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(collectionId))
        {
            return;
        }

        var appleProvider = _providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (appleProvider is null)
        {
            return;
        }

        await PersistAppleAlbumManifestAsync(
                lineage,
                collectionId,
                hints.GetValueOrDefault(MetadataFieldConstants.Album),
                GetMusicCreatorHint(hints),
                appleProvider.ProviderId,
                ct)
            .ConfigureAwait(false);
    }

    private async Task PersistAcceptedMusicBrainzAlbumManifestAsync(
        WorkLineage lineage,
        IReadOnlyDictionary<string, string> bridgeIds,
        CancellationToken ct)
    {
        if (_musicBrainzReleaseClient is null
            || !bridgeIds.TryGetValue(BridgeIdKeys.MusicBrainzReleaseId, out var trackReleaseId)
            || string.IsNullOrWhiteSpace(trackReleaseId))
        {
            return;
        }

        var rootWorkId = lineage.TargetForParentScope;
        var albumLock = MusicAlbumManifestLocks.GetOrAdd(rootWorkId, static _ => new SemaphoreSlim(1, 1));
        await albumLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rootValues = await _canonicalRepo.GetByEntityAsync(rootWorkId, ct).ConfigureAwait(false);

            // The album's agreed release (SharedParentIdentity) can differ from the one this track's own
            // lookup chose, and a sibling may have changed it since this job read it: the manifest follows
            // the album, whichever track gets here last.
            var releaseId = rootValues.FirstOrDefault(value =>
                    string.Equals(value.Key, BridgeIdKeys.MusicBrainzReleaseId, StringComparison.OrdinalIgnoreCase)
                    && value.WinningProviderId == WellKnownProviders.MusicBrainz
                    && !string.IsNullOrWhiteSpace(value.Value))?.Value
                ?? trackReleaseId;
            var existingManifest = rootValues.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.ChildEntitiesJson, StringComparison.OrdinalIgnoreCase))?.Value;
            if (MusicBrainzAlbumManifestJson.IsCompleteForRelease(existingManifest, releaseId))
            {
                return;
            }

            var release = await _musicBrainzReleaseClient.FetchReleaseAsync(releaseId, ct).ConfigureAwait(false);
            if (release is null)
            {
                return;
            }

            var values = new List<CanonicalValue>
            {
                new()
                {
                    EntityId = rootWorkId,
                    Key = MetadataFieldConstants.ChildEntitiesJson,
                    Value = release.ManifestJson,
                    LastScoredAt = DateTimeOffset.UtcNow,
                    WinningProviderId = WellKnownProviders.MusicBrainz,
                },
                new()
                {
                    EntityId = rootWorkId,
                    Key = MetadataFieldConstants.TrackCount,
                    Value = release.TrackCount.ToString(CultureInfo.InvariantCulture),
                    LastScoredAt = DateTimeOffset.UtcNow,
                    WinningProviderId = WellKnownProviders.MusicBrainz,
                },
                new()
                {
                    EntityId = rootWorkId,
                    Key = BridgeIdKeys.MusicBrainzReleaseId,
                    Value = release.ReleaseId,
                    LastScoredAt = DateTimeOffset.UtcNow,
                    WinningProviderId = WellKnownProviders.MusicBrainz,
                },
            };

            var hasManagedCover = rootValues.Any(value =>
                (value.Key is MetadataFieldConstants.Cover or MetadataFieldConstants.CoverUrl)
                && !string.IsNullOrWhiteSpace(value.Value));
            if (!hasManagedCover && !string.IsNullOrWhiteSpace(release.CoverUrl))
            {
                values.Add(new CanonicalValue
                {
                    EntityId = rootWorkId,
                    Key = MetadataFieldConstants.CoverUrl,
                    Value = release.CoverUrl,
                    LastScoredAt = DateTimeOffset.UtcNow,
                    WinningProviderId = WellKnownProviders.MusicBrainz,
                });
            }

            await _canonicalRepo.UpsertBatchAsync(values, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Music: persisted exact {TrackCount}-track MusicBrainz release manifest {ReleaseId} on root work {RootWorkId}",
                release.TrackCount,
                release.ReleaseId,
                rootWorkId);
        }
        finally
        {
            albumLock.Release();
        }
    }

    private async Task PersistAppleAlbumManifestAsync(
        WorkLineage lineage,
        string collectionId,
        string? album,
        string? artist,
        Guid providerId,
        CancellationToken ct,
        IReadOnlyList<JsonNode>? knownTracks = null)
    {
        var rootWorkId = lineage.TargetForParentScope;
        var albumLock = MusicAlbumManifestLocks.GetOrAdd(rootWorkId, static _ => new SemaphoreSlim(1, 1));
        await albumLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rootValues = await _canonicalRepo.GetByEntityAsync(rootWorkId, ct).ConfigureAwait(false);
            var existingManifest = rootValues.FirstOrDefault(value =>
                string.Equals(value.Key, MetadataFieldConstants.ChildEntitiesJson, StringComparison.OrdinalIgnoreCase))?.Value;
            if (AppleAlbumManifestJson.IsCompleteForCollection(existingManifest, collectionId))
            {
                return;
            }

            var tracks = knownTracks
                ?? await _appleClient.FetchAlbumTracksAsync(collectionId, "us", "en", ct).ConfigureAwait(false);
            if (tracks.Count == 0)
            {
                return;
            }

            var manifest = AppleAlbumManifestJson.Build(tracks, collectionId, album, artist);
            var trackCount = tracks.Count(track => !string.IsNullOrWhiteSpace(track["trackName"]?.GetValue<string>()));
            if (trackCount == 0)
            {
                return;
            }

            await _canonicalRepo.UpsertBatchAsync(
                [
                    new CanonicalValue
                    {
                        EntityId = rootWorkId,
                        Key = MetadataFieldConstants.ChildEntitiesJson,
                        Value = manifest,
                        LastScoredAt = DateTimeOffset.UtcNow,
                        WinningProviderId = providerId,
                    },
                    new CanonicalValue
                    {
                        EntityId = rootWorkId,
                        Key = MetadataFieldConstants.TrackCount,
                        Value = trackCount.ToString(CultureInfo.InvariantCulture),
                        LastScoredAt = DateTimeOffset.UtcNow,
                        WinningProviderId = providerId,
                    },
                    new CanonicalValue
                    {
                        EntityId = rootWorkId,
                        Key = BridgeIdKeys.AppleMusicCollectionId,
                        Value = collectionId,
                        LastScoredAt = DateTimeOffset.UtcNow,
                        WinningProviderId = providerId,
                    },
                ],
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Music: persisted {TrackCount}-track Apple album manifest for '{Artist}' / '{Album}' on root work {RootWorkId}",
                trackCount,
                artist ?? "(unknown artist)",
                album ?? "(unknown album)",
                rootWorkId);
        }
        finally
        {
            albumLock.Release();
        }
    }
}
