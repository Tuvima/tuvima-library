using MediaEngine.Api.Services.Artwork;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Helpers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

public sealed class ArtworkRenditionServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_rendition_service_{Guid.NewGuid():N}");
    private readonly AssetPathService _paths;
    private readonly DatabaseConnection _db;
    private readonly EntityAssetRepository _assets;
    private readonly ArtworkRenditionQueue _queue = new();
    private readonly ArtworkRenditionService _service;

    public ArtworkRenditionServiceTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_tempRoot);
        _paths = new AssetPathService(Path.Combine(_tempRoot, "library"));
        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _assets = new EntityAssetRepository(_db);
        _service = new ArtworkRenditionService(_assets, _paths, _queue, NullLogger<ArtworkRenditionService>.Instance);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Resolve_ServesTheExactCopy_WhenItExists()
    {
        var asset = await SeedWithCopiesAsync("Headshot");

        var resolved = await _service.ResolveAsync(asset, "t", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.True(resolved.IsRequestedSize);
        Assert.Equal(asset.LocalImagePathTiny, resolved.Path);
        Assert.False(_queue.IsPending(asset.Id));
    }

    [Theory]
    [InlineData(" S ", "s")]
    [InlineData("M", "m")]
    public async Task Resolve_AcceptsAnySizeCaseWithSpaces(string requested, string expectedKey)
    {
        var asset = await SeedWithCopiesAsync("CoverArt");

        var resolved = await _service.ResolveAsync(asset, requested, CancellationToken.None);

        Assert.Equal(ArtworkVariantHelper.GetRenditionPath(asset, expectedKey), resolved!.Path);
        Assert.True(resolved.IsRequestedSize);
    }

    [Fact]
    public async Task Resolve_ServesTheNextBiggerCopyAndQueuesTheMissingOne_WhenTheAskedCopyIsMissing()
    {
        var asset = await SeedWithCopiesAsync("Headshot");
        File.Delete(asset.LocalImagePathTiny!);

        var resolved = await _service.ResolveAsync(asset, "t", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.False(resolved.IsRequestedSize);
        Assert.Equal(asset.LocalImagePathSmall, resolved.Path);
        Assert.NotEqual(asset.LocalImagePath, resolved.Path);
        Assert.True(_queue.IsPending(asset.Id));
    }

    [Fact]
    public async Task Resolve_MakesTheCopiesFirst_WhenOnlyTheOriginalExists_AndReturnsATinyFileForT()
    {
        var asset = await SeedOriginalOnlyAsync("Headshot");

        var resolved = await _service.ResolveAsync(asset, "t", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.True(resolved.IsRequestedSize);
        Assert.NotEqual(asset.LocalImagePath, resolved.Path);
        Assert.True(LongEdge(resolved.Path) <= 96);
        var stored = await _assets.FindByIdAsync(asset.Id);
        Assert.Equal(resolved.Path, stored!.LocalImagePathTiny);
        Assert.True(File.Exists(stored.LocalImagePathSmall));
        Assert.True(File.Exists(stored.LocalImagePathMedium));
        Assert.True(File.Exists(stored.LocalImagePathLarge));
    }

    [Fact]
    public async Task Resolve_MakesTheCopiesOnce_WhenSeveralRequestsAskForTheSameOriginalOnlyPicture()
    {
        var asset = await SeedOriginalOnlyAsync("Headshot");
        var copies = Enumerable.Range(0, 6).Select(_ => CloneOf(asset)).ToList();

        var results = await Task.WhenAll(copies.Select(copy => _service.ResolveAsync(copy, "s", CancellationToken.None)));

        Assert.All(results, resolved => Assert.True(resolved!.IsRequestedSize));
        Assert.Single(results.Select(resolved => resolved!.Path).Distinct());
    }

    [Fact]
    public async Task Resolve_ServesTheOriginal_WhenNoSizeIsAsked()
    {
        var asset = await SeedWithCopiesAsync("CoverArt");

        var none = await _service.ResolveAsync(asset, null, CancellationToken.None);
        var unknown = await _service.ResolveAsync(asset, "xl", CancellationToken.None);

        Assert.Equal(asset.LocalImagePath, none!.Path);
        Assert.True(none.IsRequestedSize);
        Assert.Equal(asset.LocalImagePath, unknown!.Path);
    }

    [Fact]
    public async Task Resolve_ServesTheOriginalOfALogoAtAnySize_AndMakesNoCopies()
    {
        var asset = await SeedOriginalOnlyAsync("Logo");

        var resolved = await _service.ResolveAsync(asset, "t", CancellationToken.None);

        Assert.Equal(asset.LocalImagePath, resolved!.Path);
        Assert.True(resolved.IsRequestedSize);
        Assert.Null((await _assets.FindByIdAsync(asset.Id))!.LocalImagePathTiny);
    }

    [Fact]
    public async Task Resolve_ServesTheOriginalAsAStandIn_WhenACopyCannotBeMade()
    {
        var path = Path.Combine(_tempRoot, "broken.jpg");
        await File.WriteAllTextAsync(path, "not an image");
        var asset = NewAsset("Headshot", path);
        await _assets.UpsertAsync(asset);

        var resolved = await _service.ResolveAsync(asset, "t", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(path, resolved.Path);
        Assert.False(resolved.IsRequestedSize);
    }

    [Fact]
    public async Task Resolve_ReturnsNothing_WhenThePictureHasNoFileAtAll()
    {
        var asset = NewAsset("Headshot", Path.Combine(_tempRoot, "gone.jpg"));

        Assert.Null(await _service.ResolveAsync(asset, "t", CancellationToken.None));
        Assert.Null(await _service.ResolveAsync(asset, null, CancellationToken.None));
    }

    [Fact]
    public async Task CreateMissingCopies_FillsTheMissingCopiesOfAStoredPicture()
    {
        var asset = await SeedWithCopiesAsync("CoverArt");
        File.Delete(asset.LocalImagePathTiny!);
        File.Delete(asset.LocalImagePathMedium!);

        await _service.CreateMissingCopiesAsync(asset.Id, CancellationToken.None);

        var stored = await _assets.FindByIdAsync(asset.Id);
        Assert.True(File.Exists(stored!.LocalImagePathTiny));
        Assert.True(File.Exists(stored.LocalImagePathMedium));
    }

    [Fact]
    public void Queue_DropsAPictureThatIsAlreadyWaiting_UntilItIsMarkedDone()
    {
        var id = Guid.NewGuid();

        Assert.True(_queue.TryEnqueue(id));
        Assert.False(_queue.TryEnqueue(id));
        _queue.MarkDone(id);
        Assert.True(_queue.TryEnqueue(id));
    }

    private async Task<EntityAsset> SeedWithCopiesAsync(string assetType)
    {
        var asset = NewAsset(assetType, CreateJpeg(1000, 1250));
        ArtworkVariantHelper.StampMetadataAndRenditions(asset, _paths);
        await _assets.UpsertAsync(asset);
        return asset;
    }

    private async Task<EntityAsset> SeedOriginalOnlyAsync(string assetType)
    {
        var asset = NewAsset(assetType, CreateJpeg(1000, 1250));
        await _assets.UpsertAsync(asset);
        return asset;
    }

    private static EntityAsset CloneOf(EntityAsset asset) => new()
    {
        Id = asset.Id,
        EntityId = asset.EntityId,
        EntityType = asset.EntityType,
        AssetTypeValue = asset.AssetTypeValue,
        LocalImagePath = asset.LocalImagePath,
        CreatedAt = asset.CreatedAt,
    };

    private static EntityAsset NewAsset(string assetType, string originalPath) => new()
    {
        Id = Guid.NewGuid(),
        EntityId = Guid.NewGuid().ToString(),
        EntityType = "Person",
        AssetTypeValue = assetType,
        ImageUrl = "https://example.test/picture.jpg",
        LocalImagePath = originalPath,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private string CreateJpeg(int width, int height)
    {
        var path = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N") + ".jpg");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [SKColors.Navy, SKColors.Orange], SKShaderTileMode.Clamp) })
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
