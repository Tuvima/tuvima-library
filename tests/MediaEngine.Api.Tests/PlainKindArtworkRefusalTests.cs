using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Asking for provider artwork for an item whose library never looks anything up (a plain kind) is refused before any provider is
/// contacted, whatever the library's own policy field says (Library setup D38).
/// </summary>
public sealed class PlainKindArtworkRefusalTests : IDisposable
{
    private const string PlainLibrary = "eeeeeeee-0000-4000-8000-000000000001";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima-artwork-plain-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ArtworkScopeService _service;
    private readonly Guid _plainWork = Guid.NewGuid();

    public PlainKindArtworkRefusalTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        Seed(_plainWork, PlainLibrary);

        // The fields say "enriched" on purpose: the kind alone must stop the lookup.
        var loader = new LibrariesOnlyLoader(
            new LibraryFolderConfig { Id = PlainLibrary, Name = "Recordings", KindKey = "other_audio", MetadataPolicy = LibraryMetadataPolicies.Enriched });
        _service = new ArtworkScopeService(
            null!, null!, null!, null!,
            new MediaAssetRepository(_database),
            new MetadataEditorRepository(_database),
            new AssetPathService(Path.Combine(Path.GetTempPath(), "tuvima-artwork-plain-assets")),
            loader);
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_databasePath);
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task ProviderArtwork_IsSkippedAsLookupBlocked_ForAnItemInAnOtherAudioLibrary()
    {
        var target = await _service.ResolveProviderArtworkRefreshTargetAsync(Scope(_plainWork), CancellationToken.None);

        Assert.Equal("external_lookup_blocked", target.Skipped!.SkippedReason);
        Assert.Contains("never looks anything up", target.Skipped.Message, StringComparison.Ordinal);
    }

    private static MetadataEndpoints.EditorScopeResolution Scope(Guid workId) => new(
        "item", "Item", 0, workId, "Work", workId, "Work", "Title", null, "Title", "item", null, null,
        CanEditFields: true, CanEditArtwork: true, MediaType: "Music", ArtworkFolderPath: null, RepresentativeMediaFilePath: null);

    private void Seed(Guid work, string libraryId)
    {
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind) VALUES (@work, 'Music', 'standalone');
            INSERT INTO editions (id, work_id) VALUES (@edition, @work);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id)
            VALUES (@asset, @edition, @hash, @path, 'Normal', @libraryId);
            """,
            new { work, edition, asset, libraryId, hash = asset.ToString("N"), path = $"music/{asset:N}.flac" });
    }

    private sealed class LibrariesOnlyLoader : IConfigurationLoader
    {
        public LibrariesOnlyLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; }

        public LibrariesConfiguration LoadLibraries() => Libraries;

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
