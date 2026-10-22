using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using MediaEngine.Storage;
using SkiaSharp;

namespace MediaEngine.Providers.Tests;

public sealed class ArtworkRenditionCopiesTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_rendition_copies_{Guid.NewGuid():N}");
    private readonly AssetPathService _paths;
    private readonly DatabaseConnection _db;
    private readonly EntityAssetRepository _assets;

    public ArtworkRenditionCopiesTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_tempRoot);
        _paths = new AssetPathService(Path.Combine(_tempRoot, "library"));
        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _assets = new EntityAssetRepository(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void StampMetadataAndRenditions_WritesTinySmallMediumAndLargeCopies_WithTinyAtMost96Pixels()
    {
        var asset = NewAsset("Headshot", CreateJpeg("face.jpg", 1000, 1250));

        ArtworkVariantHelper.StampMetadataAndRenditions(asset, _paths);

        Assert.All(
            new[] { asset.LocalImagePathTiny, asset.LocalImagePathSmall, asset.LocalImagePathMedium, asset.LocalImagePathLarge },
            path => Assert.True(File.Exists(path), path));
        Assert.True(LongEdge(asset.LocalImagePathTiny!) <= 96);
        Assert.Equal(96, LongEdge(asset.LocalImagePathTiny!));
        Assert.Equal(320, LongEdge(asset.LocalImagePathSmall!));
        Assert.Equal(960, LongEdge(asset.LocalImagePathMedium!));
        Assert.Equal(1250, LongEdge(asset.LocalImagePathLarge!));
    }

    [Fact]
    public void StampMetadataAndRenditions_MakesNoCopies_ForLogos()
    {
        var asset = NewAsset("Logo", CreateJpeg("logo.jpg", 600, 200));

        ArtworkVariantHelper.StampMetadataAndRenditions(asset, _paths);

        Assert.Null(asset.LocalImagePathTiny);
        Assert.Null(asset.LocalImagePathSmall);
        Assert.False(ArtworkVariantHelper.HasMissingRenditions(asset));
    }

    [Fact]
    public void EnsureMissingRenditions_MakesOnlyTheMissingCopy_AndLeavesTheOthersUntouched()
    {
        var asset = NewAsset("CoverArt", CreateJpeg("poster.jpg", 1200, 1800));
        ArtworkVariantHelper.StampMetadataAndRenditions(asset, _paths);
        var smallBefore = File.GetLastWriteTimeUtc(asset.LocalImagePathSmall!);
        File.Delete(asset.LocalImagePathTiny!);
        Assert.True(ArtworkVariantHelper.HasMissingRenditions(asset));

        var made = ArtworkVariantHelper.EnsureMissingRenditions(asset, _paths);

        Assert.True(made);
        Assert.True(LongEdge(asset.LocalImagePathTiny!) <= 96);
        Assert.Equal(smallBefore, File.GetLastWriteTimeUtc(asset.LocalImagePathSmall!));
        Assert.False(ArtworkVariantHelper.EnsureMissingRenditions(asset, _paths));
    }

    [Fact]
    public async Task EnsureManagedAsync_RegistersADirectlyDownloadedFace_AsThePreferredHeadshotWithATinyCopy()
    {
        var personId = Guid.NewGuid();
        var facePath = CreateJpeg("headshot.jpg", 1000, 1250);

        var managed = await PersonHeadshotAssets.EnsureManagedAsync(
            personId, facePath, "wikidata", "https://example.test/face.jpg", _assets, _paths, CancellationToken.None);

        Assert.NotNull(managed);
        Assert.True(managed.IsPreferred);
        Assert.Equal("Headshot", managed.AssetTypeValue);
        Assert.True(LongEdge(managed.LocalImagePathTiny!) <= 96);
        var stored = await _assets.GetPreferredAsync(personId.ToString(), "Headshot");
        Assert.NotNull(stored);
        Assert.Equal(managed.Id, stored.Id);
        Assert.Equal(managed.LocalImagePathTiny, stored.LocalImagePathTiny);
    }

    [Fact]
    public async Task EnsureManagedAsync_ReusesTheHeadshotThatAlreadyUsesTheFile_AndCompletesItsMissingCopies()
    {
        var personId = Guid.NewGuid();
        var facePath = CreateJpeg("headshot.jpg", 1000, 1250);
        var first = await PersonHeadshotAssets.EnsureManagedAsync(
            personId, facePath, null, null, _assets, _paths, CancellationToken.None);
        Assert.NotNull(first);
        File.Delete(first.LocalImagePathTiny!);

        var second = await PersonHeadshotAssets.EnsureManagedAsync(
            personId, facePath, null, null, _assets, _paths, CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _assets.GetByEntityAsync(personId.ToString(), "Headshot"));
        var stored = await _assets.FindByIdAsync(first.Id);
        Assert.True(File.Exists(stored!.LocalImagePathTiny));
    }

    [Fact]
    public async Task EnsureManagedAsync_AddsANonPreferredHeadshot_WhenThePersonAlreadyHasAPreferredOne()
    {
        var personId = Guid.NewGuid();
        var preferred = await PersonHeadshotAssets.EnsureManagedAsync(
            personId, CreateJpeg("a.jpg", 500, 600), null, null, _assets, _paths, CancellationToken.None);
        var other = await PersonHeadshotAssets.EnsureManagedAsync(
            personId, CreateJpeg("b.jpg", 500, 600), null, null, _assets, _paths, CancellationToken.None);

        Assert.True(preferred!.IsPreferred);
        Assert.False(other!.IsPreferred);
        Assert.Equal(preferred.Id, (await _assets.GetPreferredAsync(personId.ToString(), "Headshot"))!.Id);
    }

    [Fact]
    public async Task EnsureManagedAsync_ReturnsNull_WhenTheFileIsNotAReadablePicture()
    {
        var path = Path.Combine(_tempRoot, "not-a-picture.jpg");
        await File.WriteAllTextAsync(path, "not an image");

        var managed = await PersonHeadshotAssets.EnsureManagedAsync(
            Guid.NewGuid(), path, null, null, _assets, _paths, CancellationToken.None);

        Assert.Null(managed);
    }

    private EntityAsset NewAsset(string assetType, string originalPath) => new()
    {
        Id = Guid.NewGuid(),
        EntityId = Guid.NewGuid().ToString(),
        EntityType = "Person",
        AssetTypeValue = assetType,
        LocalImagePath = originalPath,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private string CreateJpeg(string name, int width, int height)
    {
        var path = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N") + "-" + name);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.DarkRed, SKColors.Gold], SKShaderTileMode.Clamp) })
        {
            canvas.DrawRect(0, 0, width, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static int LongEdge(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        return Math.Max(bitmap.Width, bitmap.Height);
    }
}
