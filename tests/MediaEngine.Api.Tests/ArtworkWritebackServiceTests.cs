using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ArtworkWritebackServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_artwork_writeback_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ConfigurationDirectoryLoader _configuration;

    public ArtworkWritebackServiceTests()
    {
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
    }

    [Fact]
    public async Task ExistingMetadataWritebackDoesNotEnableArtworkMutation()
    {
        var assetId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works (id,media_type) VALUES (@id,'Music')", new { id = workId });
            connection.Execute("INSERT INTO editions (id,work_id) VALUES (@id,@workId)", new { id = editionId, workId });
            connection.Execute("""
                INSERT INTO media_assets (id,edition_id,content_hash,file_path_root)
                VALUES (@id,@editionId,@hash,@path)
                """, new { id = assetId, editionId, hash = Guid.NewGuid().ToString("N"), path = Path.Combine(_root, "track.mp3") });
        }

        var artwork = new ArtworkAssetService(_database, new AssetPathService(_root), new EmptyHttpFactory());
        var service = CreateService(artwork, new EmptyLibraryResolver());

        Assert.Null(await service.GetStatusAsync(Guid.NewGuid(), CancellationToken.None));
        var status = await service.GetStatusAsync(assetId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("Disabled", status.Status);
        Assert.Null(status.EmbeddedArtworkAssetId);
        Assert.Equal(0, await service.SweepOnceAsync(40, CancellationToken.None));
    }

    [Fact]
    public async Task TheSweep_PutsAFileThatNeedsItsCoverOnTheWriteList_WritesNothing_AndDoesNotQueueItAgainWhileItWaits()
    {
        var managed = Directory.CreateDirectory(Path.Combine(_root, "managed")).FullName;
        var track = Path.Combine(managed, "track.mp3");
        File.WriteAllBytes(track, [1, 2, 3]);
        var cover = Path.Combine(_root, "cover.jpg");
        File.WriteAllBytes(cover, [9, 9, 9]);
        var assetId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var artworkId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO works (id,media_type) VALUES (@id,'Music')", new { id = workId });
            connection.Execute("INSERT INTO editions (id,work_id) VALUES (@id,@workId)", new { id = editionId, workId });
            connection.Execute("""
                INSERT INTO media_assets (id,edition_id,content_hash,file_path_root) VALUES (@id,@editionId,@hash,@path);
                INSERT INTO artwork_assets (id, content_hash, original_path, width_px, height_px, aspect_class, source_provider, created_at)
                VALUES (@artworkId, @artworkHash, @cover, 500, 500, 'Square', 'test', @now);
                INSERT INTO artwork_asset_context
                    (artwork_asset_id, entity_id, entity_type, entity_label, media_type, year, role, provider, canonical_id, search_text, created_at)
                VALUES (@artworkId, @workId, 'Work', 'Album', 'Music', '1997', 'Primary', 'test', 'Q1', 'Album', @now);
                INSERT INTO entity_artwork_links
                    (id, entity_id, entity_type, artwork_asset_id, role, context, source_asset_type, is_preferred, created_at)
                VALUES (@linkId, @workId, 'Work', @artworkId, 'Primary', '', 'CoverArt', 1, @now);
                """, new
            {
                id = assetId,
                editionId,
                hash = Guid.NewGuid().ToString("N"),
                path = track,
                artworkId,
                artworkHash = "hash-cover",
                cover,
                workId,
                linkId = Guid.NewGuid(),
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        }
        _configuration.SaveConfig(string.Empty, "writeback", new WriteBackConfiguration { Enabled = true, ArtworkEnabled = true });
        var library = new LibraryFolderEntry
        {
            Id = "music",
            Name = "Music",
            PrimaryDestinationSourceId = "managed",
            Sources =
            [
                new LibrarySourceEntry
                {
                    Id = "managed",
                    Path = managed,
                    ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                    AccessMode = LibrarySourceAccessModes.Writable,
                },
            ],
        };
        var artwork = new ArtworkAssetService(_database, new AssetPathService(_root), new EmptyHttpFactory());
        var service = CreateService(artwork, new SingleSourceResolver(new ResolvedLibrarySource(library, library.Sources[0])));

        Assert.Equal("Pending", (await service.GetStatusAsync(assetId, CancellationToken.None))!.Status);

        Assert.Equal(1, await service.SweepOnceAsync(40, CancellationToken.None));
        Assert.Equal(0, await service.SweepOnceAsync(40, CancellationToken.None));
        Assert.Equal(1, await service.SweepOnceAsync(40, CancellationToken.None));

        using var check = _database.CreateConnection();
        var row = check.QuerySingle<(string Trigger, long Generation, string Status)>(
            "SELECT trigger AS Trigger, generation AS Generation, status AS Status FROM media_file_write_intents WHERE asset_id=@assetId;",
            new { assetId });
        Assert.Equal("artwork", row.Trigger);
        Assert.Equal(1, row.Generation);
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, check.ExecuteScalar<int>("SELECT COUNT(*) FROM media_artwork_writeback;"));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(track));
    }

    [Fact]
    public async Task TheMediaAssetWriter_WritesTheTagsOfEveryFileInTheBatch_AndReportsEachOutcome()
    {
        var artwork = new ArtworkAssetService(_database, new AssetPathService(_root), new EmptyHttpFactory());
        var tags = new SpyTagWriter();
        var writer = new MediaAssetFileWriter(
            tags, CreateService(artwork, new EmptyLibraryResolver()), _configuration, NullLogger<MediaAssetFileWriter>.Instance);
        var good = new MediaFileWriteIntent { Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), Trigger = "manual_override", MediaType = "Movies" };
        var bad = new MediaFileWriteIntent { Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), Trigger = "auto_match", MediaType = "Movies" };
        tags.Throwing.Add(bad.AssetId);

        var results = await writer.WriteBatchAsync([good, bad], CancellationToken.None);

        Assert.Equal([good.AssetId, bad.AssetId], tags.Asked.Select(call => call.AssetId));
        Assert.Equal("manual_override", tags.Asked[0].Trigger);
        Assert.Equal(WriteBackOutcomeKind.Verified, results.Single(result => result.IntentId == good.Id).Outcome.Kind);
        var failed = results.Single(result => result.IntentId == bad.Id).Outcome;
        Assert.Equal(WriteBackOutcomeKind.Failed, failed.Kind);
        Assert.Equal("The tagger fell over.", failed.Reason);
    }

    [Fact]
    public async Task TheMediaAssetWriter_LeavesArtworkAloneUnlessItWasSwitchedOn()
    {
        var artwork = new ArtworkAssetService(_database, new AssetPathService(_root), new EmptyHttpFactory());
        var writer = new MediaAssetFileWriter(
            new SpyTagWriter(), CreateService(artwork, new EmptyLibraryResolver()), _configuration, NullLogger<MediaAssetFileWriter>.Instance);
        var music = new MediaFileWriteIntent { Id = Guid.NewGuid(), AssetId = Guid.NewGuid(), Trigger = "artwork", MediaType = "Music" };

        var result = Assert.Single(await writer.WriteBatchAsync([music], CancellationToken.None));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM media_artwork_writeback;"));
    }

    private ArtworkWritebackService CreateService(ArtworkAssetService artwork, ILibraryFolderResolver libraries)
    {
        var intents = new MediaFileWriteIntentRepository(_database);
        return new ArtworkWritebackService(_database, artwork, _configuration,
            libraries, new SourceMutationPolicyGate(), [], new SystemActivityRepository(_database),
            new FileWriteQueue(intents, _configuration, NullLogger<FileWriteQueue>.Instance), intents,
            NullLogger<ArtworkWritebackService>.Instance);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class EmptyHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class SpyTagWriter : IWriteBackOutcomeService
    {
        public List<(Guid AssetId, string Trigger)> Asked { get; } = [];

        public HashSet<Guid> Throwing { get; } = [];

        public Task WriteMetadataAsync(Guid assetId, string trigger, CancellationToken ct = default) =>
            WriteMetadataWithOutcomeAsync(assetId, trigger, ct);

        public Task<WriteBackOutcome> WriteMetadataWithOutcomeAsync(Guid assetId, string trigger, CancellationToken ct = default)
        {
            Asked.Add((assetId, trigger));
            return Throwing.Contains(assetId)
                ? throw new InvalidOperationException("The tagger fell over.")
                : Task.FromResult(WriteBackOutcome.Verified());
        }
    }

    private sealed class SingleSourceResolver(ResolvedLibrarySource source) : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => source.Library;
        public LibraryFolderEntry? ResolveForPath(string absolutePath) => source.Library;
        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) => source;
        public string? ResolveSourcePath(string absolutePath) => source.Source.Path;
    }

    private sealed class EmptyLibraryResolver : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => null;
        public LibraryFolderEntry? ResolveForPath(string absolutePath) => null;
        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) => null;
        public string? ResolveSourcePath(string absolutePath) => null;
    }
}
