using Dapper;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The rule fields added by the Collections and lists spec (4c), against a real temp SQLite data store:
/// activity fields evaluate for the viewing profile, and the dates, age levels, tags, collections, Universe,
/// library, file location, quality and variant fields find the right titles.
/// </summary>
public sealed class CollectionRuleExtendedFieldTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public CollectionRuleExtendedFieldTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_rule_fields_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        AddProfile(_alice, "Alice");
        AddProfile(_bob, "Bob");
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    private CollectionRuleEvaluator Evaluator() => new(_db, new FixedClock(Now));

    private static CollectionRulePredicate Rule(string field, string op, params string[] values) => new()
    {
        Field = field,
        Op = op,
        Values = values,
    };

    private IReadOnlyList<Guid> Match(Guid? viewer, params CollectionRulePredicate[] rules) =>
        Evaluator().Evaluate(CollectionRuleDefinition.SingleGroup(rules), viewerProfileId: viewer);

    // ── Per-profile evaluation (D4) ─────────────────────────────────────────────

    [Fact]
    public void UnwatchedAndProviderRatingAtLeast_ReturnsDifferentTitlesForTwoProfilesWithDifferentHistory()
    {
        var eightPointZero = Movie("Eight", rating: "8.0");
        var ninePointZero = Movie("Nine", rating: "9.0");
        var sevenPointZero = Movie("Seven", rating: "7.0");
        var eightPointFive = Movie("Eight and a half", rating: "8.5");

        // Alice finished "Eight" and is halfway through "Nine"; Bob finished "Eight and a half".
        Progress(_alice, eightPointZero, 100);
        Progress(_alice, ninePointZero, 50);
        Progress(_bob, eightPointFive, 99.9);

        var rules = new[]
        {
            Rule("watch_state", "eq", "unwatched"),
            Rule("provider_rating", "gte", "7.5"),
        };

        var forAlice = Match(_alice, rules);
        var forBob = Match(_bob, rules);

        Assert.Equal([eightPointFive], forAlice);
        Assert.Equivalent(new[] { eightPointZero, ninePointZero }, forBob);
        Assert.DoesNotContain(sevenPointZero, forAlice.Concat(forBob));
        Assert.NotEqual(forAlice, forBob);
    }

    [Fact]
    public void ActivityConditions_WithNoViewingProfile_MatchNothing()
    {
        Movie("Anything", rating: "9.0");

        Assert.Empty(Match(null, Rule("watch_state", "eq", "unwatched")));
        Assert.Empty(Match(null, Rule("on_my_list", "eq", "true")));
        Assert.Equal(0, Evaluator().Count(CollectionRuleDefinition.SingleGroup([Rule("favourite", "eq", "true")])));
    }

    [Fact]
    public void Count_EvaluatesActivityForTheViewer()
    {
        var first = Movie("First", rating: "8");
        Movie("Second", rating: "8");
        Progress(_alice, first, 100);

        var definition = CollectionRuleDefinition.SingleGroup([Rule("watch_state", "eq", "unwatched")]);

        Assert.Equal(1, Evaluator().Count(definition, viewerProfileId: _alice));
        Assert.Equal(2, Evaluator().Count(definition, viewerProfileId: _bob));
    }

    [Fact]
    public void WatchState_SeparatesWatchedInProgressAndUnwatched()
    {
        var finished = Movie("Finished");
        var started = Movie("Started");
        var fresh = Movie("Fresh");
        var song = Movie("Song", mediaType: "Music");
        Progress(_alice, finished, 100);
        Progress(_alice, started, 40);
        PlayedSong(_alice, song, plays: 3, lastPlayed: Now.AddDays(-2));

        Assert.Equivalent(new[] { finished, song }, Match(_alice, Rule("watch_state", "eq", "watched")));
        Assert.Equal([started], Match(_alice, Rule("watch_state", "eq", "in_progress")));
        Assert.Equal([fresh], Match(_alice, Rule("watch_state", "eq", "unwatched")));
        Assert.Equivalent(new[] { started, fresh }, Match(_alice, Rule("watch_state", "neq", "watched")));
    }

    [Fact]
    public void PlayCount_CountsFinishedSessionsAndSongPlays()
    {
        var movie = Movie("Rewatched");
        var once = Movie("Once");
        var song = Movie("Song", mediaType: "Music");
        FinishedSession(_alice, movie, "s1");
        FinishedSession(_alice, movie, "s2");
        FinishedSession(_alice, once, "s3");
        PlayedSong(_alice, song, plays: 5, lastPlayed: Now);

        Assert.Equivalent(new[] { movie, song }, Match(_alice, Rule("play_count", "gte", "2")));
        Assert.Equal([once], Match(_alice, Rule("play_count", "eq", "1")));
        Assert.Equal([song], Match(_alice, Rule("play_count", "between", "4", "6")));
        Assert.Empty(Match(_bob, Rule("play_count", "gte", "1")));
    }

    [Fact]
    public void LastPlayed_WithinOrNotWithinDays_UsesTheViewersOwnHistory()
    {
        var recent = Movie("Recent");
        var old = Movie("Old");
        var never = Movie("Never");
        Progress(_alice, recent, 30, lastAccessed: Now.AddDays(-3));
        Progress(_alice, old, 30, lastAccessed: Now.AddDays(-90));

        Assert.Equal([recent], Match(_alice, Rule("last_played", "within_days", "7")));
        Assert.Equivalent(new[] { old, never }, Match(_alice, Rule("last_played", "not_within_days", "7")));
        Assert.Equal([never], Match(_alice, Rule("last_played", "unknown")));
        Assert.Equivalent(new[] { never, old, recent }, Match(_bob, Rule("last_played", "unknown")));
    }

    [Fact]
    public void OnMyListFavouriteAndYourRating_AreEachProfilesOwn()
    {
        var listed = Movie("Listed");
        var loved = Movie("Loved");
        var disliked = Movie("Disliked");
        Movie("Plain");
        SaveToMyList(_alice, listed);
        React(_alice, loved, "Love");
        React(_alice, disliked, "Dislike");
        React(_bob, listed, "Like");

        Assert.Equal([listed], Match(_alice, Rule("on_my_list", "eq", "true")));
        Assert.Empty(Match(_bob, Rule("on_my_list", "eq", "true")));
        Assert.Equal([loved], Match(_alice, Rule("favourite", "eq", "true")));
        Assert.Equal([loved], Match(_alice, Rule("your_rating", "eq", "love")));
        Assert.Equal([disliked], Match(_alice, Rule("your_rating", "eq", "dislike")));
        Assert.Equal([listed], Match(_bob, Rule("your_rating", "eq", "like")));
        Assert.Equal(2, Match(_alice, Rule("your_rating", "eq", "unrated")).Count);
        Assert.Equal(3, Match(_alice, Rule("on_my_list", "eq", "false")).Count);
    }

    [Fact]
    public void MyListSavedOnTheShow_CountsForItsEpisodes()
    {
        var (show, episode) = TvEpisode();
        SaveToMyList(_alice, show);

        Assert.Contains(episode, Match(_alice, Rule("on_my_list", "eq", "true")));
        Assert.DoesNotContain(episode, Match(_bob, Rule("on_my_list", "eq", "true")));
    }

    [Fact]
    public void Tags_CountTheViewersOwnLocalTagsBesideLibraryTags()
    {
        var library = Movie("Library tag");
        var local = Movie("Local tag");
        Movie("Untagged");
        Canonical(library, "custom_tags", "cozy");
        LocalTags(_alice, local, "cozy");

        Assert.Equivalent(new[] { library, local }, Match(_alice, Rule("custom_tags", "eq", "cozy")));
        Assert.Equal([library], Match(_bob, Rule("custom_tags", "eq", "cozy")));
        Assert.Equal([library], Match(null, Rule("custom_tags", "eq", "cozy")));
    }

    // ── Media ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ReleaseDate_WorksRelativeAndAbsolute()
    {
        var thisYear = Movie("This year");
        var lastYear = Movie("Last year");
        var older = Movie("Older");
        var yearOnly = Movie("Year only");
        Canonical(thisYear, "release_date", "2026-06-15");
        Canonical(lastYear, "release_date", "2025-11-01");
        Canonical(older, "release_date", "1999-03-31");
        Canonical(yearOnly, "release_date", "2010");

        Assert.Equivalent(new[] { thisYear, lastYear }, Match(null, Rule("release_date", "in_last_years", "1")));
        Assert.Equal([thisYear], Match(null, Rule("release_date", "in_last_months", "6")));
        Assert.Equal([thisYear], Match(null, Rule("release_date", "in_last_days", "200")));
        Assert.Equal([older], Match(null, Rule("release_date", "lt", "2000-01-01")));
        Assert.Equivalent(new[] { thisYear, lastYear }, Match(null, Rule("release_date", "gt", "2025-01-01")));
        Assert.Equal([yearOnly], Match(null, Rule("release_date", "between", "2009-06-01", "2011-06-01")));
        Assert.Equal([yearOnly], Match(null, Rule("release_date", "between", "2011-06-01", "2009-06-01")));
    }

    [Fact]
    public void AgeLevel_FollowsTheKidsRatingLadder()
    {
        var g = Movie("G movie");
        var pg = Movie("PG movie");
        var pg13 = Movie("Teen movie");
        var tvMa = Movie("Mature show");
        var unrated = Movie("No rating");
        Canonical(g, "content_rating", "G");
        Canonical(pg, "content_rating", "PG");
        Canonical(pg13, "content_rating", "PG-13");
        Canonical(tvMa, "certification", "TV-MA");

        Assert.Equivalent(new[] { g, pg }, Match(null, Rule("age_level", "lte", "PG")));
        Assert.Equivalent(new[] { pg13, tvMa }, Match(null, Rule("age_level", "gt", "PG")));
        Assert.Equal([unrated], Match(null, Rule("age_level", "unknown")));
        Assert.Equal(4, Match(null, Rule("age_level", "known")).Count);
    }

    [Fact]
    public void AgeLevel_ExcludesATitleThatAlsoCarriesAHigherRating()
    {
        var mixed = Movie("Mixed ratings");
        Canonical(mixed, "content_rating", "PG");
        Canonical(mixed, "certification", "R");

        Assert.Empty(Match(null, Rule("age_level", "lte", "PG")));
        Assert.Equal([mixed], Match(null, Rule("age_level", "gt", "PG")));
    }

    [Fact]
    public void Length_FieldsUseTheStoredRuntimePagesAndTracks()
    {
        var short1 = Movie("Short");
        var long1 = Movie("Long");
        Canonical(short1, "runtime", "88");
        Canonical(long1, "runtime", "172");
        var book = Movie("Book", mediaType: "Books");
        Canonical(book, "page_count", "412");

        Assert.Equal([long1], Match(null, Rule("runtime", "gte", "150")));
        Assert.Equal([short1], Match(null, Rule("runtime", "between", "60", "100")));
        Assert.Equal([book], Match(null, Rule("page_count", "gte", "400")));
    }

    [Fact]
    public void HasVariantsAndAvailableAs_FindTheSameTitleOwnedInAnotherFormat()
    {
        var ebook = Movie("Dune", mediaType: "Books", qid: "Q1");
        var audiobook = Movie("Dune (audio)", mediaType: "Audiobooks", qid: "Q1");
        var loner = Movie("Loner", mediaType: "Books", qid: "Q2");
        var twoEditions = Movie("Two editions", mediaType: "Movies");
        AddEdition(twoEditions);

        Assert.Equivalent(new[] { ebook, audiobook, twoEditions }, Match(null, Rule("has_variants", "eq", "true")));
        Assert.Contains(loner, Match(null, Rule("has_variants", "eq", "false")));
        Assert.Equivalent(new[] { ebook, audiobook }, Match(null, Rule("available_as", "eq", "audiobook")));
        Assert.Equivalent(new[] { ebook, audiobook }, Match(null, Rule("available_as", "all_of", "ebook", "audiobook")));
        Assert.Equal([loner], Match(null, Rule("available_as", "eq", "ebook"), Rule("available_as", "neq", "audiobook")));
    }

    // ── Story and world ─────────────────────────────────────────────────────────

    [Fact]
    public void InCollectionAndInUniverse_UseStoredMembersAndTheUniverseParent()
    {
        var inShelf = Movie("On the shelf");
        var elsewhere = Movie("Elsewhere");
        var shelfId = Guid.NewGuid();
        AddManualCollection(shelfId, "Shelf", inShelf);

        var universe = Guid.NewGuid();
        var series = Guid.NewGuid();
        AddCollectionRow(universe, null);
        AddCollectionRow(series, universe);
        var universeWork = Movie("Franchise film");
        SetWorkCollection(universeWork, series);

        Assert.Equal([inShelf], Match(null, Rule("in_collection", "eq", shelfId.ToString("D"))));
        Assert.Equal([inShelf], Match(null, Rule("in_collection", "known")));
        Assert.Contains(elsewhere, Match(null, Rule("in_collection", "neq", shelfId.ToString("D"))));
        Assert.DoesNotContain(inShelf, Match(null, Rule("in_collection", "neq", shelfId.ToString("D"))));
        Assert.Equal([universeWork], Match(null, Rule("in_universe", "eq", "true")));
        Assert.DoesNotContain(universeWork, Match(null, Rule("in_universe", "eq", "false")));
    }

    // ── My library ──────────────────────────────────────────────────────────────

    [Fact]
    public void LibraryFileLocationAndQuality_ReadTheOwnedFiles()
    {
        var uhd = Movie("UHD", libraryId: "lib-movies", path: "/media/Movies/4K/uhd.mkv");
        var sd = Movie("SD", libraryId: "lib-old", path: "/media/Old/sd.avi");
        Canonical(AssetOf(uhd), "video_height", "2160");
        Canonical(AssetOf(sd), "video_height", "480");

        Assert.Equal([uhd], Match(null, Rule("library", "eq", "lib-movies")));
        Assert.Equal([sd], Match(null, Rule("library", "neq", "lib-movies")));
        Assert.Equal([uhd], Match(null, Rule("file_location", "contains", "/Movies/4K")));
        Assert.Equal([sd], Match(null, Rule("file_location", "not_contains", "4K")));
        Assert.Equal([uhd], Match(null, Rule("quality_tier", "eq", "4k")));
        Assert.Equal([sd], Match(null, Rule("quality_tier", "eq", "sd")));
        Assert.Empty(Match(null, Rule("quality_tier", "eq", "hd")));
    }

    [Fact]
    public void AddedDate_FiltersByTheDayTheTitleEnteredTheLibrary()
    {
        var early = Movie("Early");
        var late = Movie("Late");
        Claim(AssetOf(early), "2024-02-10T08:00:00Z");
        Claim(AssetOf(late), "2026-09-01T08:00:00Z");

        Assert.Equal([early], Match(null, Rule("added_date", "lt", "2025-01-01")));
        Assert.Equal([late], Match(null, Rule("added_date", "gt", "2026-01-01")));
        Assert.Equivalent(new[] { early, late }, Match(null, Rule("added_date", "between", "2024-01-01", "2026-12-31")));
    }

    [Fact]
    public void AddedWithinDays_IsNot_FindsTitlesAddedLongerAgoThanThat()
    {
        var shelved = Movie("Shelved");
        var fresh = Movie("Fresh");
        Claim(AssetOf(shelved), DateTime.UtcNow.AddDays(-500).ToString("O"));
        Claim(AssetOf(fresh), DateTime.UtcNow.AddDays(-10).ToString("O"));

        Assert.Equal([fresh], Match(null, Rule("added_within_days", "eq", "365")));
        Assert.Equal([shelved], Match(null, Rule("added_within_days", "neq", "365")));
    }

    [Fact]
    public void UsesPerProfileFields_FlagsOnlyActivityConditions()
    {
        Assert.True(CollectionRuleEvaluator.UsesPerProfileFields(
            CollectionRuleDefinition.SingleGroup([Rule("genre", "eq", "Drama"), Rule("watch_state", "eq", "unwatched")])));
        Assert.False(CollectionRuleEvaluator.UsesPerProfileFields(
            CollectionRuleDefinition.SingleGroup([Rule("genre", "eq", "Drama"), Rule("release_date", "lt", "2000-01-01")])));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private readonly Dictionary<Guid, Guid> _assets = [];

    private Guid AssetOf(Guid workId) => _assets[workId];

    private void AddProfile(Guid id, string name)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profiles (id, display_name, created_at) VALUES (@id, @name, @at);",
            new { id, name, at = Now.ToString("O") });
    }

    private Guid Movie(
        string title,
        string mediaType = "Movies",
        string? rating = null,
        string? qid = null,
        string libraryId = "lib-default",
        string? path = null)
    {
        using var conn = _db.CreateConnection();
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO collections (id, created_at) VALUES (@collectionId, datetime('now'));
            INSERT INTO works (id, collection_id, media_type, wikidata_qid, ready_at)
                VALUES (@workId, @collectionId, @mediaType, @qid, '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
                VALUES (@assetId, @editionId, @hash, @path, 'Normal', @libraryId);
            """,
            new
            {
                collectionId,
                workId,
                editionId,
                assetId,
                mediaType,
                qid,
                libraryId,
                hash = $"hash_{workId:N}",
                path = path ?? $"/media/{title}.bin",
            });
        _assets[workId] = assetId;
        Canonical(workId, "title", title);
        if (rating is not null)
        {
            Canonical(workId, "rating", rating);
        }

        return workId;
    }

    private (Guid Show, Guid Episode) TvEpisode()
    {
        var show = Movie("Show", mediaType: "TV");
        var episode = Movie("Episode", mediaType: "TV");
        using var conn = _db.CreateConnection();
        conn.Execute("UPDATE works SET parent_work_id = @show WHERE id = @episode;", new { show, episode });
        return (show, episode);
    }

    private void AddEdition(Guid workId)
    {
        using var conn = _db.CreateConnection();
        var editionId = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
                VALUES (@assetId, @editionId, @hash, '/media/second-edition.mkv', 'Normal');
            """,
            new { editionId, workId, assetId = Guid.NewGuid(), hash = $"hash_{editionId:N}" });
    }

    private void Canonical(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
            VALUES (@entityId, @key, COALESCE((SELECT MAX(ordinal) + 1 FROM canonical_value_arrays WHERE entity_id = @entityId AND key = @key), 0), @value);
            """,
            new { entityId, key, value });
        if (key is "title" or "rating" or "release_date" or "runtime" or "page_count" or "video_height" or "content_rating" or "certification")
        {
            conn.Execute(
                "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, datetime('now'));",
                new { entityId, key, value });
        }
    }

    private void Claim(Guid assetId, string claimedAt)
    {
        using var conn = _db.CreateConnection();
        var provider = Guid.NewGuid();
        conn.Execute(
            """
            INSERT INTO metadata_providers (id, name, version) VALUES (@provider, @name, '1');
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            VALUES (@id, @assetId, @provider, 'title', 'x', 0.9, @claimedAt);
            """,
            new { id = Guid.NewGuid(), assetId, provider, name = $"provider-{provider:N}", claimedAt });
    }

    private void Progress(Guid profile, Guid workId, double percent, DateTimeOffset? lastAccessed = null)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed) VALUES (@profile, @asset, @percent, @at);",
            new { profile, asset = AssetOf(workId), percent, at = (lastAccessed ?? Now).ToString("O") });
    }

    private void FinishedSession(Guid profile, Guid workId, string session)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO consumption_history (id, profile_id, asset_id, session_key, experience, started_at, updated_at, progress_pct)
            VALUES (@id, @profile, @asset, @session, 'Played', @at, @at, 100);
            """,
            new { id = Guid.NewGuid(), profile, asset = AssetOf(workId), session, at = Now.ToString("O") });
    }

    private void PlayedSong(Guid profile, Guid workId, int plays, DateTimeOffset lastPlayed)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO music_play_stats (profile_id, work_id, play_count, last_played_at) VALUES (@profile, @workId, @plays, @at);",
            new { profile, workId, plays, at = lastPlayed.ToString("O") });
    }

    private void SaveToMyList(Guid profile, Guid workId)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profile_saved_items (profile_id, entity_kind, entity_id, saved_at) VALUES (@profile, 'Movie', @workId, @at);",
            new { profile, workId, at = Now.ToString("O") });
    }

    private void React(Guid profile, Guid workId, string reaction)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profile_reactions (profile_id, entity_kind, entity_id, reaction, updated_at) VALUES (@profile, 'Movie', @workId, @reaction, @at);",
            new { profile, workId, reaction, at = Now.ToString("O") });
    }

    private void LocalTags(Guid profile, Guid workId, params string[] tags)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO profile_work_preferences (profile_id, work_id, local_tags_json, revision, updated_at) VALUES (@profile, @workId, @json, 0, @at);",
            new { profile, workId, json = System.Text.Json.JsonSerializer.Serialize(tags), at = Now.ToString("O") });
    }

    private void AddManualCollection(Guid id, string name, params Guid[] works)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO collections (id, display_name, resolution, created_at) VALUES (@id, @name, 'materialized', datetime('now'));",
            new { id, name });
        foreach (var work in works)
        {
            conn.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id) VALUES (@itemId, @id, @work);",
                new { itemId = Guid.NewGuid(), id, work });
        }
    }

    private void AddCollectionRow(Guid id, Guid? parent)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO collections (id, parent_collection_id, created_at) VALUES (@id, @parent, datetime('now'));",
            new { id, parent });
    }

    private void SetWorkCollection(Guid workId, Guid collectionId)
    {
        using var conn = _db.CreateConnection();
        conn.Execute("UPDATE works SET collection_id = @collectionId WHERE id = @workId;", new { workId, collectionId });
    }
}
