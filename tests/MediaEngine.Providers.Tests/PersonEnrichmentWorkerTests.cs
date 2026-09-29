using System.Net;
using System.Text;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

public sealed class PersonEnrichmentWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public PersonEnrichmentWorkerTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_person_enrichment_{Guid.NewGuid():N}.db");
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
    public void BuildTmdbImageHints_KeepsCompanionDataBoundToEachContributor()
    {
        var claims = new List<ProviderClaim>
        {
            new("director", "First Director", 0.9),
            new("director_tmdb_id", "101", 0.9),
            new("director_profile_url", "https://images.example/first.jpg", 0.9),
            new("director", "Second Director", 0.9),
            new("director_tmdb_id", "202", 0.9),
            new("director", "Third Director", 0.9),
            new("director_profile_url", "https://images.example/third.jpg", 0.9),
        };

        var hints = PersonEnrichmentWorker.BuildTmdbImageHints(claims);

        var first = hints[$"Director::{RetailHints.NormalizePersonNameKey("First Director")}"];
        Assert.Equal(101, first.PersonId);
        Assert.Equal("https://images.example/first.jpg", first.ProfileUrl);

        var second = hints[$"Director::{RetailHints.NormalizePersonNameKey("Second Director")}"];
        Assert.Equal(202, second.PersonId);
        Assert.Null(second.ProfileUrl);

        var third = hints[$"Director::{RetailHints.NormalizePersonNameKey("Third Director")}"];
        Assert.Null(third.PersonId);
        Assert.Equal("https://images.example/third.jpg", third.ProfileUrl);
    }

    [Fact]
    public void ResolvePersonWorkTitleHint_AudiobookPrefersBookIdentityOverSegmentTitle()
    {
        var values = new List<CanonicalValue>
        {
            new() { Key = MetadataFieldConstants.Title, Value = "Part 01" },
            new() { Key = "book_title", Value = "Project Hail Mary" },
            new() { Key = MetadataFieldConstants.Album, Value = "Project Hail Mary" },
        };

        var title = PersonEnrichmentWorker.ResolvePersonWorkTitleHint(values, MediaType.Audiobooks);

        Assert.Equal("Project Hail Mary", title);
    }

    [Fact]
    public async Task EnrichActorCharacterMappingsAsync_DoesNotPairIndependentCanonicalArraysByPosition()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var workQid = "Q172241";
        var personRepo = new PersonRepository(_db);
        var fictionalRepo = new FictionalEntityRepository(_db);
        var canonicalRepo = new CanonicalValueRepository(_db);
        var arrayRepo = new CanonicalValueArrayRepository(_db);

        var person = await personRepo.CreateAsync(new Person
        {
            Name = "Tim Robbins",
            WikidataQid = "Q95048",
            Roles = ["Actor"],
        });

        InsertOwnedMovie(workId, editionId, assetId);

        await arrayRepo.SetValuesAsync(workId, "cast_member",
        [
            new CanonicalArrayEntry
            {
                Ordinal = 0,
                Value = "Tim Robbins",
                ValueQid = "Q95048",
            },
        ]);
        await arrayRepo.SetValuesAsync(workId, "characters",
        [
            new CanonicalArrayEntry
            {
                Ordinal = 0,
                Value = "Andy Dufresne",
                ValueQid = "Q56240620",
            },
        ]);

        var worker = new PersonEnrichmentWorker(
            new MetadataClaimRepository(_db),
            canonicalRepo,
            new StubRecursiveIdentityService(),
            new StubHarvestingService(),
            personRepo,
            fictionalRepo,
            new CollectionRepository(_db),
            NullLogger<PersonEnrichmentWorker>.Instance,
            canonicalArrayRepo: arrayRepo);

        await worker.EnrichActorCharacterMappingsAsync(assetId, workQid, CancellationToken.None);

        var character = await fictionalRepo.FindByQidAsync("Q56240620");
        Assert.Null(character);
        var links = await personRepo.GetCharacterLinksAsync(person.Id);
        Assert.Empty(links);
    }

    [Fact]
    public async Task TvActorWithWikidataIdentity_UsesTvdbPersonDetails()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        InsertOwnedMovie(workId, editionId, assetId);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE works SET media_type = 'TV' WHERE id = @id", new { id = GuidSql.ToBlob(workId) });
            conn.Execute("INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled) VALUES (@id, 'tvdb', '1.0', 1)",
                new { id = GuidSql.ToBlob(WellKnownProviders.Tvdb) });
        }

        var personRepo = new PersonRepository(_db);
        var person = await personRepo.CreateAsync(new Person
        {
            Name = "TV Actor", WikidataQid = "Q12345", Roles = ["Actor"],
        });
        var claims = new MetadataClaimRepository(_db);
        await claims.InsertBatchAsync(
        [
            new MetadataClaim { Id = Guid.NewGuid(), EntityId = workId, ProviderId = WellKnownProviders.Tvdb,
                ClaimKey = MetadataFieldConstants.CastMember, ClaimValue = "TV Actor", ClaimedAt = DateTimeOffset.UtcNow },
            new MetadataClaim { Id = Guid.NewGuid(), EntityId = workId, ProviderId = WellKnownProviders.Tvdb,
                ClaimKey = "cast_member_qid", ClaimValue = "Q12345", ClaimedAt = DateTimeOffset.UtcNow },
            new MetadataClaim { Id = Guid.NewGuid(), EntityId = workId, ProviderId = WellKnownProviders.Tvdb,
                ClaimKey = "cast_member_tvdb_identity", ClaimValue = "789::TV Actor", ClaimedAt = DateTimeOffset.UtcNow },
            new MetadataClaim { Id = Guid.NewGuid(), EntityId = workId, ProviderId = WellKnownProviders.Tvdb,
                ClaimKey = MetadataFieldConstants.CastMember, ClaimValue = "Legacy Actor", ClaimedAt = DateTimeOffset.UtcNow },
            new MetadataClaim { Id = Guid.NewGuid(), EntityId = workId, ProviderId = WellKnownProviders.Tvdb,
                ClaimKey = "cast_member_tmdb_id", ClaimValue = "555", ClaimedAt = DateTimeOffset.UtcNow },
        ]);
        var canonicals = new CanonicalValueRepository(_db);
        await canonicals.UpsertBatchAsync([new CanonicalValue
        {
            EntityId = workId, Key = "media_type", Value = "TV",
            WinningProviderId = WellKnownProviders.Tvdb, LastScoredAt = DateTimeOffset.UtcNow,
        }]);
        var configDir = _dbPath + ".config";
        var loader = new ConfigurationDirectoryLoader(configDir);
        loader.SaveProvider(new MediaEngine.Domain.Configuration.ProviderConfiguration
        {
            Name = "tvdb", Enabled = true,
            Endpoints = new Dictionary<string, string> { ["api"] = "https://api4.thetvdb.com/v4" },
            HttpClient = new HttpClientConfig { ApiKey = "installation-key" },
        });
        var client = new TvdbRetailClient(loader, new TvdbPersonFactory(), new ProviderRateLimiterCoordinator());
        var bridges = new BridgeIdRepository(_db);
        var worker = new PersonEnrichmentWorker(claims, canonicals,
            new StubRecursiveIdentityService(), new StubHarvestingService(), personRepo,
            new FictionalEntityRepository(_db), new CollectionRepository(_db),
            NullLogger<PersonEnrichmentWorker>.Instance,
            bridgeIds: bridges, tvdbClient: client);

        await worker.EnrichFromClaimsAsync(assetId, CancellationToken.None);

        var updated = await personRepo.FindByIdAsync(person.Id);
        Assert.Equal("Biography from TheTVDB", updated?.Biography);
        Assert.Contains(await bridges.FindByValueAsync(BridgeIdKeys.TvdbPersonId, "789"),
            entry => entry.EntityId == person.Id);
        Assert.Empty(await bridges.FindByValueAsync(BridgeIdKeys.TmdbPersonId, "555"));
        Directory.Delete(configDir, recursive: true);
    }

    private void InsertOwnedMovie(Guid workId, Guid editionId, Guid assetId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO works (id, media_type, work_kind)
                VALUES ($workId, 'Movies', 'standalone');
            INSERT INTO editions (id, work_id)
                VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
                VALUES ($assetId, $editionId, $hash, 'C:/library/The Shawshank Redemption.mkv');
            """;
        AddGuid(cmd, "$workId", workId);
        AddGuid(cmd, "$editionId", editionId);
        AddGuid(cmd, "$assetId", assetId);
        cmd.Parameters.AddWithValue("$hash", $"asset-{assetId:N}");
        cmd.ExecuteNonQuery();
    }

    private static void AddGuid(Microsoft.Data.Sqlite.SqliteCommand command, string name, Guid value)
    {
        command.Parameters.Add(name, Microsoft.Data.Sqlite.SqliteType.Blob).Value = GuidSql.ToBlob(value);
    }

    private sealed class StubRecursiveIdentityService : IRecursiveIdentityService
    {
        public Task<IReadOnlyList<HarvestRequest>> EnrichAsync(
            Guid mediaAssetId,
            IReadOnlyList<PersonReference> persons,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HarvestRequest>>([]);
    }

    private sealed class StubHarvestingService : IMetadataHarvestingService
    {
        public int PendingCount => 0;

        public ValueTask EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public Task ProcessSynchronousAsync(HarvestRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TvdbPersonFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TvdbPersonHandler());
    }

    private sealed class TvdbPersonHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal)
                ? """{"data":{"token":"test-token"}}"""
                : """{"data":{"id":789,"biographies":[{"language":"eng","biography":"Biography from TheTVDB"}]}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
