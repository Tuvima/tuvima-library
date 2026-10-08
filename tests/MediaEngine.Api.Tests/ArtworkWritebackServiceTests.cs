using Dapper;
using MediaEngine.Api.Services;
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
        var service = new ArtworkWritebackService(_database, artwork, _configuration,
            new EmptyLibraryResolver(), new SourceMutationPolicyGate(), [], new SystemActivityRepository(_database),
            NullLogger<ArtworkWritebackService>.Instance);

        Assert.Null(await service.GetStatusAsync(Guid.NewGuid(), CancellationToken.None));
        var status = await service.GetStatusAsync(assetId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("Disabled", status.Status);
        Assert.Null(status.EmbeddedArtworkAssetId);
        Assert.Equal(0, await service.SweepOnceAsync(40, CancellationToken.None));
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

    private sealed class EmptyLibraryResolver : ILibraryFolderResolver
    {
        public LibraryFolderEntry? ResolveById(string libraryId) => null;
        public LibraryFolderEntry? ResolveForPath(string absolutePath) => null;
        public ResolvedLibrarySource? ResolveSourceForPath(string absolutePath) => null;
        public string? ResolveSourcePath(string absolutePath) => null;
    }
}
