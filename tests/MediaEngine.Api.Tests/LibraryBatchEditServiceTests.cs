using Dapper;
using MediaEngine.Api.Services.BatchEdit;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Batch metadata edits against a real temp SQLite data store and the real repositories.</summary>
public sealed class LibraryBatchEditServiceTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-batch-edit-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly ItemCanonicalRepository _items;
    private readonly ReviewQueueRepository _reviews;
    private readonly SystemActivityRepository _activity;

    public LibraryBatchEditServiceTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _items = new ItemCanonicalRepository(_db, NullLogger<ItemCanonicalRepository>.Instance);
        _reviews = new ReviewQueueRepository(_db);
        _activity = new SystemActivityRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch (Exception) { /* test cleanup: the temp file goes next */ }
        try { File.Delete(_dbPath); } catch (IOException) { /* a leftover temp file is harmless */ }
    }

    // ── Preview ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_WhenEveryEpisodeHasTheSameGenre_ShowsThatValueAsNotMixed()
    {
        var show = SeedShow(episodeCount: 3);
        foreach (var episode in show.Episodes)
        {
            SeedGenre(episode.AssetId, "Drama");
        }

        var preview = await CreateService().PreviewAsync(show.EpisodeIds, ["genre"], NoRefusals);

        var genre = Assert.Single(preview.Fields);
        Assert.Equal(3, preview.AffectedCount);
        Assert.Equal(LibraryBatchFieldKinds.List, genre.Kind);
        Assert.False(genre.IsMixed);
        Assert.Equal(["Drama"], genre.Values);
        Assert.True(genre.Editable);
        Assert.Equal(["add", "replace", "remove"], genre.Operations);
    }

    [Fact]
    public async Task Preview_WhenTagsDiffer_ShowsMixedWithTheDistinctValuesAndHowManyItemsCarryEach()
    {
        var show = SeedShow(episodeCount: 3);
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = "Favourite; Rewatch" });
        await SetOverridesAsync(show.Episodes[1].WorkId, new() { ["custom_tags"] = "Favourite" });

        var preview = await CreateService().PreviewAsync(show.EpisodeIds, ["custom_tags"], NoRefusals);

        var tags = Assert.Single(preview.Fields);
        Assert.True(tags.IsMixed);
        Assert.Equal(2, tags.DistinctCount);
        Assert.Equal(1, tags.EmptyCount);
        Assert.Collection(
            tags.DistinctValues,
            first => { Assert.Equal("Favourite", first.Value); Assert.Equal(2, first.Count); },
            second => { Assert.Equal("Rewatch", second.Value); Assert.Equal(1, second.Count); });
    }

    [Fact]
    public async Task Preview_WhenScalarValuesDiffer_ListsAtMostFiveDistinctValuesButCountsAll()
    {
        var show = SeedShow(episodeCount: 7);
        for (var i = 0; i < show.Episodes.Count; i++)
        {
            await SetOverridesAsync(show.Episodes[i].WorkId, new() { ["rating"] = $"{i + 1}" });
        }

        var preview = await CreateService().PreviewAsync(show.EpisodeIds, ["rating"], NoRefusals);

        var rating = Assert.Single(preview.Fields);
        Assert.Equal(LibraryBatchFieldKinds.Scalar, rating.Kind);
        Assert.True(rating.IsMixed);
        Assert.Equal(5, rating.DistinctValues.Count);
        Assert.Equal(7, rating.DistinctCount);
        Assert.Equal(["set", "clear"], rating.Operations);
    }

    [Fact]
    public async Task Preview_WhenScalarsAgree_ShowsTheSharedValue()
    {
        var show = SeedShow(episodeCount: 2);
        foreach (var episode in show.Episodes)
        {
            await SetOverridesAsync(episode.WorkId, new() { ["language"] = "en" });
        }

        var preview = await CreateService().PreviewAsync(show.EpisodeIds, ["language"], NoRefusals);

        var language = Assert.Single(preview.Fields);
        Assert.False(language.IsMixed);
        Assert.Equal("en", language.Value);
    }

    [Fact]
    public async Task Preview_WhenAnEpisodeOnlyInheritsItsGenre_MarksTheFieldNotEditableAndNamesThatEpisode()
    {
        var show = SeedShow(episodeCount: 3);
        SeedGenre(show.SeasonId, "Drama");
        SeedGenre(show.Episodes[0].AssetId, "Comedy");
        SeedGenre(show.Episodes[1].AssetId, "Comedy");

        var preview = await CreateService().PreviewAsync(show.EpisodeIds, ["genre"], NoRefusals);

        var genre = Assert.Single(preview.Fields);
        Assert.False(genre.Editable);
        var locked = Assert.Single(genre.LockedItems);
        Assert.Equal(show.Episodes[2].WorkId, locked.EntityId);
        Assert.Contains("parent", locked.Reason, StringComparison.Ordinal);
        Assert.True(genre.IsMixed);
    }

    [Fact]
    public async Task Preview_ReportsProviderFactCreditsAsLockedForEveryItem()
    {
        var show = SeedShow(episodeCount: 2);

        var preview = await CreateService().PreviewAsync(show.EpisodeIds, ["director"], NoRefusals);

        var director = Assert.Single(preview.Fields);
        Assert.False(director.Editable);
        Assert.Empty(director.Operations);
        Assert.Equal(2, director.LockedItems.Count);
        Assert.All(director.LockedItems, item => Assert.Contains("catalogue", item.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_ListsRefusedAndMissingItemsAsUnavailableAndLeavesThemOutOfTheCount()
    {
        var show = SeedShow(episodeCount: 2);
        var missing = Guid.NewGuid();
        var refused = show.Episodes[1].WorkId;
        var ids = new List<Guid> { show.Episodes[0].WorkId, refused, missing };

        var preview = await CreateService().PreviewAsync(
            ids, ["rating"], new Dictionary<Guid, string> { [refused] = "You are not allowed to edit this item." });

        Assert.Equal(1, preview.AffectedCount);
        Assert.Contains(preview.UnavailableItems, item => item.EntityId == refused && item.Reason.Contains("not allowed", StringComparison.Ordinal));
        Assert.Contains(preview.UnavailableItems, item => item.EntityId == missing && item.Reason.Contains("no longer", StringComparison.Ordinal));
    }

    // ── Apply: list fields ──────────────────────────────────────────────────

    [Fact]
    public async Task Apply_AddToAList_PutsTheTagOnEveryItemWithoutDuplicates()
    {
        var show = SeedShow(episodeCount: 3);
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = "Favourite" });
        await SetOverridesAsync(show.Episodes[1].WorkId, new() { ["custom_tags"] = "favourite; Rewatch" });

        var result = await CreateService().ApplyAsync(
            show.EpisodeIds, [ListChange("custom_tags", "add", "Favourite")], NoRefusals);

        Assert.Equal(3, result.UpdatedCount);
        Assert.Equal(["Favourite"], await TagsOfAsync(show.Episodes[0].WorkId));
        Assert.Equal(["favourite", "Rewatch"], await TagsOfAsync(show.Episodes[1].WorkId));
        Assert.Equal(["Favourite"], await TagsOfAsync(show.Episodes[2].WorkId));
    }

    [Fact]
    public async Task Apply_AddingToAGenreTheItemOnlyHasFromTheProvider_KeepsTheProvidersGenresAndAddsTheNewOne()
    {
        var show = SeedShow(episodeCount: 1);
        SeedGenre(show.Episodes[0].AssetId, "Drama");

        var result = await CreateService().ApplyAsync(
            show.EpisodeIds, [ListChange("genre", "add", "Comedy")], NoRefusals);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal("Drama, Comedy", (await OverridesOfAsync(show.Episodes[0].WorkId))["genre"]);
    }

    [Fact]
    public async Task Apply_ReplaceAList_SetsExactlyTheGivenList()
    {
        var show = SeedShow(episodeCount: 2);
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = "Old one; Old two" });

        var result = await CreateService().ApplyAsync(
            show.EpisodeIds, [ListChange("custom_tags", "replace", "New one", "New two")], NoRefusals);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(["New one", "New two"], await TagsOfAsync(show.Episodes[0].WorkId));
        Assert.Equal(["New one", "New two"], await TagsOfAsync(show.Episodes[1].WorkId));
    }

    [Fact]
    public async Task Apply_ReplaceTagsWithNothing_LeavesAnExplicitlyEmptyList()
    {
        var show = SeedShow(episodeCount: 1);
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = "Old" });

        await CreateService().ApplyAsync(show.EpisodeIds, [ListChange("custom_tags", "replace")], NoRefusals);

        var overrides = await OverridesOfAsync(show.Episodes[0].WorkId);
        Assert.Equal(string.Empty, overrides["custom_tags"]);
    }

    [Fact]
    public async Task Apply_RemoveFromAList_TakesTheValueOffAndKeepsTheRest()
    {
        var show = SeedShow(episodeCount: 2);
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = "Keep; Drop" });
        await SetOverridesAsync(show.Episodes[1].WorkId, new() { ["custom_tags"] = "Drop" });

        var result = await CreateService().ApplyAsync(
            show.EpisodeIds, [ListChange("custom_tags", "remove", "drop")], NoRefusals);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(["Keep"], await TagsOfAsync(show.Episodes[0].WorkId));
        Assert.Empty(await TagsOfAsync(show.Episodes[1].WorkId));
    }

    // ── Apply: scalar fields and "No change" ────────────────────────────────

    [Fact]
    public async Task Apply_ScalarSetAndClear_WriteAndRevertTheOverride()
    {
        var show = SeedShow(episodeCount: 2);
        await SetOverridesAsync(show.Episodes[1].WorkId, new() { ["language"] = "fr" });

        var set = await CreateService().ApplyAsync(show.EpisodeIds, [ScalarChange("rating", "set", "8.50")], NoRefusals);
        var clear = await CreateService().ApplyAsync(show.EpisodeIds, [ScalarChange("language", "clear")], NoRefusals);

        Assert.Equal(2, set.UpdatedCount);
        Assert.Equal(2, clear.UpdatedCount);
        var first = await OverridesOfAsync(show.Episodes[0].WorkId);
        var second = await OverridesOfAsync(show.Episodes[1].WorkId);
        Assert.Equal("8.5", first["rating"]);
        Assert.Equal("8.5", second["rating"]);
        Assert.DoesNotContain("language", second.Keys);
    }

    [Fact]
    public async Task Apply_FieldsThatWereNotSent_AreLeftExactlyAsTheyWere()
    {
        var show = SeedShow(episodeCount: 2);
        foreach (var episode in show.Episodes)
        {
            await SetOverridesAsync(episode.WorkId, new() { ["rating"] = "7", ["language"] = "en", ["custom_tags"] = "Keep" });
        }

        await CreateService().ApplyAsync(show.EpisodeIds, [ListChange("custom_tags", "add", "Extra")], NoRefusals);

        foreach (var episode in show.Episodes)
        {
            var overrides = await OverridesOfAsync(episode.WorkId);
            Assert.Equal("7", overrides["rating"]);
            Assert.Equal("en", overrides["language"]);
        }
    }

    [Fact]
    public async Task Apply_WhenAnItemAlreadyMatches_CountsItUpdatedButWritesAndLogsNothing()
    {
        var show = SeedShow(episodeCount: 1);
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = "Same" });

        var result = await CreateService().ApplyAsync(show.EpisodeIds, [ListChange("custom_tags", "add", "same")], NoRefusals);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Empty(await ActivityForAsync(show.Episodes[0].WorkId));
    }

    // ── Apply: one request, per-row outcome, Needs Review ───────────────────

    [Fact]
    public async Task Apply_ThreeEpisodesInOneRequest_UpdatesAllThreeAndLogsOneActivityEntryEach()
    {
        var show = SeedShow(episodeCount: 3);

        var result = await CreateService().ApplyAsync(show.EpisodeIds, [ListChange("custom_tags", "add", "Binge")], NoRefusals);

        Assert.Equal(3, result.UpdatedCount);
        Assert.Equal(0, result.SentToReviewCount);
        Assert.All(result.Items, item => Assert.Equal(LibraryBatchItemOutcomes.Updated, item.Outcome));
        foreach (var episode in show.Episodes)
        {
            Assert.Equal(["Binge"], await TagsOfAsync(episode.WorkId));
            var entry = Assert.Single(await ActivityForAsync(episode.WorkId));
            Assert.Contains("custom_tags", entry.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Apply_WhenOneEpisodeCannotTakeAGenre_TwoAreUpdatedAndThatOneIsSentToNeedsReview()
    {
        var show = SeedShow(episodeCount: 3);
        SeedGenre(show.SeasonId, "Drama");
        SeedGenre(show.Episodes[0].AssetId, "Comedy");
        SeedGenre(show.Episodes[1].AssetId, "Comedy");
        // The third episode only inherits "Drama" from its season, so the pencil would lock Genres for it.
        var locked = show.Episodes[2];

        var result = await CreateService().ApplyAsync(
            show.EpisodeIds, [ListChange("genre", "add", "Thriller"), ListChange("custom_tags", "add", "Binge")], NoRefusals);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(1, result.SentToReviewCount);
        var row = Assert.Single(result.Items, item => item.Outcome == LibraryBatchItemOutcomes.SentToReview);
        Assert.Equal(locked.WorkId, row.EntityId);
        Assert.Equal(["genre", "custom_tags"], row.FieldsNotApplied);
        Assert.Contains("Genres", row.Reason, StringComparison.Ordinal);
        Assert.Contains("parent", row.Reason, StringComparison.Ordinal);

        var review = await _reviews.GetByIdAsync(row.ReviewId!.Value);
        Assert.NotNull(review);
        Assert.Equal(locked.AssetId, review.EntityId);
        Assert.Equal(ReviewTrigger.MetadataConflict, review.Trigger);
        Assert.Equal(ReviewStatus.Pending, review.Status);
        Assert.NotNull(review.ReviewReadyAt);
        Assert.Contains("Episode 3", review.Detail, StringComparison.Ordinal);
        Assert.Contains("Genres", review.Detail, StringComparison.Ordinal);
        Assert.Contains("Tags", review.Detail, StringComparison.Ordinal);

        // An item is applied together or not at all: the failing one changed in no field, the two others took every change.
        Assert.Empty(await OverridesOfAsync(locked.WorkId));
        foreach (var applied in show.Episodes.Take(2))
        {
            var overrides = await OverridesOfAsync(applied.WorkId);
            Assert.Equal("Comedy, Thriller", overrides["genre"]);
            Assert.Equal("Binge", overrides["custom_tags"]);
        }
    }

    [Fact]
    public async Task Apply_WhenSavingOneItemFails_ThatItemGoesToNeedsReviewAndTheOthersStillSave()
    {
        var show = SeedShow(episodeCount: 3);
        var failing = show.Episodes[1];
        var service = CreateService(new FailingSaveRepository(_items, failing.WorkId));

        var result = await service.ApplyAsync(show.EpisodeIds, [ListChange("custom_tags", "add", "Binge")], NoRefusals);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(1, result.SentToReviewCount);
        var row = Assert.Single(result.Items, item => item.Outcome == LibraryBatchItemOutcomes.SentToReview);
        Assert.Equal(failing.WorkId, row.EntityId);
        var review = await _reviews.GetByIdAsync(row.ReviewId!.Value);
        Assert.Equal(failing.AssetId, review!.EntityId);
        Assert.Empty(await OverridesOfAsync(failing.WorkId));
        Assert.Equal(["Binge"], await TagsOfAsync(show.Episodes[0].WorkId));
        Assert.Equal(["Binge"], await TagsOfAsync(show.Episodes[2].WorkId));
    }

    [Fact]
    public async Task Apply_WhenAStoredValueNoLongerFitsTheLimits_SendsThatItemToNeedsReview()
    {
        var show = SeedShow(episodeCount: 2);
        var full = string.Join("; ", Enumerable.Range(1, LibraryTagCatalog.MaximumTags).Select(i => $"tag{i}"));
        await SetOverridesAsync(show.Episodes[0].WorkId, new() { ["custom_tags"] = full });

        var result = await CreateService().ApplyAsync(show.EpisodeIds, [ListChange("custom_tags", "add", "one too many")], NoRefusals);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.SentToReviewCount);
        var row = Assert.Single(result.Items, item => item.Outcome == LibraryBatchItemOutcomes.SentToReview);
        Assert.Equal(show.Episodes[0].WorkId, row.EntityId);
        Assert.Contains("at most", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ForAnItemThatIsGone_RefusesItWithoutAReviewEntryAndStillUpdatesTheRest()
    {
        var show = SeedShow(episodeCount: 2);
        var gone = Guid.NewGuid();

        var result = await CreateService().ApplyAsync(
            [.. show.EpisodeIds, gone], [ListChange("custom_tags", "add", "Binge")], NoRefusals);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(0, result.SentToReviewCount);
        Assert.Equal(1, result.RefusedCount);
        var row = Assert.Single(result.Items, item => item.Outcome == LibraryBatchItemOutcomes.Refused);
        Assert.Equal(gone, row.EntityId);
        Assert.Contains("no longer", row.Reason, StringComparison.Ordinal);
        Assert.Empty(await _reviews.GetPendingAsync(50));
    }

    [Fact]
    public async Task Apply_ForAnItemTheCallerMayNotEdit_RefusesThatRowByNameAndLeavesItUntouched()
    {
        var show = SeedShow(episodeCount: 3);
        var refusedId = show.Episodes[2].WorkId;

        var result = await CreateService().ApplyAsync(
            show.EpisodeIds,
            [ListChange("custom_tags", "add", "Binge")],
            new Dictionary<Guid, string> { [refusedId] = "You are not allowed to edit this item." });

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(1, result.RefusedCount);
        var row = Assert.Single(result.Items, item => item.Outcome == LibraryBatchItemOutcomes.Refused);
        Assert.Equal(refusedId, row.EntityId);
        Assert.Equal("You are not allowed to edit this item.", row.Reason);
        Assert.Empty(await OverridesOfAsync(refusedId));
    }

    [Fact]
    public async Task Apply_WhenAnItemAndItsFileAreBothSelected_WritesTheWorkOnceAndReportsBothRows()
    {
        var show = SeedShow(episodeCount: 1);
        var episode = show.Episodes[0];

        var result = await CreateService().ApplyAsync(
            [episode.WorkId, episode.AssetId], [ListChange("custom_tags", "add", "Binge")], NoRefusals);

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(LibraryBatchItemOutcomes.Updated, item.Outcome));
        Assert.Equal(["Binge"], await TagsOfAsync(episode.WorkId));
        Assert.Single(await ActivityForAsync(episode.WorkId));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private LibraryBatchEditService CreateService(IItemCanonicalRepository? items = null) => new(
        new LibraryCurationReadService(_db),
        items ?? _items,
        _reviews,
        _activity,
        NullLogger<LibraryBatchEditService>.Instance);

    private static BatchEditChange ListChange(string key, string op, params string[] values)
    {
        Assert.True(BatchEditFieldCatalog.TryValidateChange(key, op, null, values, out var change, out var error), error);
        return change;
    }

    private static BatchEditChange ScalarChange(string key, string op, string? value = null)
    {
        Assert.True(BatchEditFieldCatalog.TryValidateChange(key, op, value, null, out var change, out var error), error);
        return change;
    }

    private async Task SetOverridesAsync(Guid workId, Dictionary<string, string> overrides)
    {
        Assert.True(await _items.SaveDisplayOverridesAsync(workId, overrides));
    }

    private async Task<Dictionary<string, string>> OverridesOfAsync(Guid workId) =>
        new((await _items.LoadDisplayOverridesAsync(workId)).Values, StringComparer.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>> TagsOfAsync(Guid workId) =>
        (await OverridesOfAsync(workId)).TryGetValue("custom_tags", out var tags)
            ? BatchEditFieldCatalog.ParseList(tags)
            : [];

    private async Task<IReadOnlyList<MediaEngine.Domain.Entities.SystemActivityEntry>> ActivityForAsync(Guid workId) =>
        (await _activity.GetRecentAsync(200))
            .Where(entry => entry.EntityId == workId && entry.ActionType == SystemActionType.MetadataManualOverride)
            .ToList();

    private void SeedGenre(Guid ownerId, string genre)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO canonical_value_arrays (entity_id, key, value, ordinal) VALUES (@ownerId, 'genre', @genre, 0);",
            new { ownerId = GuidSql.ToBlob(ownerId), genre });
    }

    private SeededShow SeedShow(int episodeCount)
    {
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodes = new List<SeededEpisode>();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@showId, 'TV', 'parent');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal) VALUES (@seasonId, @showId, 'TV', 'parent', 1);
            """,
            new { showId = GuidSql.ToBlob(showId), seasonId = GuidSql.ToBlob(seasonId) });
        for (var number = 1; number <= episodeCount; number++)
        {
            var workId = Guid.NewGuid();
            var editionId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            conn.Execute(
                """
                INSERT INTO works (id, parent_work_id, media_type, work_kind, ordinal) VALUES (@workId, @seasonId, 'TV', 'child', @number);
                INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @path);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, datetime('now'));
                """,
                new
                {
                    workId = GuidSql.ToBlob(workId),
                    seasonId = GuidSql.ToBlob(seasonId),
                    editionId = GuidSql.ToBlob(editionId),
                    assetId = GuidSql.ToBlob(assetId),
                    number,
                    hash = $"hash-{assetId:N}",
                    path = $"C:/library/tv/{assetId:N}.mkv",
                    title = $"Episode {number}",
                });
            episodes.Add(new SeededEpisode(workId, assetId));
        }

        return new SeededShow(showId, seasonId, episodes);
    }

    private sealed record SeededEpisode(Guid WorkId, Guid AssetId);

    private sealed record SeededShow(Guid ShowId, Guid SeasonId, IReadOnlyList<SeededEpisode> Episodes)
    {
        public List<Guid> EpisodeIds => Episodes.Select(episode => episode.WorkId).ToList();
    }

    /// <summary>Hand-written spy: behaves like the real repository except that saving one chosen work fails.</summary>
    private sealed class FailingSaveRepository(IItemCanonicalRepository inner, Guid failingWorkId) : IItemCanonicalRepository
    {
        public Task<bool> SaveDisplayOverridesAsync(Guid workId, IReadOnlyDictionary<string, string> overrides, CancellationToken ct = default) =>
            workId == failingWorkId
                ? throw new InvalidOperationException("database is locked")
                : inner.SaveDisplayOverridesAsync(workId, overrides, ct);

        public Task<ItemCanonicalWorkAssetContext?> ResolveWorkAssetContextAsync(Guid entityId, CancellationToken ct = default) => inner.ResolveWorkAssetContextAsync(entityId, ct);
        public Task<ItemCanonicalDisplayOverrideState> LoadDisplayOverridesAsync(Guid workId, CancellationToken ct = default) => inner.LoadDisplayOverridesAsync(workId, ct);
        public Task<Guid?> ResolveWorkIdForAssetAsync(Guid assetId, CancellationToken ct = default) => inner.ResolveWorkIdForAssetAsync(assetId, ct);
        public Task<ItemCanonicalWorkWikidataState?> LoadWorkWikidataStateAsync(Guid workId, CancellationToken ct = default) => inner.LoadWorkWikidataStateAsync(workId, ct);
        public Task UpdateWorkIdentityAsync(Guid workId, string wikidataQid, CancellationToken ct = default) => inner.UpdateWorkIdentityAsync(workId, wikidataQid, ct);
        public Task DeleteIdentityArtifactsAsync(IReadOnlyCollection<ItemCanonicalIdentityArtifact> artifacts, CancellationToken ct = default) => inner.DeleteIdentityArtifactsAsync(artifacts, ct);
        public Task ReplaceExternalIdentifiersAsync(Guid workId, IReadOnlyCollection<string> keysToRemove, IReadOnlyDictionary<string, string> replacements, CancellationToken ct = default) => inner.ReplaceExternalIdentifiersAsync(workId, keysToRemove, replacements, ct);
        public Task<string> AppendRejectedQidAsync(Guid workId, string? rejectedQid, CancellationToken ct = default) => inner.AppendRejectedQidAsync(workId, rejectedQid, ct);
    }
}
