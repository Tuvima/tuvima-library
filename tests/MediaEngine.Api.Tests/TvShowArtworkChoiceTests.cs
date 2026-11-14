using Dapper;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Issue 990, part 1 (universal editor v7 D10): the picture chosen for a show must stay chosen. A show is the root of its seasons and episodes,
/// so it is the one kind of title the artwork handler also guards as a family; the choice has to land where the show page reads its cover.
/// </summary>
public sealed class TvShowArtworkChoiceTests : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, string> NoRefusals = new Dictionary<Guid, string>();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_show_artwork_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly ArtworkAssetService _artwork;
    private readonly EntityAssetRepository _entityAssets;
    private readonly CanonicalValueRepository _canonicals;
    private readonly EditSessionService _service;

    public TvShowArtworkChoiceTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "library.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _artwork = new ArtworkAssetService(_database, new AssetPathService(_root), new NoHttpClientFactory());
        _entityAssets = new EntityAssetRepository(_database);
        _canonicals = new CanonicalValueRepository(_database);
        var handler = new ArtworkEditSessionChangeHandler(
            _artwork,
            new ItemCanonicalRepository(_database, NullLogger<ItemCanonicalRepository>.Instance),
            new SystemActivityRepository(_database),
            new ArtworkCanonicalSync(_canonicals, _entityAssets));
        _service = new EditSessionService([handler], NullLogger<EditSessionService>.Instance);
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch (Exception) { /* test cleanup: the temp folder goes next */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    [Fact]
    public async Task Apply_WhenAShowsPosterIsChosen_TheShowPageReadsTheNewPictureAndTheOldOneIsNoLongerPreferred()
    {
        var show = await SeedShowWithIngestedPosterAsync();

        var result = await _service.RunAsync(
            new EditSessionRequest
            {
                PreviewOnly = false,
                Changes = [new() { Id = "a1", Kind = EditSessionChangeKinds.Artwork, EntityId = show.Id, Slot = "CoverArt", TargetId = show.OtherAssetId.ToString() }],
            },
            NoRefusals);

        Assert.True(result.DataSaved, string.Join("; ", result.Changes.Select(change => change.Reason)));
        // The show page reads the preferred CoverArt row of the show's own work (DetailProjectionReader.LoadManagedWorkCoverUrlAsync).
        var shown = await _entityAssets.GetPreferredAsync(show.Id.ToString("D"), "CoverArt");
        Assert.NotNull(shown);
        Assert.Equal(show.OtherPath, shown!.LocalImagePath);
        Assert.Equal(1, await PreferredRowCountAsync(show.Id));
    }

    [Fact]
    public async Task Apply_WhenAShowsPosterIsChosen_TheCoverTheShowPageAndItsTilesReadNamesTheNewPicture()
    {
        var show = await SeedShowWithIngestedPosterAsync();

        var result = await _service.RunAsync(
            new EditSessionRequest
            {
                PreviewOnly = false,
                Changes = [new() { Id = "a1", Kind = EditSessionChangeKinds.Artwork, EntityId = show.Id, Slot = "CoverArt", TargetId = show.OtherAssetId.ToString() }],
            },
            NoRefusals);

        Assert.True(result.DataSaved, string.Join("; ", result.Changes.Select(change => change.Reason)));
        // A show's page and every tile read the canonical cover address (cover_url and its sized copies), not the entity_assets row. Ingestion
        // wrote the old picture's address there; choosing another picture has to move it, or the show keeps showing the old one.
        var shown = await _entityAssets.GetPreferredAsync(show.Id.ToString("D"), "CoverArt");
        var canonical = (await _canonicals.GetByEntityAsync(show.Id)).ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith($"/stream/artwork/{shown!.Id}", canonical["cover_url"], StringComparison.Ordinal);
        Assert.StartsWith($"/stream/artwork/{shown.Id}", canonical["cover_url_m"], StringComparison.Ordinal);
        Assert.Equal("present", canonical["cover_state"]);
        Assert.Equal("manual", canonical["cover_source"]);
    }

    [Fact]
    public async Task Apply_WhenTheEarlierPictureIsChosenAgain_TheShowShowsItAgain()
    {
        var show = await SeedShowWithIngestedPosterAsync();
        await ApplyAsync(show.Id, show.OtherAssetId.ToString());

        await ApplyAsync(show.Id, show.IngestedAssetId.ToString());

        var shown = await _entityAssets.GetPreferredAsync(show.Id.ToString("D"), "CoverArt");
        Assert.Equal(show.IngestedPath, shown!.LocalImagePath);
        var canonical = (await _canonicals.GetByEntityAsync(show.Id)).ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith($"/stream/artwork/{shown.Id}", canonical["cover_url"], StringComparison.Ordinal);
        Assert.Equal(1, await PreferredRowCountAsync(show.Id));
    }

    [Fact]
    public async Task Apply_WhenNoPictureIsChosen_TheShowCoverIsMarkedMissingAndThePicturesStaySaved()
    {
        var show = await SeedShowWithIngestedPosterAsync();

        await ApplyAsync(show.Id, EditSessionArtworkTargets.None);

        var canonical = (await _canonicals.GetByEntityAsync(show.Id)).ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
        Assert.False(canonical.ContainsKey("cover_url"));
        Assert.Equal("missing", canonical["cover_state"]);
        Assert.Equal(2, (await _artwork.GetEntityAsync("Work", show.Id, CancellationToken.None)).Variants.Count);
    }

    private async Task ApplyAsync(Guid showId, string target)
    {
        var result = await _service.RunAsync(
            new EditSessionRequest
            {
                PreviewOnly = false,
                Changes = [new() { Id = "a1", Kind = EditSessionChangeKinds.Artwork, EntityId = showId, Slot = "CoverArt", TargetId = target }],
            },
            NoRefusals);
        Assert.True(result.DataSaved, string.Join("; ", result.Changes.Select(change => change.Reason)));
    }

    private async Task<(Guid Id, Guid IngestedAssetId, string IngestedPath, Guid OtherAssetId, string OtherPath)> SeedShowWithIngestedPosterAsync()
    {
        var showId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind) VALUES (@showId, 'TV', 'parent');",
                new { showId = GuidSql.ToBlob(showId) });
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES (@seasonId, 'TV', 'parent', @showId);",
                new { seasonId = GuidSql.ToBlob(seasonId), showId = GuidSql.ToBlob(showId) });
        }

        // The poster as ingestion stores it: an entity_assets row on the show plus its canonical picture and link.
        var ingestedPath = Path.Combine(_root, "ingested.jpg");
        var ingestedAsset = new EntityAsset
        {
            EntityId = showId.ToString("D"),
            EntityType = "Work",
            AssetTypeValue = "CoverArt",
            ImageUrl = "https://image.tmdb.org/t/p/original/ingested.jpg",
            LocalImagePath = ingestedPath,
            SourceProvider = "tmdb",
            WidthPx = 1000,
            HeightPx = 1500,
            AspectClass = "Portrait",
            OwnerScope = "Work",
            IsPreferred = true,
        };
        await _entityAssets.UpsertAsync(ingestedAsset);
        // Ingestion also writes the picture's address into the show's canonical values, which is what the show page and its tiles read.
        await _canonicals.UpsertBatchAsync(ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(showId, ingestedAsset, DateTimeOffset.UtcNow));

        // A second picture the person added in the editor (an upload or a provider picture): linked to the show, not chosen.
        var other = Guid.NewGuid();
        var otherPath = Path.Combine(_root, $"{other:N}.jpg");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO artwork_assets (id, content_hash, original_path, width_px, height_px, aspect_class, source_provider, created_at)
                VALUES (@other, @hash, @path, 1000, 1500, 'Portrait', 'user_upload', @createdAt);
                """,
                new { other, hash = $"hash-{other:N}", path = otherPath, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        }

        await _artwork.LinkAsync("Work", showId, new(other, "Primary", Preferred: false, SourceAssetType: "CoverArt"), CancellationToken.None);
        var ingested = await _artwork.GetChosenAsync("Work", showId, "Primary", CancellationToken.None);
        Assert.NotNull(ingested);
        return (showId, ingested!.ArtworkAssetId, ingestedPath, other, otherPath);
    }

    private async Task<int> PreferredRowCountAsync(Guid showId)
    {
        using var connection = _database.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM entity_assets WHERE entity_id = @id AND asset_type = 'CoverArt' AND is_preferred = 1;",
            new { id = showId });
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
