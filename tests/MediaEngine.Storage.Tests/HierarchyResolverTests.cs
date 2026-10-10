using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Tests for <see cref="HierarchyResolver"/> — the Phase 3 (M-082) service
/// that decides whether a newly-ingested file slots under an existing
/// parent Work or stands alone.
///
/// Each test uses a real SQLite database (no mocks) so the resolver,
/// repository, schema, and migrations are exercised end-to-end.
/// </summary>
public sealed class HierarchyResolverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly WorkRepository _works;
    private readonly HierarchyResolver _resolver;

    public HierarchyResolverTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_hier_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _works = new WorkRepository(_db);
        _resolver = new HierarchyResolver(_works);
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    // ── Music ─────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Audiobook_RecordingPartsShareOneWorkEvenWithinSeries()
    {
        var metadata = new Dictionary<string, string>
        {
            ["audiobook_recording_key"] = "source-folder:recording-one",
            ["title"] = "Book",
            ["author"] = "Writer",
            ["series"] = "Series",
            ["series_position"] = "2"
        };
        var first = await _resolver.ResolveAsync(MediaType.Audiobooks, metadata);
        metadata["audiobook_part_number"] = "2";
        var second = await _resolver.ResolveAsync(MediaType.Audiobooks, metadata);
        Assert.Equal(first.WorkId, second.WorkId);
        Assert.NotNull(first.ParentWorkId);
        metadata["audiobook_recording_key"] = "source-folder:other-edition";
        var other = await _resolver.ResolveAsync(MediaType.Audiobooks, metadata);
        Assert.NotEqual(first.WorkId, other.WorkId);
        var edition = Guid.NewGuid(); var asset = Guid.NewGuid();
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition, work = first.WorkId });
            conn.Execute("INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,status) VALUES(@asset,@edition,'recording-hash','recording.mp3','Normal');", new { asset, edition });
        }
        var lineage = await _works.GetLineageByAssetAsync(asset);
        Assert.Equal(first.WorkId, lineage!.TargetForParentScope);
        Assert.Equal(first.ParentWorkId, lineage.ParentWorkId);
    }

    [Fact]
    public async Task Music_ThreeTracksOfSameAlbum_ShareOneParent()
    {
        var t1 = await _resolver.ResolveAsync(MediaType.Music, Track("Pink Floyd", "Dark Side of the Moon", "Money", 6));
        var t2 = await _resolver.ResolveAsync(MediaType.Music, Track("Pink Floyd", "Dark Side of the Moon", "Time", 4));
        var t3 = await _resolver.ResolveAsync(MediaType.Music, Track("Pink Floyd", "Dark Side of the Moon", "Brain Damage", 9));

        Assert.NotNull(t1.ParentWorkId);
        Assert.Equal(t1.ParentWorkId, t2.ParentWorkId);
        Assert.Equal(t1.ParentWorkId, t3.ParentWorkId);

        Assert.Equal(WorkKind.Child, t1.WorkKind);
        Assert.Equal(6, t1.Ordinal);
        Assert.Equal(4, t2.Ordinal);
        Assert.Equal(9, t3.Ordinal);

        // All three child Work ids are distinct.
        Assert.NotEqual(t1.WorkId, t2.WorkId);
        Assert.NotEqual(t2.WorkId, t3.WorkId);
    }

    [Fact]
    public async Task Music_DifferentAlbums_GetDifferentParents()
    {
        var a = await _resolver.ResolveAsync(MediaType.Music, Track("Radiohead", "OK Computer", "Karma Police", 6));
        var b = await _resolver.ResolveAsync(MediaType.Music, Track("Radiohead", "Kid A", "Idioteque", 8));

        Assert.NotEqual(a.ParentWorkId, b.ParentWorkId);
    }

    [Fact]
    public async Task Music_UsesAlbumArtistForCompilationParentIdentity()
    {
        var collaboration = await _resolver.ResolveAsync(MediaType.Music, new Dictionary<string, string>
        {
            ["artist"] = "Queen & David Bowie",
            ["album_artist"] = "Queen",
            ["album"] = "Hot Space",
            ["title"] = "Under Pressure",
            ["track_number"] = "11",
        });
        var queenTrack = await _resolver.ResolveAsync(MediaType.Music, Track("Queen", "Hot Space", "Las Palabras de Amor", 9));

        Assert.Equal(collaboration.ParentWorkId, queenTrack.ParentWorkId);
    }

    [Fact]
    public async Task Audiobook_MultipleFilesShareParentAndRemainOrderedParts()
    {
        var first = await _resolver.ResolveAsync(MediaType.Audiobooks, AudiobookPart("Andy Weir", "Project Hail Mary", "Part 01", 1, 3));
        var second = await _resolver.ResolveAsync(MediaType.Audiobooks, AudiobookPart("Andy Weir", "Project Hail Mary", "Part 02", 2, 3));

        Assert.Equal(WorkKind.Child, first.WorkKind);
        Assert.Equal(first.ParentWorkId, second.ParentWorkId);
        Assert.Equal(1, first.Ordinal);
        Assert.Equal(2, second.Ordinal);
    }

    [Fact]
    public async Task Maintenance_RemovesEmptyAutoParentsAndDerivedArtwork_WhenLastChildDeleted()
    {
        var resolved = await _resolver.ResolveAsync(
            MediaType.TV,
            new Dictionary<string, string>
            {
                ["show_name"] = "Game of Thrones",
                ["season_number"] = "1",
                ["episode_number"] = "1",
            });
        var maintenance = new WorkHierarchyMaintenanceService(_db);

        using (var conn = _db.CreateConnection())
        {
            var showId = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_key = 'game of thrones'");
            conn.Execute("""
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, aspect_class, asset_class, storage_location, owner_scope, is_preferred, is_user_override, created_at)
                VALUES (@id, @entityId, 'Work', 'CoverArt', 'Poster', 'Artwork', 'Central', 'Generated', 1, 0, @now);
                """,
                new { id = Guid.NewGuid(), entityId = showId, now = DateTimeOffset.UtcNow.ToString("O") });
            conn.Execute("DELETE FROM works WHERE id = @id;", new { id = resolved.WorkId });
        }

        var removed = await maintenance.CleanupEmptyParentsAsync();

        using (var conn = _db.CreateConnection())
        {
            Assert.Equal(2, removed);
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
            Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets;"));
        }
    }

    [Fact]
    public async Task Maintenance_PreservesEmptyParent_WithUserOverrideArtwork()
    {
        var resolved = await _resolver.ResolveAsync(
            MediaType.Music,
            new Dictionary<string, string>
            {
                ["album"] = "OK Computer",
                ["artist"] = "Radiohead",
                ["track_number"] = "6",
            });
        var maintenance = new WorkHierarchyMaintenanceService(_db);

        using (var conn = _db.CreateConnection())
        {
            var albumId = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_key = 'radiohead|ok computer'");
            conn.Execute("""
                INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, aspect_class, asset_class, storage_location, owner_scope, is_preferred, is_user_override, created_at)
                VALUES (@id, @entityId, 'Work', 'CoverArt', 'Square', 'Artwork', 'Central', 'User', 1, 1, @now);
                """,
                new { id = Guid.NewGuid(), entityId = albumId, now = DateTimeOffset.UtcNow.ToString("O") });
            conn.Execute("DELETE FROM works WHERE id = @id;", new { id = resolved.WorkId });
        }

        var removed = await maintenance.CleanupEmptyParentsAsync();

        using (var conn = _db.CreateConnection())
        {
            Assert.Equal(0, removed);
            Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent';"));
            Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_assets;"));
        }
    }

    [Theory]
    [InlineData("saved")]
    [InlineData("reaction")]
    [InlineData("preferences")]
    [InlineData("sequence")]
    public async Task Maintenance_PreservesEmptyAlbumReferencedByProfile(string reference)
    {
        var resolved = await _resolver.ResolveAsync(
            MediaType.Music,
            new Dictionary<string, string>
            {
                ["album"] = "OK Computer",
                ["artist"] = "Radiohead",
                ["track_number"] = "6",
            });
        var maintenance = new WorkHierarchyMaintenanceService(_db);

        using (var conn = _db.CreateConnection())
        {
            var album = conn.QuerySingle<(Guid Id, string ParentKey)>(
                "SELECT id AS Id, parent_key AS ParentKey FROM works WHERE parent_key = 'radiohead|ok computer'");
            var profileId = Guid.NewGuid();
            conn.Execute("INSERT INTO profiles (id, display_name, role, created_at) VALUES (@profileId, 'Listener', 'StandardUser', @now);",
                new { profileId, now = DateTimeOffset.UtcNow.ToString("O") });
            switch (reference)
            {
                case "saved":
                    conn.Execute("INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profileId, 'Album', @albumId, @now);",
                        new { profileId, albumId = album.Id, now = DateTimeOffset.UtcNow.ToString("O") });
                    break;
                case "reaction":
                    conn.Execute("INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@profileId, 'Album', @albumId, 'Like', @now);",
                        new { profileId, albumId = album.Id, now = DateTimeOffset.UtcNow.ToString("O") });
                    break;
                case "preferences":
                    conn.Execute("INSERT INTO profile_work_preferences (profile_id, work_id, updated_at) VALUES (@profileId, @albumId, @now);",
                        new { profileId, albumId = album.Id, now = DateTimeOffset.UtcNow.ToString("O") });
                    break;
                case "sequence":
                    conn.Execute("INSERT INTO profile_sequence_preferences (profile_id, media_type, container_key, show_missing, updated_at) VALUES (@profileId, 'Music', @parentKey, 1, @now);",
                        new { profileId, parentKey = album.ParentKey, now = DateTimeOffset.UtcNow.ToString("O") });
                    break;
            }
            conn.Execute("DELETE FROM works WHERE id = @id;", new { id = resolved.WorkId });
        }

        var removed = await maintenance.CleanupEmptyParentsAsync();

        using var verification = _db.CreateConnection();
        Assert.Equal(0, removed);
        Assert.Equal(1, verification.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent';"));
    }

    [Fact]
    public async Task Maintenance_PreservesEmptyAlbumWithPreferredManagedArtwork()
    {
        var resolved = await _resolver.ResolveAsync(
            MediaType.Music,
            new Dictionary<string, string>
            {
                ["album"] = "OK Computer",
                ["artist"] = "Radiohead",
                ["track_number"] = "6",
            });
        var maintenance = new WorkHierarchyMaintenanceService(_db);

        using (var conn = _db.CreateConnection())
        {
            var albumId = conn.QuerySingle<Guid>("SELECT id FROM works WHERE parent_key = 'radiohead|ok computer'");
            var artworkId = Guid.NewGuid();
            conn.Execute("INSERT INTO artwork_assets (id, content_hash) VALUES (@artworkId, @hash);",
                new { artworkId, hash = $"art-{artworkId:N}" });
            conn.Execute("""
                INSERT INTO entity_artwork_links
                    (id, entity_id, entity_type, artwork_asset_id, role, is_preferred, is_user_override)
                VALUES (@linkId, @albumId, 'Work', @artworkId, 'Primary', 1, 1);
                """, new { linkId = Guid.NewGuid(), albumId, artworkId });
            conn.Execute("DELETE FROM works WHERE id = @id;", new { id = resolved.WorkId });
        }

        var removed = await maintenance.CleanupEmptyParentsAsync();

        using var verification = _db.CreateConnection();
        Assert.Equal(0, removed);
        Assert.Equal(1, verification.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent';"));
        Assert.Equal(1, verification.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_artwork_links;"));
    }

    [Fact]
    public async Task Music_SameTrackTwice_IsIdempotent()
    {
        var first = await _resolver.ResolveAsync(MediaType.Music, Track("Radiohead", "OK Computer", "Karma Police", 6));
        var second = await _resolver.ResolveAsync(MediaType.Music, Track("Radiohead", "OK Computer", "Karma Police", 6));

        Assert.Equal(first.WorkId, second.WorkId);
        Assert.Equal(first.ParentWorkId, second.ParentWorkId);
        Assert.True(first.NewlyCreated);
        Assert.False(second.NewlyCreated);
    }

    [Fact]
    public async Task Music_MultiDiscTracks_DoNotCollideOnTrackNumberOnly()
    {
        var discOne = await _resolver.ResolveAsync(MediaType.Music,
            Track("Various Artists", "Anthology", "Opening", 1, discNumber: 1));
        var discTwo = await _resolver.ResolveAsync(MediaType.Music,
            Track("Various Artists", "Anthology", "Encore", 1, discNumber: 2));

        Assert.Equal(discOne.ParentWorkId, discTwo.ParentWorkId);
        Assert.NotEqual(discOne.WorkId, discTwo.WorkId);

        using var conn = _db.CreateConnection();
        var sorts = conn.Query<double>(
            "SELECT ordinal_sort FROM works WHERE parent_work_id = @parent ORDER BY ordinal_sort;",
            new { parent = discOne.ParentWorkId })
            .ToList();

        Assert.Equal([1d, 2001d], sorts);
    }

    [Fact]
    public async Task Music_DiacriticDifferences_NormalizeToSameParent()
    {
        // Note: only true diacritics (combining marks) are normalised away.
        // Ligatures like æ are distinct letters and remain as-is.
        var a = await _resolver.ResolveAsync(MediaType.Music, Track("Beyoncé", "Renaissance", "Cuff It", 3));
        var b = await _resolver.ResolveAsync(MediaType.Music, Track("Beyonce", "Renaissance", "Break My Soul", 6));

        Assert.Equal(a.ParentWorkId, b.ParentWorkId);
    }

    [Fact]
    public async Task Music_NoAlbum_FallsBackToStandalone()
    {
        var loose = await _resolver.ResolveAsync(MediaType.Music, new Dictionary<string, string>
        {
            ["title"] = "Untitled Demo",
            ["artist"] = "Some Artist",
        });

        Assert.Equal(WorkKind.Standalone, loose.WorkKind);
        Assert.Null(loose.ParentWorkId);
    }

    // ── TV ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TV_ThreeEpisodes_BuildsShowSeasonEpisodeHierarchy()
    {
        var e1 = await _resolver.ResolveAsync(MediaType.TV, Episode("Breaking Bad", 1, 1, "Pilot"));
        var e2 = await _resolver.ResolveAsync(MediaType.TV, Episode("Breaking Bad", 1, 2, "Cat's in the Bag..."));
        var e3 = await _resolver.ResolveAsync(MediaType.TV, Episode("Breaking Bad", 2, 1, "Seven Thirty-Seven"));

        // e1 and e2 share Season 1 parent.
        Assert.NotNull(e1.ParentWorkId);
        Assert.Equal(e1.ParentWorkId, e2.ParentWorkId);

        // Season 2 is a different parent.
        Assert.NotEqual(e1.ParentWorkId, e3.ParentWorkId);

        Assert.Equal(WorkKind.Child, e1.WorkKind);
        Assert.Equal(1, e1.Ordinal);
        Assert.Equal(2, e2.Ordinal);
        Assert.Equal(1, e3.Ordinal);
    }

    [Fact]
    public async Task TV_TwoShowsWithSeasonOne_StayDistinct()
    {
        var bb = await _resolver.ResolveAsync(MediaType.TV, Episode("Breaking Bad", 1, 1, "Pilot"));
        var bc = await _resolver.ResolveAsync(MediaType.TV, Episode("Better Call Saul", 1, 1, "Uno"));

        Assert.NotEqual(bb.ParentWorkId, bc.ParentWorkId);
    }

    [Fact]
    public async Task TV_NoShowName_FallsBackToStandalone()
    {
        var orphan = await _resolver.ResolveAsync(MediaType.TV, new Dictionary<string, string>
        {
            ["title"] = "Some Episode",
        });

        Assert.Equal(WorkKind.Standalone, orphan.WorkKind);
    }

    // ── Comics ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Comics_TwoIssues_ShareSeriesParent()
    {
        var i1 = await _resolver.ResolveAsync(MediaType.Comics, new Dictionary<string, string>
        {
            ["series"] = "Saga",
            ["issue_number"] = "1",
            ["title"] = "Chapter One",
        });
        var i2 = await _resolver.ResolveAsync(MediaType.Comics, new Dictionary<string, string>
        {
            ["series"] = "Saga",
            ["issue_number"] = "2",
            ["title"] = "Chapter Two",
        });

        Assert.NotNull(i1.ParentWorkId);
        Assert.Equal(i1.ParentWorkId, i2.ParentWorkId);
        Assert.Equal(1, i1.Ordinal);
        Assert.Equal(2, i2.Ordinal);
    }

    [Fact]
    public async Task Comics_FractionalIssue_DoesNotCollideWithWholeIssue()
    {
        var special = await _resolver.ResolveAsync(MediaType.Comics, new Dictionary<string, string>
        {
            ["series"] = "Example Run",
            ["issue_number"] = "3.5",
            ["title"] = "Interlude",
        });
        var issue = await _resolver.ResolveAsync(MediaType.Comics, new Dictionary<string, string>
        {
            ["series"] = "Example Run",
            ["issue_number"] = "4",
            ["title"] = "Chapter Four",
        });

        Assert.Equal(special.ParentWorkId, issue.ParentWorkId);
        Assert.NotEqual(special.WorkId, issue.WorkId);

        using var conn = _db.CreateConnection();
        var sorts = conn.Query<double>(
            "SELECT ordinal_sort FROM works WHERE parent_work_id = @parent ORDER BY ordinal_sort;",
            new { parent = special.ParentWorkId })
            .ToList();

        Assert.Equal([3.5d, 4d], sorts);
    }

    [Fact]
    public async Task Books_FoundationSeriesMetadata_CreatesSeriesParent()
    {
        var first = await _resolver.ResolveAsync(MediaType.Books, new Dictionary<string, string>
        {
            ["title"] = "Foundation",
            ["author"] = "Isaac Asimov",
            ["series"] = "Foundation",
            ["series_position"] = "1",
        });
        var second = await _resolver.ResolveAsync(MediaType.Books, new Dictionary<string, string>
        {
            ["title"] = "Foundation and Empire",
            ["author"] = "Asimov, Isaac",
            ["series"] = "Foundation",
            ["series_position"] = "2",
        });

        Assert.Equal(WorkKind.Child, first.WorkKind);
        Assert.Equal(WorkKind.Child, second.WorkKind);
        Assert.NotNull(first.ParentWorkId);
        Assert.Equal(first.ParentWorkId, second.ParentWorkId);
        Assert.Equal(1, first.Ordinal);
        Assert.Equal(2, second.Ordinal);

        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE work_kind = 'parent' AND media_type = 'Books';"));
    }

    [Fact]
    public async Task Books_DecimalSeriesPosition_IsStoredAsSortValue()
    {
        var novella = await _resolver.ResolveAsync(MediaType.Books, new Dictionary<string, string>
        {
            ["title"] = "The Churn",
            ["author"] = "James S. A. Corey",
            ["series"] = "The Expanse",
            ["series_position"] = "3.5",
        });
        var novel = await _resolver.ResolveAsync(MediaType.Books, new Dictionary<string, string>
        {
            ["title"] = "Cibola Burn",
            ["author"] = "Corey, James S. A.",
            ["series"] = "The Expanse",
            ["series_position"] = "4",
        });

        Assert.Equal(novella.ParentWorkId, novel.ParentWorkId);
        Assert.NotEqual(novella.WorkId, novel.WorkId);
        Assert.Null(novella.Ordinal);
        Assert.Equal(4, novel.Ordinal);

        using var conn = _db.CreateConnection();
        var sorts = conn.Query<double>(
            "SELECT ordinal_sort FROM works WHERE parent_work_id = @parent ORDER BY ordinal_sort;",
            new { parent = novella.ParentWorkId })
            .ToList();

        Assert.Equal([3.5d, 4d], sorts);
    }

    // ── Standalone ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Movies_AlwaysStandalone_NoDedup()
    {
        var a = await _resolver.ResolveAsync(MediaType.Movies, new Dictionary<string, string>
        {
            ["title"] = "Inception",
            ["director"] = "Christopher Nolan",
            ["year"] = "2010",
        });
        var b = await _resolver.ResolveAsync(MediaType.Movies, new Dictionary<string, string>
        {
            ["title"] = "Inception",
            ["director"] = "Christopher Nolan",
            ["year"] = "2010",
        });

        // Two different files of the same movie create two different
        // standalone Works. The "is this the same file" check happens
        // upstream in the ingestion engine via content hash; the chain
        // factory is only invoked when a NEW file needs a Work.
        Assert.Equal(WorkKind.Standalone, a.WorkKind);
        Assert.Equal(WorkKind.Standalone, b.WorkKind);
        Assert.NotEqual(a.WorkId, b.WorkId);
        Assert.Null(a.ParentWorkId);
    }

    // ── Catalog promotion ─────────────────────────────────────────────────────

    [Fact]
    public async Task Music_CatalogRow_GetsPromoted_WhenFileArrives()
    {
        // Set up: ingest one track of an album so the parent exists.
        var first = await _resolver.ResolveAsync(MediaType.Music,
            Track("Daft Punk", "Discovery", "One More Time", 1));
        var parentId = first.ParentWorkId!.Value;

        // Simulate Wikidata adding a catalog row for track #2 (we don't own it yet).
        var catalogId = await _works.InsertCatalogChildAsync(
            MediaType.Music, parentId, ordinal: 2, externalIdentifiers: null, default);

        // Now a file for track #2 arrives.
        var arrived = await _resolver.ResolveAsync(MediaType.Music,
            Track("Daft Punk", "Discovery", "Aerodynamic", 2));

        // The resolver should have found the catalog row and promoted it.
        Assert.Equal(catalogId, arrived.WorkId);
        Assert.False(arrived.NewlyCreated);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkRepository_FindConfirmedSiblingQid_MatchesOwnedBookVariant()
    {
        await InsertOwnedWorkWithCanonicalsAsync(MediaType.Books, new Dictionary<string, string>
        {
            ["title"] = "The Shining",
            ["author"] = "Stephen King",
            ["wikidata_qid"] = "Q470937",
        });

        var titleOnlyMatch = await _works.FindConfirmedSiblingQidAsync(
            MediaType.Audiobooks,
            [MediaType.Books],
            "The Shining (Unabridged)",
            null);
        Assert.NotNull(titleOnlyMatch);

        var match = await _works.FindConfirmedSiblingQidAsync(
            MediaType.Audiobooks,
            [MediaType.Books],
            "The Shining (Unabridged)",
            "King, Stephen");

        Assert.NotNull(match);
        Assert.Equal(MediaType.Books, match!.MediaType);
        Assert.Equal("Q470937", match.WikidataQid);
        Assert.Equal("The Shining", match.Title);
    }

    private static Dictionary<string, string> Track(string artist, string album, string title, int trackNum, int? discNumber = null)
    {
        var metadata = new Dictionary<string, string>
        {
            ["artist"] = artist,
            ["album"] = album,
            ["title"] = title,
            ["track_number"] = trackNum.ToString(),
        };

        if (discNumber.HasValue)
        {
            metadata["disc_number"] = discNumber.Value.ToString();
        }

        return metadata;
    }

    private static Dictionary<string, string> AudiobookPart(
        string author,
        string bookTitle,
        string title,
        int part,
        int partCount) => new()
        {
            ["author"] = author,
            ["book_title"] = bookTitle,
            ["album"] = bookTitle,
            ["title"] = title,
            ["audiobook_part_number"] = part.ToString(),
            ["audiobook_part_count"] = partCount.ToString(),
        };

    private static Dictionary<string, string> Episode(string show, int season, int episode, string title) => new()
    {
        ["show_name"] = show,
        ["season_number"] = season.ToString(),
        ["episode_number"] = episode.ToString(),
        ["episode_title"] = title,
    };

    private async Task<Guid> InsertOwnedWorkWithCanonicalsAsync(
        MediaType mediaType,
        IReadOnlyDictionary<string, string> canonicalValues)
    {
        var workId = await _works.InsertStandaloneAsync(mediaType);
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO editions (id, work_id, format_label)
                VALUES (@editionId, @workId, 'Test');

                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @contentHash, @filePathRoot, 'Normal');
                """;
            AddGuidParameter(cmd, "@editionId", editionId);
            AddGuidParameter(cmd, "@workId", workId);
            AddGuidParameter(cmd, "@assetId", assetId);
            cmd.Parameters.AddWithValue("@contentHash", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("@filePathRoot", Path.Combine(Path.GetTempPath(), $"{assetId:N}.media"));
            await cmd.ExecuteNonQueryAsync();
        }

        var scalarValues = canonicalValues
            .Where(kvp => !MetadataFieldConstants.IsMultiValued(kvp.Key))
            .Select(kvp => new CanonicalValue
            {
                EntityId = assetId,
                Key = kvp.Key,
                Value = kvp.Value,
                LastScoredAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        var canonicalRepo = new CanonicalValueRepository(_db);
        await canonicalRepo.UpsertBatchAsync(scalarValues);

        var arrayRepo = new CanonicalValueArrayRepository(_db);
        foreach (var (key, value) in canonicalValues.Where(kvp => MetadataFieldConstants.IsMultiValued(kvp.Key)))
        {
            await arrayRepo.SetValuesAsync(
                assetId,
                key,
                [new CanonicalArrayEntry { Ordinal = 0, Value = value }]);
        }

        return workId;
    }

    private static void AddGuidParameter(Microsoft.Data.Sqlite.SqliteCommand command, string name, Guid value) =>
        command.Parameters.Add(name, Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(value);

    private void InsertLegacyAssetBackedWork(
        Guid workId,
        Guid assetId,
        string mediaType,
        IReadOnlyDictionary<string, string> canonicalValues)
    {
        using var conn = _db.CreateConnection();
        var editionId = Guid.NewGuid();
        conn.Execute("""
            INSERT INTO works (id, media_type, work_kind, is_catalog_only, ownership)
            VALUES (@workId, @mediaType, 'standalone', 0, 'Owned');

            INSERT INTO editions (id, work_id, format_label)
            VALUES (@editionId, @workId, 'Test');

            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @contentHash, @path);
            """,
            new
            {
                workId = workId.ToString("D"),
                editionId = editionId.ToString("D"),
                assetId = assetId.ToString("D"),
                mediaType,
                contentHash = Guid.NewGuid().ToString("N"),
                path = Path.Combine(Path.GetTempPath(), $"{assetId:N}.media"),
            });

        foreach (var (key, value) in canonicalValues)
        {
            conn.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at, is_conflicted, needs_review)
                VALUES (@assetId, @key, @value, @now, 0, 0);
                """,
                new
                {
                    assetId = assetId.ToString("D"),
                    key,
                    value,
                    now = DateTimeOffset.UtcNow.ToString("O"),
                });
        }
    }
}
