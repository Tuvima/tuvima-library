using Dapper;
using MediaEngine.Domain;

namespace MediaEngine.Storage.Tests;

public sealed class PersonCreditSqlTests : IDisposable
{
    private const string ViewSelect = """
        SELECT hex(media_asset_id) AS AssetHex,
               hex(person_id) AS PersonHex,
               person_name AS PersonName,
               person_qid AS PersonQid,
               credit_key AS CreditKey,
               billing_order AS BillingOrder,
               role AS Role
        """;

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public PersonCreditSqlTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima-person-credit-sql-{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ScopedCredits_ForEveryFile_MatchTheSharedCreditView()
    {
        var fixture = await SeedAsync();

        var expected = await ReadViewAsync(fixture.AllAssets);
        var actual = await ReadScopedAsync(fixture.AllAssets);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ScopedCredits_ForASubsetOfFiles_MatchTheSharedCreditViewForThoseFiles()
    {
        var fixture = await SeedAsync();
        var subset = new[] { fixture.Episode1, fixture.Track, fixture.BookWithRootAndFileAuthor, fixture.BookWithPseudonym };

        var expected = await ReadViewAsync(subset);
        var actual = await ReadScopedAsync(subset);

        Assert.NotEmpty(expected);
        Assert.Equal(expected, actual);
        Assert.All(actual, row => Assert.Contains(row.AssetHex, subset.Select(Hex)));
        Assert.True(actual.Count < (await ReadViewAsync(fixture.AllAssets)).Count);
    }

    [Fact]
    public async Task ScopedCredits_ForNoFiles_ReturnNothing()
    {
        await SeedAsync();

        var actual = await ReadScopedAsync(Array.Empty<Guid>());

        Assert.Empty(actual);
    }

    [Fact]
    public async Task ScopedCredits_UsedTwiceInOneStatement_DoNotCollide()
    {
        var fixture = await SeedAsync();
        var firstSet = new[] { fixture.Episode1, fixture.Track };
        var secondSet = new[] { fixture.Movie, fixture.BookWithPseudonym };

        var parameters = new DynamicParameters();
        var firstSelect = AssetIdsSelect("a", firstSet, parameters);
        var secondSelect = AssetIdsSelect("b", secondSet, parameters);
        var sql = $"""
            WITH {PersonCreditSql.ScopedCreditsCte("first_credits", firstSelect, "first_")},
                 {PersonCreditSql.ScopedCreditsCte("second_credits", secondSelect, "second_")}
            SELECT 1 AS SetNumber, hex(media_asset_id) AS AssetHex, hex(person_id) AS PersonHex,
                   person_name AS PersonName, person_qid AS PersonQid, credit_key AS CreditKey,
                   billing_order AS BillingOrder, role AS Role
            FROM first_credits
            UNION ALL
            SELECT 2, hex(media_asset_id), hex(person_id), person_name, person_qid, credit_key, billing_order, role
            FROM second_credits
            """;

        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<NumberedCreditRow>(sql, parameters)).ToList();

        var first = rows.Where(r => r.SetNumber == 1).Select(r => r.ToCredit()).OrderBy(c => c.SortKey, StringComparer.Ordinal).ToList();
        var second = rows.Where(r => r.SetNumber == 2).Select(r => r.ToCredit()).OrderBy(c => c.SortKey, StringComparer.Ordinal).ToList();
        Assert.Equal(await ReadViewAsync(firstSet), first);
        Assert.Equal(await ReadViewAsync(secondSet), second);
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
    }

    [Fact]
    public async Task ScopedCredits_FollowTheViewsPrecedenceRules()
    {
        var fixture = await SeedAsync();

        var credits = await ReadScopedAsync(fixture.AllAssets);
        IEnumerable<string> NamesFor(Guid assetId, string creditKey) => credits
            .Where(c => c.AssetHex == Hex(assetId) && c.CreditKey == creditKey)
            .OrderBy(c => c.BillingOrder)
            .Select(c => c.PersonName);

        // Show credits beat the episode's own director; arrays beat claims; the file's screenwriter array beats the work claim.
        Assert.Equal(new[] { "Show Director" }, NamesFor(fixture.Episode1, "director"));
        Assert.Equal(new[] { "Cast One", "Cast Two" }, NamesFor(fixture.Episode1, "cast_member"));
        Assert.Equal(new[] { "Episode Writer" }, NamesFor(fixture.Episode1, "screenwriter"));

        // A scalar only counts when no array or claim names the credit.
        Assert.Equal(new[] { "Scalar Composer" }, NamesFor(fixture.Episode2, "composer"));
        Assert.Equal(new[] { "Claim Actor" }, NamesFor(fixture.Movie, "cast_member"));
        Assert.Equal(new[] { "Movie Director" }, NamesFor(fixture.Movie, "director"));

        // Root credit wins over the file's own credit; blank values are dropped; claims keep their order.
        Assert.Equal(new[] { "Root Author" }, NamesFor(fixture.BookWithRootAndFileAuthor, "author"));
        Assert.Equal(new[] { "Book Author One", "Book Author Two" }, NamesFor(fixture.BookFromClaims, "author"));
        Assert.Equal(new[] { "Padded Name" }, NamesFor(fixture.BookFromClaims, "narrator"));

        // The album's artist applies to its track; the track's own performer is a separate credit.
        Assert.Equal(new[] { "Album Artist" }, NamesFor(fixture.Track, "artist"));
        Assert.Equal(new[] { "Track Performer" }, NamesFor(fixture.Track, "performer"));

        // A credit with a QID finds the person by QID; a credit without one matches the name ignoring case.
        Assert.Equal(new[] { "Douglas A." }, NamesFor(fixture.BookWithQid, "author"));
        Assert.Equal(new[] { "Shy Faruqi" }, NamesFor(fixture.BookWithQid, "narrator"));

        // When an author is credited with a pen name, the real person is not listed beside it.
        Assert.Equal(new[] { "Pen Name" }, NamesFor(fixture.BookWithPseudonym, "author"));
    }

    [Fact]
    public async Task ScopedCredits_ForTenFiles_SearchTheCreditTablesByIndexAndNeverScanThem()
    {
        var fixture = await SeedAsync();
        var ids = fixture.AllAssets.Concat(new[] { Guid.NewGuid(), Guid.NewGuid() }).ToList();
        var parameters = new DynamicParameters();
        var select = AssetIdsSelect("a", ids, parameters);
        var sql = $"""
            EXPLAIN QUERY PLAN
            WITH {PersonCreditSql.ScopedCreditsCte("scoped_credits", select, "sc_")}
            SELECT * FROM scoped_credits
            """;

        using var conn = _db.CreateConnection();
        var plan = (await conn.QueryAsync<PlanRow>(sql, parameters)).Select(r => r.Detail).ToList();

        Assert.NotEmpty(plan);
        // cva, mc and cv are the aliases the building block gives canonical_value_arrays, metadata_claims and canonical_values.
        foreach (var alias in new[] { "cva", "mc", "cv" })
        {
            Assert.Contains(plan, line => line.StartsWith($"SEARCH {alias} USING INDEX", StringComparison.Ordinal));
            Assert.DoesNotContain(plan, line => line.StartsWith($"SCAN {alias}", StringComparison.Ordinal));
        }
    }

    private async Task<Fixture> SeedAsync()
    {
        var persons = new[]
        {
            "Show Director", "Cast One", "Cast Two", "Episode Director", "Episode Writer", "Claim Actor",
            "Scalar Composer", "Album Artist", "Track Performer", "Book Author One", "Book Author Two",
            "Padded Name", "Root Author", "File Author", "Movie Director", "Scalar Actor", "Shy Faruqi",
            "Real Person",
        };
        foreach (var name in persons)
        {
            await InsertPersonAsync(name);
        }

        await InsertPersonAsync("Douglas A.", qid: "Q42");
        await InsertPersonAsync("Pen Name", isPseudonym: true);

        // TV: show -> season -> two episodes.
        var showId = await InsertParentWorkAsync("TV", null);
        var seasonId = await InsertParentWorkAsync("TV", showId);
        var (episode1Work, episode1) = await InsertChildWorkAsync("TV", seasonId);
        var (episode2Work, episode2) = await InsertChildWorkAsync("TV", seasonId);
        await InsertArrayAsync(showId, "director", 0, "Show Director");
        await InsertArrayAsync(showId, "cast_member", 0, "Cast One");
        await InsertArrayAsync(showId, "cast_member", 1, "cast two");
        await InsertArrayAsync(episode1Work, "director", 0, "Episode Director");
        await InsertClaimAsync(episode1Work, "cast_member", "Claim Actor");
        await InsertArrayAsync(episode1, "screenwriter", 0, "Episode Writer");
        await InsertClaimAsync(episode1Work, "screenwriter", "Claim Actor");
        await InsertClaimAsync(episode2Work, "director", "Episode Director");
        await InsertArrayAsync(episode2Work, "narrator", 0, "   ");
        await InsertScalarAsync(episode2, "composer", "Scalar Composer");

        // Music: album -> track.
        var albumId = await InsertParentWorkAsync("Music", null);
        var (trackWork, track) = await InsertChildWorkAsync("Music", albumId);
        await InsertArrayAsync(albumId, "artist", 0, "Album Artist");
        await InsertArrayAsync(trackWork, "performer", 0, "Track Performer");
        await InsertClaimAsync(track, "artist", "Track Performer");

        // Books: credits only as claims, root versus file, QID and name matching, pen name.
        var (bookFromClaimsWork, bookFromClaims) = await InsertStandaloneWorkAsync("Books");
        await InsertClaimAsync(bookFromClaimsWork, "author", "Book Author One");
        await InsertClaimAsync(bookFromClaimsWork, "author", "  Book Author Two  ");
        await InsertClaimAsync(bookFromClaimsWork, "author", "");
        await InsertClaimAsync(bookFromClaims, "narrator", "  Padded Name ");

        var (rootAndFileWork, rootAndFile) = await InsertStandaloneWorkAsync("Books");
        await InsertArrayAsync(rootAndFileWork, "author", 0, "Root Author");
        await InsertArrayAsync(rootAndFile, "author", 0, "File Author");

        var (qidWork, bookWithQid) = await InsertStandaloneWorkAsync("Books");
        await InsertArrayAsync(qidWork, "author", 0, "Someone Else Entirely", "q42");
        await InsertArrayAsync(qidWork, "narrator", 0, "shy faruqi");

        var (pseudonymWork, bookWithPseudonym) = await InsertStandaloneWorkAsync("Books");
        await InsertArrayAsync(pseudonymWork, "author", 0, "Real Person");
        await InsertArrayAsync(pseudonymWork, "author", 1, "Pen Name");

        // Movie: director only as a scalar value; a claim beats a scalar for the cast.
        var (movieWork, movie) = await InsertStandaloneWorkAsync("Movies");
        await InsertScalarAsync(movieWork, "director", "Movie Director");
        await InsertClaimAsync(movieWork, "cast_member", "Claim Actor");
        await InsertScalarAsync(movieWork, "cast_member", "Scalar Actor");

        return new Fixture(
            episode1, episode2, track, bookFromClaims, rootAndFile, bookWithQid, bookWithPseudonym, movie,
            new[] { episode1, episode2, track, bookFromClaims, rootAndFile, bookWithQid, bookWithPseudonym, movie });
    }

    private async Task<List<Credit>> ReadViewAsync(IReadOnlyCollection<Guid> assetIds)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CreditRow>($"{ViewSelect} FROM primary_person_media_credits");
        var wanted = assetIds.Select(Hex).ToHashSet(StringComparer.Ordinal);
        return rows
            .Where(r => wanted.Contains(r.AssetHex))
            .Select(r => r.ToCredit())
            .OrderBy(c => c.SortKey, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<Credit>> ReadScopedAsync(IReadOnlyCollection<Guid> assetIds)
    {
        var parameters = new DynamicParameters();
        var select = AssetIdsSelect("a", assetIds, parameters);
        var sql = $"""
            WITH {PersonCreditSql.ScopedCreditsCte("scoped_credits", select, "sc_")}
            {ViewSelect} FROM scoped_credits
            """;

        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<CreditRow>(sql, parameters);
        return rows
            .Select(r => r.ToCredit())
            .OrderBy(c => c.SortKey, StringComparer.Ordinal)
            .ToList();
    }

    private static string AssetIdsSelect(string name, IReadOnlyCollection<Guid> assetIds, DynamicParameters parameters)
    {
        if (assetIds.Count == 0)
        {
            return "SELECT NULL WHERE 0";
        }

        var parts = new List<string>();
        var index = 0;
        foreach (var assetId in assetIds)
        {
            var parameterName = $"{name}{index++}";
            parameters.Add(parameterName, GuidSql.ToBlob(assetId), System.Data.DbType.Binary);
            parts.Add($"SELECT @{parameterName} AS asset_id");
        }

        return string.Join(" UNION ALL ", parts);
    }

    private static string Hex(Guid id) => Convert.ToHexString(GuidSql.ToBlob(id));

    private async Task InsertPersonAsync(string name, string? qid = null, bool isPseudonym = false)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO persons (id, name, wikidata_qid, is_pseudonym, created_at)
            VALUES (@id, @name, @qid, @isPseudonym, datetime('now'));
            """,
            new { id = Guid.NewGuid(), name, qid, isPseudonym = isPseudonym ? 1 : 0 });
    }

    private async Task<Guid> InsertParentWorkAsync(string mediaType, Guid? parentWorkId)
    {
        using var conn = _db.CreateConnection();
        var workId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, parent_work_id)
            VALUES (@workId, @mediaType, 'parent', @parentWorkId);
            """,
            new { workId, mediaType, parentWorkId });
        return workId;
    }

    private async Task<(Guid WorkId, Guid AssetId)> InsertChildWorkAsync(string mediaType, Guid parentWorkId)
    {
        using var conn = _db.CreateConnection();
        var workId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO works (id, media_type, work_kind, parent_work_id, ordinal)
            VALUES (@workId, @mediaType, 'child', @parentWorkId, 1);
            """,
            new { workId, mediaType, parentWorkId });
        return (workId, await InsertEditionAndAssetAsync(workId));
    }

    private async Task<(Guid WorkId, Guid AssetId)> InsertStandaloneWorkAsync(string mediaType)
    {
        using var conn = _db.CreateConnection();
        var workId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO works (id, media_type) VALUES (@workId, @mediaType);
            """,
            new { workId, mediaType });
        return (workId, await InsertEditionAndAssetAsync(workId));
    }

    private async Task<Guid> InsertEditionAndAssetAsync(Guid workId)
    {
        using var conn = _db.CreateConnection();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await conn.ExecuteAsync("""
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @filePath, 'Normal');
            """,
            new
            {
                editionId,
                workId,
                assetId,
                hash = $"hash_{assetId:N}",
                filePath = $"/library/{assetId:N}.bin",
            });
        return assetId;
    }

    private async Task InsertArrayAsync(Guid entityId, string key, int ordinal, string value, string? valueQid = null)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
            VALUES (@entityId, @key, @ordinal, @value, @valueQid);
            """,
            new { entityId, key, ordinal, value, valueQid });
    }

    private async Task InsertScalarAsync(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@entityId, @key, @value, datetime('now'));
            """,
            new { entityId, key, value });
    }

    private async Task InsertClaimAsync(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("""
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            VALUES (@id, @entityId, @providerId, @key, @value, 0.8, datetime('now'));
            """,
            new { id = Guid.NewGuid(), entityId, providerId = WellKnownProviders.OpenLibrary, key, value });
    }

    private sealed record Fixture(
        Guid Episode1,
        Guid Episode2,
        Guid Track,
        Guid BookFromClaims,
        Guid BookWithRootAndFileAuthor,
        Guid BookWithQid,
        Guid BookWithPseudonym,
        Guid Movie,
        IReadOnlyCollection<Guid> AllAssets);

    private sealed record Credit(
        string AssetHex,
        string PersonHex,
        string PersonName,
        string? PersonQid,
        string CreditKey,
        long BillingOrder,
        string Role)
    {
        public string SortKey =>
            $"{AssetHex}|{CreditKey}|{BillingOrder:D12}|{PersonHex}|{PersonName}|{PersonQid}|{Role}";
    }

    private class CreditRow
    {
        public string AssetHex { get; set; } = "";
        public string PersonHex { get; set; } = "";
        public string PersonName { get; set; } = "";
        public string? PersonQid { get; set; }
        public string CreditKey { get; set; } = "";
        public long BillingOrder { get; set; }
        public string Role { get; set; } = "";

        public Credit ToCredit() =>
            new(AssetHex, PersonHex, PersonName, PersonQid, CreditKey, BillingOrder, Role);
    }

    private sealed class PlanRow
    {
        public string Detail { get; set; } = "";
    }

    private sealed class NumberedCreditRow : CreditRow
    {
        public int SetNumber { get; set; }
    }
}
