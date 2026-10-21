using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage.Tests;

public sealed class EntityAssetTinyRenditionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly EntityAssetRepository _assets;

    public EntityAssetTinyRenditionTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_entity_asset_tiny_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _assets = new EntityAssetRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task Upsert_RoundTripsTheTinyCopyPathBesideTheOtherCopies()
    {
        var asset = NewAsset();
        asset.LocalImagePathTiny = "/assets/face-t.jpg";
        asset.LocalImagePathSmall = "/assets/face-s.jpg";

        await _assets.UpsertAsync(asset);
        var stored = await _assets.FindByIdAsync(asset.Id);

        Assert.NotNull(stored);
        Assert.Equal("/assets/face-t.jpg", stored.LocalImagePathTiny);
        Assert.Equal("/assets/face-s.jpg", stored.LocalImagePathSmall);
    }

    [Fact]
    public async Task UpdateRenditionPaths_ChangesOnlyTheCopyPaths_AndNotThePreferredChoice()
    {
        var asset = NewAsset();
        asset.IsPreferred = true;
        await _assets.UpsertAsync(asset);

        await _assets.UpdateRenditionPathsAsync(asset.Id, "/assets/t.jpg", "/assets/s.jpg", "/assets/m.jpg", "/assets/l.jpg");
        var stored = await _assets.FindByIdAsync(asset.Id);

        Assert.NotNull(stored);
        Assert.Equal("/assets/t.jpg", stored.LocalImagePathTiny);
        Assert.Equal("/assets/s.jpg", stored.LocalImagePathSmall);
        Assert.Equal("/assets/m.jpg", stored.LocalImagePathMedium);
        Assert.Equal("/assets/l.jpg", stored.LocalImagePathLarge);
        Assert.Equal("/assets/face.jpg", stored.LocalImagePath);
        Assert.Equal(asset.Id, (await _assets.GetPreferredAsync(asset.EntityId, "Headshot"))!.Id);
    }

    [Fact]
    public async Task DeleteVariant_ReportsTheTinyCopyAsAnUnusedFile()
    {
        var asset = NewAsset();
        asset.LocalImagePathTiny = Path.Combine(Path.GetTempPath(), "tuvima-deleted-face-t.jpg");
        asset.LocalImagePathSmall = Path.Combine(Path.GetTempPath(), "tuvima-deleted-face-s.jpg");
        await _assets.UpsertAsync(asset);

        var unused = await _assets.DeleteVariantAsync(asset.Id);

        Assert.Contains(asset.LocalImagePathTiny, unused);
        Assert.Contains(asset.LocalImagePathSmall, unused);
    }

    [Fact]
    public async Task AssetFileReferences_CountTheTinyCopyAsInUse()
    {
        var asset = NewAsset();
        asset.LocalImagePathTiny = Path.Combine(Path.GetTempPath(), "tuvima-kept-face-t.jpg");
        await _assets.UpsertAsync(asset);

        using var connection = _db.CreateConnection();
        var referenced = AssetFileReferences.LoadReferencedPaths(connection);

        Assert.True(AssetFileReferences.IsReferenced(referenced, asset.LocalImagePathTiny));
    }

    private static EntityAsset NewAsset() => new()
    {
        Id = Guid.NewGuid(),
        EntityId = Guid.NewGuid().ToString(),
        EntityType = "Person",
        AssetTypeValue = "Headshot",
        ImageUrl = "https://example.test/face.jpg",
        LocalImagePath = "/assets/face.jpg",
        SourceProvider = "wikidata",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
