using System.Net;
using System.Text;
using MediaEngine.Api.Services.Collections;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class AlbumTrackManifestRepairTests
{
    [Fact]
    public void MusicBrainzReleaseManifest_ConfirmsOnlyItsOwnRecording()
    {
        const string manifest = """
            {
              "source": "musicbrainz_release",
              "provider_collection_id": "3dd79a9c-ede6-4d05-8735-5bb51a3e505b",
              "tracks": [
                { "title": "Correct Track", "ordinal": 1, "track_number": 1, "musicbrainz_recording_id": "recording-in-release" }
              ]
            }
            """;

        Assert.True(MusicBrainzAlbumManifestJson.ContainsRecording(manifest, "recording-in-release"));
        Assert.False(MusicBrainzAlbumManifestJson.ContainsRecording(manifest, "recording-on-other-release"));
        Assert.Null(MusicBrainzAlbumManifestJson.ContainsRecording("{}", "recording-in-release"));
    }

    [Fact]
    public async Task LegacyBoxSetManifest_IsReplacedByResolvedAlbumManifest()
    {
        var rootWorkId = Guid.NewGuid();
        var canonicalRepo = new StubCanonicalValueRepository();
        var httpFactory = new RoutingHttpClientFactory(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/search?", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "results": [
                        { "collectionName": "A New Career in a New Town (1977-1982)", "artistName": "David Bowie", "collectionId": 1255088551 },
                        { "collectionName": "\"Heroes\" (2017 Remaster)", "artistName": "David Bowie", "collectionId": 1347894082 }
                      ]
                    }
                    """);
            }

            return JsonResponse("""
                {
                  "results": [
                    { "wrapperType": "collection", "collectionId": 1347894082 },
                    { "wrapperType": "track", "kind": "song", "trackName": "Beauty and the Beast", "trackNumber": 1, "discNumber": 1, "trackTimeMillis": 215000, "trackId": 1347894083, "artworkUrl100": "https://example.test/100x100bb.jpg" },
                    { "wrapperType": "track", "kind": "music-video", "trackName": "Documentary", "trackNumber": 2, "trackTimeMillis": 600000, "trackId": 999 },
                    { "wrapperType": "track", "kind": "song", "trackName": "Heroes", "trackNumber": 3, "discNumber": 1, "trackTimeMillis": 370000, "trackId": 1347894085 }
                  ]
                }
                """);
        });
        var client = new AppleRetailClient(
            httpFactory,
            new RetailRequestBuilder(),
            new ProviderRateLimiterCoordinator(),
            NullLogger<AppleRetailClient>.Instance);
        var musicBrainzClient = new MusicBrainzReleaseClient(
            httpFactory,
            new ProviderRateLimiterCoordinator(),
            NullLogger<MusicBrainzReleaseClient>.Instance);
        var service = new AlbumTrackManifestService(canonicalRepo, client, musicBrainzClient);
        var legacyManifest = """
            {
              "tracks": [
                { "title": "Wrong Box Set Track", "ordinal": 1, "track_number": 1, "duration_seconds": 100, "source": "apple_itunes" }
              ]
            }
            """;

        var result = await service.EnsureAlbumTrackManifestAsync(
            rootWorkId,
            "David Bowie",
            "Heroes",
            legacyManifest,
            [],
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("\"provider_collection_id\":\"1347894082\"", result, StringComparison.Ordinal);
        Assert.Contains("\"Beauty and the Beast\"", result, StringComparison.Ordinal);
        Assert.Contains("\"Heroes\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrong Box Set Track", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Documentary", result, StringComparison.Ordinal);
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == MetadataFieldConstants.TrackCount
            && value.Value == "2");
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == BridgeIdKeys.AppleMusicCollectionId
            && value.Value == "1347894082");
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == MetadataFieldConstants.CoverUrl
            && value.Value == "https://example.test/9999x9999bb.jpg");
    }

    [Fact]
    public async Task ExactMusicBrainzRelease_ProvidesManifestAndCoverBeforeAppleNameFallback()
    {
        const string releaseId = "3dd79a9c-ede6-4d05-8735-5bb51a3e505b";
        var rootWorkId = Guid.NewGuid();
        var canonicalRepo = new StubCanonicalValueRepository();
        canonicalRepo.Values.AddRange(
        [
            new CanonicalValue
            {
                EntityId = rootWorkId,
                Key = BridgeIdKeys.MusicBrainzReleaseId,
                Value = releaseId,
                WinningProviderId = WellKnownProviders.MusicBrainz,
            },
            new CanonicalValue
            {
                EntityId = rootWorkId,
                Key = MetadataFieldConstants.TrackCount,
                Value = "50",
                WinningProviderId = WellKnownProviders.MusicBrainz,
            },
        ]);

        var httpFactory = new RoutingHttpClientFactory(request =>
        {
            Assert.Contains($"/release/{releaseId}", request.RequestUri!.ToString(), StringComparison.Ordinal);
            return JsonResponse($$"""
                {
                  "id": "{{releaseId}}",
                  "title": "Interstellar",
                  "artist-credit": [
                    { "name": "Hans Zimmer", "joinphrase": "" }
                  ],
                  "cover-art-archive": { "artwork": true, "front": true },
                  "media": [
                    {
                      "position": 1,
                      "tracks": [
                        { "position": 1, "title": "Dreaming of the Crash", "length": 235840, "recording": { "id": "ba6f5a1a-d8fc-4a7a-afd0-97c42cdab38b", "title": "Dreaming of the Crash", "length": 235840 } },
                        { "position": 2, "title": "Cornfield Chase", "length": 126960, "recording": { "id": "442c73a5-8b61-40e6-8eb2-bcd913e1b88d", "title": "Cornfield Chase", "length": 126960 } }
                      ]
                    }
                  ]
                }
                """);
        });
        var appleClient = new AppleRetailClient(
            httpFactory,
            new RetailRequestBuilder(),
            new ProviderRateLimiterCoordinator(),
            NullLogger<AppleRetailClient>.Instance);
        var musicBrainzClient = new MusicBrainzReleaseClient(
            httpFactory,
            new ProviderRateLimiterCoordinator(),
            NullLogger<MusicBrainzReleaseClient>.Instance);
        var service = new AlbumTrackManifestService(
            canonicalRepo,
            appleClient,
            musicBrainzClient);

        var result = await service.EnsureAlbumTrackManifestAsync(
            rootWorkId,
            "Hans Zimmer",
            "Interstellar",
            null,
            canonicalRepo.Values,
            CancellationToken.None);

        Assert.True(MusicBrainzAlbumManifestJson.IsCompleteForRelease(result, releaseId));
        Assert.Contains("\"Cornfield Chase\"", result, StringComparison.Ordinal);
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == MetadataFieldConstants.TrackCount
            && value.Value == "2");
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == MetadataFieldConstants.CoverUrl
            && value.Value == $"https://coverartarchive.org/release/{releaseId}/front-500");
    }

    [Fact]
    public async Task CompleteManifest_RepairsTrackCountWithoutProviderLookup()
    {
        var rootWorkId = Guid.NewGuid();
        var canonicalRepo = new StubCanonicalValueRepository();
        canonicalRepo.Values.AddRange(
        [
            new CanonicalValue
            {
                EntityId = rootWorkId,
                Key = MetadataFieldConstants.TrackCount,
                Value = "50",
                WinningProviderId = WellKnownProviders.MusicBrainz,
            },
            new CanonicalValue
            {
                EntityId = rootWorkId,
                Key = MetadataFieldConstants.CoverUrl,
                Value = "https://example.test/cover.jpg",
                WinningProviderId = WellKnownProviders.AppleApi,
            },
        ]);
        var manifest = """
            {
              "schema": "music_album_tracks_v1",
              "source": "apple_itunes_album",
              "provider_collection_id": "123",
              "tracks": [
                { "title": "One", "ordinal": 1, "track_number": 1, "duration_seconds": 100 },
                { "title": "Two", "ordinal": 2, "track_number": 2, "duration_seconds": 120 }
              ]
            }
            """;
        var httpFactory = new RoutingHttpClientFactory(_ =>
            throw new Xunit.Sdk.XunitException("A complete manifest must not require a provider lookup."));
        var service = new AlbumTrackManifestService(
            canonicalRepo,
            new AppleRetailClient(
                httpFactory,
                new RetailRequestBuilder(),
                new ProviderRateLimiterCoordinator(),
                NullLogger<AppleRetailClient>.Instance),
            new MusicBrainzReleaseClient(
                httpFactory,
                new ProviderRateLimiterCoordinator(),
                NullLogger<MusicBrainzReleaseClient>.Instance));

        var result = await service.EnsureAlbumTrackManifestAsync(
            rootWorkId,
            "Artist",
            "Album",
            manifest,
            canonicalRepo.Values,
            CancellationToken.None);

        Assert.Equal(manifest, result);
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == MetadataFieldConstants.TrackCount
            && value.Value == "2"
            && value.WinningProviderId == WellKnownProviders.AppleApi);
    }

    [Fact]
    public async Task SelectedRelease_ReplacesCompleteStaleManifestWithExactMusicBrainzRelease()
    {
        const string releaseId = "d6dbf151-f092-4df7-89ab-f0da3ebe539c";
        var rootWorkId = Guid.NewGuid();
        var canonicalRepo = new StubCanonicalValueRepository();
        canonicalRepo.Values.Add(new CanonicalValue
        {
            EntityId = rootWorkId,
            Key = BridgeIdKeys.MusicBrainzReleaseId,
            Value = releaseId,
            WinningProviderId = WellKnownProviders.MusicBrainz,
        });
        var staleManifest = """
            {
              "schema": "music_album_tracks_v1",
              "source": "apple_itunes_album",
              "provider_collection_id": "old-five-track-edition",
              "tracks": [
                { "title": "The Real Slim Shady (instrumental)", "ordinal": 1 },
                { "title": "Stan", "ordinal": 2 }
              ]
            }
            """;
        var httpFactory = new RoutingHttpClientFactory(request =>
        {
            Assert.Contains($"/release/{releaseId}", request.RequestUri!.ToString(), StringComparison.Ordinal);
            return JsonResponse($$"""
                {
                  "id": "{{releaseId}}",
                  "title": "The Marshall Mathers LP",
                  "artist-credit": [{ "name": "Eminem", "joinphrase": "" }],
                  "cover-art-archive": { "artwork": true, "front": true },
                  "media": [{
                    "position": 1,
                    "tracks": [
                      { "position": 1, "title": "Public Service Announcement 2000", "length": 25000, "recording": { "id": "11111111-1111-1111-1111-111111111111", "title": "Public Service Announcement 2000", "length": 25000 } },
                      { "position": 2, "title": "Kill You", "length": 264000, "recording": { "id": "22222222-2222-2222-2222-222222222222", "title": "Kill You", "length": 264000 } },
                      { "position": 3, "title": "Stan", "length": 404000, "recording": { "id": "33333333-3333-3333-3333-333333333333", "title": "Stan", "length": 404000 } }
                    ]
                  }]
                }
                """);
        });
        var service = new AlbumTrackManifestService(
            canonicalRepo,
            new AppleRetailClient(
                httpFactory,
                new RetailRequestBuilder(),
                new ProviderRateLimiterCoordinator(),
                NullLogger<AppleRetailClient>.Instance),
            new MusicBrainzReleaseClient(
                httpFactory,
                new ProviderRateLimiterCoordinator(),
                NullLogger<MusicBrainzReleaseClient>.Instance));

        var result = await service.EnsureAlbumTrackManifestAsync(
            rootWorkId,
            "Eminem",
            "The Marshall Mathers LP",
            staleManifest,
            canonicalRepo.Values,
            CancellationToken.None);

        Assert.True(MusicBrainzAlbumManifestJson.IsCompleteForRelease(result, releaseId));
        Assert.Contains("Public Service Announcement 2000", result, StringComparison.Ordinal);
        Assert.Contains("Kill You", result, StringComparison.Ordinal);
        Assert.DoesNotContain("old-five-track-edition", result, StringComparison.Ordinal);
        Assert.Contains(canonicalRepo.Values, value =>
            value.EntityId == rootWorkId
            && value.Key == MetadataFieldConstants.TrackCount
            && value.Value == "3"
            && value.WinningProviderId == WellKnownProviders.MusicBrainz);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class RoutingHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new RoutingHttpMessageHandler(responder), disposeHandler: true);
    }

    private sealed class RoutingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StubCanonicalValueRepository : ICanonicalValueRepository
    {
        public List<CanonicalValue> Values { get; } = [];

        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(
            Guid entityId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>(
                Values.Where(value => value.EntityId == entityId).ToList());

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(
            IReadOnlyList<Guid> entityIds,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(
                entityIds.ToDictionary(
                    entityId => entityId,
                    entityId => (IReadOnlyList<CanonicalValue>)Values
                        .Where(value => value.EntityId == entityId)
                        .ToList()));

        public Task UpsertBatchAsync(
            IReadOnlyList<CanonicalValue> values,
            CancellationToken ct = default)
        {
            foreach (var value in values)
            {
                Values.RemoveAll(existing =>
                    existing.EntityId == value.EntityId
                    && string.Equals(existing.Key, value.Key, StringComparison.OrdinalIgnoreCase));
                Values.Add(value);
            }

            return Task.CompletedTask;
        }

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
        {
            Values.RemoveAll(value => value.EntityId == entityId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default)
        {
            Values.RemoveAll(value =>
                value.EntityId == entityId
                && string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> FindByValueAsync(
            string key,
            string value,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(
            string key,
            string prefix,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(
            string hasField,
            string missingField,
            int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
