using MediaEngine.Domain.Models;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Phase 4 — integration tests for lineage-aware readers. Builds a real
/// SQLite hierarchy (show → season → episode → edition → asset) and verifies
/// that:
///   • LibraryItemRepository.GetPageAsync reads self-scope fields from the asset
///     row and parent-scope fields from the topmost Work row.
///   • SearchIndexRepository.UpsertByEntityIdAsync self-fetches title from
///     the asset row and author/description from the topmost Work row.
///   • CollectionRuleEvaluator's CvLookup union finds works whose value lives on
///     either lineage row.
/// </summary>
public sealed class LineageReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public LineageReaderTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_lineage_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    // ── LibraryItemRepository.GetPageAsync ────────────────────────────────────

    [Fact]
    public async Task LibraryItemGetPage_ReadsTitleFromAsset_AndAuthorFromRootParent()
    {
        // Build a TV hierarchy: show → season → episode → edition → asset
        var (showId, _, episodeId, assetId) = await BuildTvHierarchyAsync();

        // Self-scope: episode title on the asset row.
        await InsertCanonicalAsync(assetId, "title", "Hide and Seek");
        // Parent-scope: author lives on the root show Work row.
        await InsertCanonicalArrayAsync(showId, "author", "Dan Erickson");
        await InsertCanonicalArrayAsync(showId, "genre", "Sci-Fi");

        var repo = new LibraryItemRepository(_db);
        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));

        // The page returns one row per Work in the chain (show + season + episode).
        // The episode is the leaf — it has the asset-row title and inherits the
        // show-row author/genre via the lineage walk.
        var episode = page.Items.Single(i => i.EntityId == episodeId);
        Assert.Equal("Hide and Seek", episode.Title);
        Assert.Equal("Dan Erickson", episode.Author);
        Assert.Equal("Sci-Fi", episode.Genre);
    }

    [Fact]
    public async Task LibraryItemGetPage_ParentScopeAuthor_NotReadFromAssetRow()
    {
        // Verify that parent-scope fields are NOT silently picked up from the
        // asset row (no fallback). If author is on the asset, it must NOT be
        // returned because the new reader only consults the parent Work row.
        var (_, _, episodeId, assetId) = await BuildTvHierarchyAsync();

        await InsertCanonicalAsync(assetId, "title", "Pilot");
        // Intentionally write author to the WRONG row (asset).
        await InsertCanonicalArrayAsync(assetId, "author", "Wrong Place");
        // Leave the show row empty for author.

        var repo = new LibraryItemRepository(_db);
        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));

        var episode = page.Items.Single(i => i.EntityId == episodeId);
        Assert.Equal("Pilot", episode.Title);
        Assert.Null(episode.Author); // No fallback — must stay null.
    }

    [Fact]
    public async Task LibraryItemGetPage_StandaloneMovie_ReadsParentFieldsFromOwnWork()
    {
        // Standalone movie: parent collapses to self, but parent-scope fields
        // still live on the Work row, NOT the asset row.
        var (workId, _, assetId) = await BuildStandaloneWorkAsync("Movies");

        await InsertCanonicalAsync(assetId, "title", "Dune");
        await InsertCanonicalAsync(workId, "year", "2021");
        await InsertCanonicalArrayAsync(workId, "director", "Denis Villeneuve");

        var repo = new LibraryItemRepository(_db);
        var page = await repo.GetPageAsync(new LibraryItemQuery(IncludeAll: true));

        var item = page.Items.Single(i => i.EntityId == workId);
        Assert.Equal("Dune", item.Title);
        Assert.Equal("2021", item.Year);
        Assert.Equal("Denis Villeneuve", item.Director);
    }

    // ── SearchIndexRepository.UpsertByEntityIdAsync ────────────────────────

    [Fact]
    public async Task SearchIndex_Upsert_ReadsTitleFromAssetAndAuthorFromRootParent()
    {
        var (showId, _, _, assetId) = await BuildTvHierarchyAsync();

        await InsertCanonicalAsync(assetId, "title", "Hide and Seek");
        await InsertCanonicalAsync(assetId, "original_title", "Hide and Seek");
        await InsertCanonicalArrayAsync(showId, "author", "Dan Erickson");
        await InsertCanonicalAsync(showId, "description", "A workplace mystery.");

        var index = new SearchIndexRepository(_db);
        await index.UpsertByEntityIdAsync(assetId);

        // The search row is keyed on the leaf work id (the episode work).
        var (workId, title, author, description) = ReadSearchRowByAssetId(assetId);
        Assert.NotNull(workId);
        Assert.Equal("Hide and Seek", title);
        Assert.Equal("Dan Erickson", author);
        Assert.Equal("A workplace mystery.", description);
    }

    [Fact]
    public async Task SearchIndex_Upsert_AcceptsWorkIdAsEntryPoint()
    {
        // Caller may pass either an asset id or the leaf work id — both
        // resolve to the same FTS row.
        var (showId, _, episodeWorkId, assetId) = await BuildTvHierarchyAsync();

        await InsertCanonicalAsync(assetId, "title", "Hide and Seek");
        await InsertCanonicalArrayAsync(showId, "author", "Dan Erickson");

        var index = new SearchIndexRepository(_db);
        await index.UpsertByEntityIdAsync(episodeWorkId);

        var (workId, title, author, _) = ReadSearchRowByAssetId(assetId);
        Assert.Equal(episodeWorkId, workId);
        Assert.Equal("Hide and Seek", title);
        Assert.Equal("Dan Erickson", author);
    }

    // ── CollectionRuleEvaluator (CvLookup union over self+parent) ─────────────────

    [Fact]
    public async Task CollectionRule_CvLookup_FindsValueOnAssetRow()
    {
        var (_, _, episodeWorkId, assetId) = await BuildTvHierarchyAsync();
        await InsertCanonicalAsync(assetId, "year", "2022");

        // Drop a noise standalone work that should NOT match.
        await BuildStandaloneWorkAsync("Books");

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "year", Op = "eq", Value = "2022" }]);

        // The episode work is found via the asset-row (Self) lookup path.
        Assert.Contains(episodeWorkId, matches);
    }

    [Fact]
    public async Task CollectionRepository_ReturnsAssetWorkLineage()
    {
        var (showId, seasonId, episodeWorkId, assetId) = await BuildTvHierarchyBlobAsync();

        var repo = new CollectionRepository(_db);
        var lineage = await repo.GetWorkLineageIdsByMediaAssetAsync(assetId);

        Assert.Equal(new[] { episodeWorkId, seasonId, showId }, lineage);
    }

    [Fact]
    public async Task CollectionRule_Evaluate_ReadsGuidBlobWorkIds()
    {
        var (_, _, episodeWorkId, _) = await BuildTvHierarchyBlobAsync();

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "TV" }]);

        Assert.Contains(episodeWorkId, matches);
    }

    [Fact]
    public async Task CollectionRule_CvLookup_FindsBlobValueOnRootParentRow()
    {
        var (showId, _, episodeWorkId, _) = await BuildTvHierarchyBlobAsync();
        await InsertCanonicalBlobAsync(showId, "show_name", "Severance");

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "show_name", Op = "eq", Value = "Severance" }]);

        Assert.Contains(episodeWorkId, matches);
    }

    [Fact]
    public async Task CollectionRule_CvLookup_FindsValueOnRootParentRow()
    {
        // Same predicate, but the value lives on the parent show Work id.
        var (showId, _, episodeWorkId, _) = await BuildTvHierarchyAsync();
        await InsertCanonicalArrayAsync(showId, "genre", "Sci-Fi");

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "genre", Op = "eq", Value = "Sci-Fi" }]);

        // The episode work is found via the parent-scope path (Parent lookup
        // walks parent_work_id up two levels and finds the show row).
        Assert.Contains(episodeWorkId, matches);
    }

    [Fact]
    public async Task CollectionRule_PersonQid_FollowsPrimaryCanonicalCreditToOwnedWork()
    {
        var (workId, _, assetId) = await BuildStandaloneWorkAsync("Movies");
        var personRepo = new PersonRepository(_db);
        var person = await personRepo.CreateAsync(new MediaEngine.Domain.Entities.Person
        {
            Id = Guid.NewGuid(),
            Name = "Christopher Nolan",
            WikidataQid = "Q25191",
        });
        await personRepo.LinkToMediaAssetAsync(assetId, person.Id, "Director");
        using (var conn = _db.CreateConnection())
        {
            using (var command = conn.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                    VALUES ($workId, 'director', 0, $name, $qid);
                    """;
                command.Parameters.AddWithValue("$workId", GuidSql.ToBlob(workId));
                command.Parameters.AddWithValue("$name", person.Name);
                command.Parameters.AddWithValue("$qid", person.WikidataQid);
                command.ExecuteNonQuery();
            }
        }

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "person_qid", Op = "eq", Value = "Q25191" }]);

        Assert.Contains(workId, matches);
    }

    [Fact]
    public async Task CollectionRule_EntityFieldsMatchQidAndNotDisplayLabel()
    {
        var (workId, _, _) = await BuildStandaloneWorkAsync("Movies");
        await InsertCanonicalEntityArrayAsync(workId, "award_received", "Academy Award for Best Picture", "Q102427");

        var evaluator = new CollectionRuleEvaluator(_db);
        Assert.Contains(workId, evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "award_received", Op = "eq", Value = "Q102427", DisplayValue = "Best Picture" }]));
        Assert.DoesNotContain(workId, evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "award_received", Op = "eq", Value = "Academy Award for Best Picture" }]));
    }

    [Fact]
    public async Task CollectionRule_InOperatorMatchesAnySelectedCanonicalIdentifier()
    {
        var (bestPictureWinner, _, _) = await BuildStandaloneWorkAsync("Movies");
        var (bestActorWinner, _, _) = await BuildStandaloneWorkAsync("Movies");
        var (otherWinner, _, _) = await BuildStandaloneWorkAsync("Movies");
        await InsertCanonicalEntityArrayAsync(bestPictureWinner, "award_received", "Academy Award for Best Picture", "Q102427");
        await InsertCanonicalEntityArrayAsync(bestActorWinner, "award_received", "Academy Award for Best Actor", "Q103916");
        await InsertCanonicalEntityArrayAsync(otherWinner, "award_received", "Palme d'Or", "Q179808");

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate([
            new CollectionRulePredicate
            {
                Field = "award_received",
                Op = "in",
                Values = ["Q102427", "Q103916"],
                DisplayValues = ["Academy Award for Best Picture", "Academy Award for Best Actor"],
            },
        ]);

        Assert.Contains(bestPictureWinner, matches);
        Assert.Contains(bestActorWinner, matches);
        Assert.DoesNotContain(otherWinner, matches);
    }

    [Fact]
    public async Task CollectionRule_MediaTypeInOperatorMatchesEverySelectedType()
    {
        var (movie, _, _) = await BuildStandaloneWorkAsync("Movies");
        var (book, _, _) = await BuildStandaloneWorkAsync("Books");
        var (music, _, _) = await BuildStandaloneWorkAsync("Music");

        var evaluator = new CollectionRuleEvaluator(_db);
        var matches = evaluator.Evaluate([
            new CollectionRulePredicate { Field = "media_type", Op = "in", Values = ["Movies", "Books"] },
        ]);

        Assert.Contains(movie, matches);
        Assert.Contains(book, matches);
        Assert.DoesNotContain(music, matches);
    }

    [Fact]
    public async Task CollectionRule_Evaluate_AppliesPersistedSortFieldAndDirection()
    {
        var (alpha, _, alphaAsset) = await BuildStandaloneWorkAsync("Movies");
        var (bravo, _, bravoAsset) = await BuildStandaloneWorkAsync("Movies");
        var (charlie, _, charlieAsset) = await BuildStandaloneWorkAsync("Movies");

        await InsertCanonicalAsync(alphaAsset, "title", "Alpha");
        await InsertCanonicalAsync(bravoAsset, "title", "Bravo");
        await InsertCanonicalAsync(charlieAsset, "title", "Charlie");
        await InsertCanonicalAsync(alpha, "year", "2020");
        await InsertCanonicalAsync(bravo, "year", "2000");
        await InsertCanonicalAsync(charlie, "year", "2010");
        await InsertCanonicalAsync(alpha, "rating", "3.5");
        await InsertCanonicalAsync(bravo, "rating", "4.8");
        await InsertCanonicalAsync(charlie, "rating", "4.2");

        var evaluator = new CollectionRuleEvaluator(_db);
        var definition = CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }]);

        Assert.Equal([bravo, charlie, alpha], evaluator.Evaluate(definition, "provider_rating", "desc"));
        Assert.Equal([bravo, charlie, alpha], evaluator.Evaluate(definition, "release_date", "asc"));
        Assert.Equal([charlie, bravo, alpha], evaluator.Evaluate(definition, "title", "desc"));
    }

    [Fact]
    public async Task CollectionRule_Evaluate_AppliesSecondarySortAsResultTieBreaker()
    {
        var (alpha, _, alphaAsset) = await BuildStandaloneWorkAsync("Movies");
        var (zebra, _, zebraAsset) = await BuildStandaloneWorkAsync("Movies");
        var (newer, _, newerAsset) = await BuildStandaloneWorkAsync("Movies");
        await InsertCanonicalAsync(alphaAsset, "title", "Alpha");
        await InsertCanonicalAsync(zebraAsset, "title", "Zebra");
        await InsertCanonicalAsync(newerAsset, "title", "Beta");
        await InsertCanonicalAsync(alpha, "year", "2020");
        await InsertCanonicalAsync(zebra, "year", "2020");
        await InsertCanonicalAsync(newer, "year", "2021");

        var evaluator = new CollectionRuleEvaluator(_db);
        var definition = CollectionRuleDefinition.SingleGroup(
            [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }]);

        var matches = evaluator.Evaluate(
            definition,
            "year",
            "desc",
            secondarySortField: "title",
            secondarySortDirection: "desc");

        Assert.Equal([newer, zebra, alpha], matches);
        Assert.Equal(
            CollectionRuleEvaluator.ComputeRuleHash(definition),
            CollectionRuleEvaluator.ComputeRuleHash(definition));
    }

    [Fact]
    public async Task CollectionRule_Evaluate_UsesPersistedRelationshipBetweenGroups()
    {
        var (scienceMovie, _, scienceMovieAsset) = await BuildStandaloneWorkAsync("Movies");
        var (dramaMovie, _, dramaMovieAsset) = await BuildStandaloneWorkAsync("Movies");
        var (scienceBook, _, scienceBookAsset) = await BuildStandaloneWorkAsync("Books");
        await InsertCanonicalArrayAsync(scienceMovieAsset, "genre", "Science Fiction");
        await InsertCanonicalArrayAsync(dramaMovieAsset, "genre", "Drama");
        await InsertCanonicalArrayAsync(scienceBookAsset, "genre", "Science Fiction");

        var definition = new CollectionRuleDefinition
        {
            Groups =
            [
                new CollectionRuleGroup
                {
                    MatchMode = "all",
                    Conditions = [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }],
                },
                new CollectionRuleGroup
                {
                    JoinWithPrevious = "and",
                    MatchMode = "all",
                    Conditions = [new CollectionRulePredicate { Field = "genre", Op = "eq", Value = "Science Fiction" }],
                },
            ],
        };

        var matches = new CollectionRuleEvaluator(_db).Evaluate(definition, "title", "asc");

        Assert.Equal([scienceMovie], matches);
        Assert.DoesNotContain(dramaMovie, matches);
        Assert.DoesNotContain(scienceBook, matches);
    }

    [Fact]
    public async Task CollectionRule_NotEqualRequiresKnownValueAndUnknownIsExplicit()
    {
        var (winner, _, _) = await BuildStandaloneWorkAsync("Movies");
        var (differentWinner, _, _) = await BuildStandaloneWorkAsync("Movies");
        var (unknown, _, _) = await BuildStandaloneWorkAsync("Movies");
        await InsertCanonicalEntityArrayAsync(winner, "award_received", "Best Picture", "Q102427");
        await InsertCanonicalEntityArrayAsync(differentWinner, "award_received", "Palme d'Or", "Q179808");
        await InsertDiscoveryCapabilityAsync(unknown, "award_received", "no_result");

        var evaluator = new CollectionRuleEvaluator(_db);
        var notBestPicture = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "award_received", Op = "neq", Value = "Q102427" }]);
        Assert.Contains(differentWinner, notBestPicture);
        Assert.DoesNotContain(winner, notBestPicture);
        Assert.DoesNotContain(unknown, notBestPicture);

        var unknownAwards = evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "award_received", Op = "unknown" }]);
        Assert.Contains(unknown, unknownAwards);
        Assert.DoesNotContain(winner, unknownAwards);
    }

    [Fact]
    public async Task CollectionRule_AdaptationOwnershipTraversesQidRelationship()
    {
        var (source, _, _) = await BuildStandaloneWorkAsync("Books");
        var (adaptation, _, _) = await BuildStandaloneWorkAsync("Movies");
        await SetWorkQidAsync(source, "Q1001");
        await SetWorkQidAsync(adaptation, "Q1002");
        await InsertCanonicalEntityArrayAsync(adaptation, "based_on", "Source novel", "Q1001");

        var evaluator = new CollectionRuleEvaluator(_db);
        Assert.Contains(adaptation, evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "source_work_owned", Op = "eq", Value = "true" }]));
        Assert.Contains(source, evaluator.Evaluate(
            [new CollectionRulePredicate { Field = "adaptation_owned", Op = "eq", Value = "true" }]));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers — schema-safe hierarchy builders
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds: collection → show Work → season Work (parent=show) → episode Work
    /// (parent=season) → edition → asset. Returns the IDs as a tuple.
    /// </summary>
    private async Task<(Guid ShowId, Guid SeasonId, Guid EpisodeId, Guid AssetId)>
        BuildTvHierarchyAsync()
        => await BuildTvHierarchyBlobAsync();

    private async Task<(Guid ShowId, Guid SeasonId, Guid EpisodeId, Guid AssetId)>
        BuildTvHierarchyBlobAsync()
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var epId = Guid.NewGuid();
        var edId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type)
                VALUES (@showId, @collectionId, 'TV');
            INSERT INTO works (id, collection_id, media_type, parent_work_id)
                VALUES (@seasonId, @collectionId, 'TV', @showId);
            INSERT INTO works (id, collection_id, media_type, parent_work_id)
                VALUES (@episodeId, @collectionId, 'TV', @seasonId);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @episodeId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, '/lib/test-blob.mkv', 'Normal');
            """;
        AddGuid(cmd, "@collectionId", collectionId);
        AddGuid(cmd, "@showId", showId);
        AddGuid(cmd, "@seasonId", seasonId);
        AddGuid(cmd, "@episodeId", epId);
        AddGuid(cmd, "@editionId", edId);
        AddGuid(cmd, "@assetId", assetId);
        cmd.Parameters.AddWithValue("@hash", $"hash_{epId:N}");
        await cmd.ExecuteNonQueryAsync();
        return (showId, seasonId, epId, assetId);
    }

    /// <summary>
    /// Builds a flat hierarchy: collection → Work (no parent) → edition → asset.
    /// Returns (workId, editionId, assetId).
    /// </summary>
    private async Task<(Guid WorkId, Guid EditionId, Guid AssetId)>
        BuildStandaloneWorkAsync(string mediaType)
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var edId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type)
                VALUES (@workId, @collectionId, @mediaType);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, '/lib/standalone.bin', 'Normal');
            """;
        AddGuid(cmd, "@collectionId", collectionId);
        AddGuid(cmd, "@workId", workId);
        AddGuid(cmd, "@editionId", edId);
        AddGuid(cmd, "@assetId", assetId);
        cmd.Parameters.AddWithValue("@mediaType", mediaType);
        cmd.Parameters.AddWithValue("@hash", $"hash_{workId:N}");
        await cmd.ExecuteNonQueryAsync();
        return (workId, edId, assetId);
    }

    private async Task InsertCanonicalAsync(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@entityId, @key, @value, datetime('now'));
            """;
        AddGuid(cmd, "@entityId", entityId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCanonicalArrayAsync(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@entityId, @key, 0, @value);
            """;
        AddGuid(cmd, "@entityId", entityId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCanonicalEntityArrayAsync(Guid entityId, string key, string value, string qid)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
            VALUES (@entityId, @key, 0, @value, @qid);
            """;
        AddGuid(cmd, "@entityId", entityId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@qid", qid);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertDiscoveryCapabilityAsync(Guid entityId, string subKey, string status)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_capability_states
                (id, entity_id, entity_kind, media_type, capability_id, capability_kind,
                 capability_version, sub_key, status, requiredness, created_at, updated_at)
            VALUES
                (@id, @entityId, 'work', 'Movies', 'enrichment.structured_discovery_metadata',
                 'enrichment', '1.0', @subKey, @status, 'optional', datetime('now'), datetime('now'));
            """;
        AddGuid(cmd, "@id", Guid.NewGuid());
        AddGuid(cmd, "@entityId", entityId);
        cmd.Parameters.AddWithValue("@subKey", subKey);
        cmd.Parameters.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SetWorkQidAsync(Guid workId, string qid)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE works SET wikidata_qid = @qid WHERE id = @workId;";
        AddGuid(cmd, "@workId", workId);
        cmd.Parameters.AddWithValue("@qid", qid);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertCanonicalBlobAsync(Guid entityId, string key, string value)
        => await InsertCanonicalAsync(entityId, key, value);

    private static void AddGuid(Microsoft.Data.Sqlite.SqliteCommand command, string name, Guid value) =>
        command.Parameters.Add(name, Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(value);

    /// <summary>
    /// Reads the FTS5 search_index row for the leaf work belonging to the
    /// given asset. Returns (workId, title, author, description).
    /// </summary>
    private (Guid? WorkId, string? Title, string? Author, string? Description)
        ReadSearchRowByAssetId(Guid assetId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT si.entity_id, si.title, si.author, si.description
            FROM search_index si
            JOIN editions e      ON 1=1
            JOIN media_assets ma ON ma.edition_id = e.id
            WHERE ma.id = @assetId
              AND si.entity_id = e.work_id
            LIMIT 1
            """;
        AddGuid(cmd, "@assetId", assetId);
        using var rdr = cmd.ExecuteReader();
        if (!rdr.Read())
        {
            return (null, null, null, null);
        }

        return (
            rdr.IsDBNull(0) ? null : GuidSql.FromDb(rdr.GetValue(0)),
            rdr.IsDBNull(1) ? null : rdr.GetString(1),
            rdr.IsDBNull(2) ? null : rdr.GetString(2),
            rdr.IsDBNull(3) ? null : rdr.GetString(3));
    }
}
