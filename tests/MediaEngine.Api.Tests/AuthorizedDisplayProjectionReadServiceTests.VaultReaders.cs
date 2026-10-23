using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

// WP17 part C: one seeded Vault work and one ordinary work behind each Part A read service. Each test names a service and asserts the
// Vault work is absent from everything it answers, and the ordinary one is still there (so the test cannot pass by returning nothing).
public sealed partial class AuthorizedDisplayProjectionReadServiceTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value);

    [Fact]
    public async Task VaultReaders_CollectionBrowse_NeverListsAVaultWorkInAnyCollection()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var collectionId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO collections (id, display_name, created_at) VALUES (@collection, 'Shared shelf', CURRENT_TIMESTAMP);
                UPDATE works SET collection_id = @collection, media_type = 'Movies' WHERE id IN (@vault, @normal);
                """,
                new { collection = collectionId, vault = shape.Secret.WorkId, normal = shape.Normal.WorkId });
        }

        var service = new CollectionBrowseReadService(new CollectionRepository(_database), _database, NullLogger<CollectionBrowseReadService>.Instance);
        var all = Json(await service.GetAllAsync(default));

        Assert.Contains(shape.Normal.WorkId.ToString("D"), all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shape.Secret.WorkId.ToString("D"), all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultReaders_CollectionMediaLookup_NeverCountsOrResolvesAVaultWork()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var service = new CollectionMediaLookupReadService(_database);
        var ids = new[] { shape.Secret.WorkId, shape.Normal.WorkId };

        var counts = await service.CountMediaTypesAsync(ids, default);
        var resolved = Json(await service.ResolveMetadataAsync(ids, default));

        Assert.Equal(1, counts.Values.Sum());
        Assert.Contains(shape.Normal.WorkId.ToString("D"), resolved, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shape.Secret.WorkId.ToString("D"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultReaders_LibraryCuration_NeverLoadsAVaultItemForABatchEdit()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);

        var items = await new LibraryCurationReadService(_database).LoadBatchEditItemsAsync(
            [shape.Secret.WorkId, shape.Secret.AssetId, shape.Normal.WorkId], ["title"], default);

        Assert.DoesNotContain(shape.Secret.WorkId, items.Keys);
        Assert.DoesNotContain(shape.Secret.AssetId, items.Keys);
        Assert.Contains(shape.Normal.WorkId, items.Keys);
    }

    [Fact]
    public async Task VaultReaders_LibraryOverview_NeverCountsAVaultFileAsRecentlyAdded()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        using (var connection = _database.CreateConnection())
        {
            var provider = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO metadata_providers (id, name, version, is_enabled) VALUES (@provider, 'overview-test', '1', 1);
                INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
                VALUES (@one, @vault, @provider, 'title', 'Secret', 1.0, @now), (@two, @normal, @provider, 'title', 'Ordinary', 1.0, @now);
                """,
                new { provider, one = Guid.NewGuid(), two = Guid.NewGuid(), vault = shape.Secret.AssetId, normal = shape.Normal.AssetId, now = DateTimeOffset.UtcNow.ToString("O") });
        }

        var overview = await new LibraryOverviewReadService(_database).GetOverviewAggregatesAsync(default);

        Assert.Equal(1, overview.Added24h);
        Assert.Equal(1, overview.Added7d);
        Assert.Equal(1, overview.Added30d);
    }

    [Fact]
    public async Task VaultReaders_CollectionSearch_DropsAVaultWorkEvenWhenTheIndexNamesIt()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var service = new CollectionSearchReadService(_database, new NamingSearchIndex(shape.Secret.WorkId, shape.Normal.WorkId));

        var found = Json(await service.SearchAsync("scene", default));

        Assert.Contains(shape.Normal.WorkId.ToString("D"), found, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(shape.Secret.WorkId.ToString("D"), found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VaultReaders_TheNeedsReviewList_AndItsCounts_NeverIncludeAVaultFile()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        var vaultReview = await InsertReviewAsync(shape.Secret.AssetId, ReviewStatus.Pending);
        var vaultWorkReview = await InsertReviewAsync(shape.Secret.WorkId, ReviewStatus.Pending, "Work");
        var ordinaryReview = await InsertReviewAsync(shape.Normal.AssetId, ReviewStatus.Pending);
        var service = new ReviewQueueReadService(_database);

        var pending = await service.GetPendingAsync(50);
        var ids = pending.Select(item => item.Id).ToList();

        Assert.Contains(ordinaryReview, ids);
        Assert.DoesNotContain(vaultReview, ids);
        Assert.DoesNotContain(vaultWorkReview, ids);
        Assert.Equal(1, await service.GetPendingCountAsync());
        Assert.Equal(1, (await service.GetPendingReasonCountsAsync()).Sum(reason => reason.Count));
        Assert.Null(await service.GetByIdAsync(vaultReview));
        Assert.Null(await service.GetByIdAsync(vaultWorkReview));
        Assert.NotNull(await service.GetByIdAsync(ordinaryReview));
    }

    [Fact]
    public async Task VaultReaders_IngestionPresentation_NeverCountsOrShowsAVaultFile()
    {
        var shape = await SeedVaultAsync(administrator: true, hasAStaleHiddenRowOnTheVaultLibrary: true);
        await InsertReviewAsync(shape.Secret.AssetId, ReviewStatus.Pending);
        await InsertReviewAsync(shape.Normal.AssetId, ReviewStatus.Pending);
        var operations = new MediaOperationRepository(_database);
        await operations.EnsureAsync(OperationAbout(shape.Secret.AssetId, "/watch/secret.mkv", "presentation-vault"));
        await operations.EnsureAsync(OperationAbout(shape.Normal.AssetId, "/watch/ordinary.mkv", "presentation-ordinary"));
        var older = DateTimeOffset.UtcNow.AddHours(-3);
        var newer = DateTimeOffset.UtcNow.AddMinutes(-1);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO ingestion_log (id, file_path, media_asset_id, status, detected_title, created_at, updated_at)
                VALUES (@one, '/watch/ordinary.mkv', @normal, 'completed', 'Ordinary title', @older, @older),
                       (@two, '/watch/secret.mkv', @vault, 'completed', 'Secret title', @newer, @newer);
                """,
                new
                {
                    one = Guid.NewGuid(),
                    two = Guid.NewGuid(),
                    normal = shape.Normal.AssetId,
                    vault = shape.Secret.AssetId,
                    older = older.ToString("O"),
                    newer = newer.ToString("O"),
                });
        }

        var service = new IngestionPresentationReadService(_database);
        var snapshot = await service.GetSnapshotAsync();
        var summary = await service.GetActivitySummaryAsync();
        var current = Json(await service.GetCurrentMediaAsync(0, 50));
        var recent = Json(await service.GetRecentAdditionsAsync(null, null, null, null, 0, 50));

        Assert.Equal(1, summary.ItemsNeedingFollowUp);
        Assert.Contains(snapshot.Attention, item => item.Kind == "review" && item.Count == 1);
        Assert.Equal(1, snapshot.QueuedOperations);
        Assert.NotNull(summary.LastActivityAt);
        Assert.True(summary.LastActivityAt <= older.AddMinutes(1), "The newest activity is the Vault file's; it must not set the last-activity time.");
        foreach (var shown in new[] { Json(snapshot), current, recent })
        {
            Assert.DoesNotContain("Secret title", shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(shape.Secret.AssetId.ToString("D"), shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(shape.Secret.WorkId.ToString("D"), shown, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The search index is asked for "scene" and answers with a Vault work and an ordinary one, as a stale index might.</summary>
    private sealed class NamingSearchIndex(params Guid[] workIds) : ISearchIndexRepository
    {
        public Task UpsertByEntityIdAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> SearchAsync(string query, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(workIds);

        public Task RebuildAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
