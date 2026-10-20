using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class CatalogUpsertServiceTests
{
    [Fact]
    public async Task UpsertChildrenAsync_BatchesClaimsAndCanonicalsOncePerPayload()
    {
        var works = new FakeWorkRepository();
        var claims = new CountingMetadataClaimRepository();
        var canonicals = new CountingCanonicalValueRepository();
        var service = new CatalogUpsertService(works, claims: claims, canonicals: canonicals);

        var inserted = await service.UpsertChildrenAsync(
            Guid.NewGuid(),
            MediaType.Music,
            """
            {
              "tracks": [
                { "title": "Track One", "ordinal": 1, "qid": "Q1", "description": "First track" },
                { "title": "Track Two", "ordinal": 2, "qid": "Q2", "description": "Second track" }
              ]
            }
            """);

        Assert.Equal(2, inserted);
        Assert.Equal(2, works.InsertedChildren);
        Assert.Equal(1, claims.InsertBatchCalls);
        Assert.Equal(1, canonicals.UpsertBatchCalls);
        Assert.True(claims.InsertedClaims.Count >= 4);
        Assert.Equal(claims.InsertedClaims.Count, canonicals.UpsertedValues.Count);
    }

    [Fact]
    public async Task TvCataloguePayload_CannotCreateOrOverwriteSeasonWorks()
    {
        var works = new FakeWorkRepository();
        var claims = new CountingMetadataClaimRepository();
        var canonicals = new CountingCanonicalValueRepository();
        var service = new CatalogUpsertService(works, claims: claims, canonicals: canonicals);
        const string payload = """{"seasons":[{"season_number":1,"episodes":[{"episode_number":1,"title":"Pilot"}]}],"unassigned_episodes":[{"episode_number":1,"title":"Pilot","description":"Long encyclopedia plot"}]}""";
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(0, await service.UpsertChildrenAsync(Guid.NewGuid(), MediaType.TV, payload));
        }

        Assert.Equal(0, works.InsertedChildren);
        Assert.Equal(0, claims.InsertBatchCalls);
        Assert.Equal(0, canonicals.UpsertBatchCalls);
    }

    private sealed class FakeWorkRepository : IWorkRepository
    {
        public int InsertedChildren { get; private set; }

        public Task<Guid?> FindParentByKeyAsync(MediaType mediaType, string parentKey, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid> GetOrCreateParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid?> FindChildByOrdinalAsync(Guid parentWorkId, int ordinal, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid?> FindChildByOrdinalSortAsync(Guid parentWorkId, double ordinalSort, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid?> FindChildByTitleAsync(Guid parentWorkId, string title, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid?> FindByExternalIdentifierAsync(string scheme, string value, CancellationToken ct = default)
            => Task.FromResult<Guid?>(null);

        public Task<Guid> InsertParentAsync(MediaType mediaType, string parentKey, Guid? grandparentWorkId, int? ordinal, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid> InsertChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid> GetOrCreateChildAsync(MediaType mediaType, Guid parentWorkId, int? ordinal, double? ordinalSort = null, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task UpdateOrdinalSortAsync(Guid workId, double? ordinalSort, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<Guid> InsertStandaloneAsync(MediaType mediaType, CancellationToken ct = default)
            => Task.FromResult(Guid.NewGuid());

        public Task<Guid> InsertCatalogChildAsync(
            MediaType mediaType,
            Guid parentWorkId,
            int? ordinal,
            IReadOnlyDictionary<string, string>? externalIdentifiers,
            CancellationToken ct = default)
        {
            InsertedChildren++;
            return Task.FromResult(Guid.NewGuid());
        }

        public Task PromoteCatalogToOwnedAsync(Guid workId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task WriteExternalIdentifiersAsync(Guid workId, IReadOnlyDictionary<string, string> identifiers, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<WorkLineage?> GetLineageByAssetAsync(Guid assetId, CancellationToken ct = default)
            => Task.FromResult<WorkLineage?>(null);

        public Task<ConfirmedSiblingWorkQid?> FindConfirmedSiblingQidAsync(MediaType sourceMediaType, IReadOnlyList<MediaType> candidateMediaTypes, string title, string? creator, Guid? excludeWorkId = null, bool requireSettledIdentity = false, CancellationToken ct = default)
            => Task.FromResult<ConfirmedSiblingWorkQid?>(null);
    }

    private sealed class CountingMetadataClaimRepository : IMetadataClaimRepository
    {
        public int InsertBatchCalls { get; private set; }
        public List<MetadataClaim> InsertedClaims { get; } = [];

        public Task InsertBatchAsync(IReadOnlyList<MetadataClaim> claims, CancellationToken ct = default)
        {
            InsertBatchCalls++;
            InsertedClaims.AddRange(claims);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MetadataClaim>>([]);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CountingCanonicalValueRepository : ICanonicalValueRepository
    {
        public int UpsertBatchCalls { get; private set; }
        public List<CanonicalValue> UpsertedValues { get; } = [];

        public Task UpsertBatchAsync(IReadOnlyList<CanonicalValue> values, CancellationToken ct = default)
        {
            UpsertBatchCalls++;
            UpsertedValues.AddRange(values);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>>(new Dictionary<Guid, IReadOnlyList<CanonicalValue>>());

        public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> FindByValueAsync(string key, string value, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(string key, string prefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CanonicalValue>>([]);

        public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(string hasField, string missingField, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Guid>>([]);
    }
}
