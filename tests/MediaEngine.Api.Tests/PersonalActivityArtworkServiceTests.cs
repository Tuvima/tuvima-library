using Dapper;
using MediaEngine.Api.Services.Activity;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The small copy of a cover kept with an activity entry: at most 240 px on the longest edge, shared by entries with
/// the same source cover, and still there after the item (and its cover) are deleted.
/// </summary>
public sealed class PersonalActivityArtworkServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tuvima-activity-art-{Guid.NewGuid():N}");
    private PersonalActivityWorld _world = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _world = PersonalActivityWorld.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _world.DisposeAsync();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ACover_IsKeptAsASmallCopyNoLongerThan240Pixels()
    {
        var (_, asset) = await SeedMovieAsync(WriteCover("cover.png", 800, 1200));
        var service = PersonalActivityTestKit.CreateArtwork(_world.Database);

        var key = await service.CaptureAsync(asset, default);

        Assert.NotNull(key);
        Assert.Matches("^[0-9a-f]{64}$", key);
        var stored = await _world.Repository.GetArtworkAsync(key);
        Assert.NotNull(stored);
        Assert.Equal(240, Math.Max(stored.Width!.Value, stored.Height!.Value));
        Assert.Equal(160, Math.Min(stored.Width.Value, stored.Height.Value));
        using var decoded = SKBitmap.Decode(stored.Image);
        Assert.Equal(stored.Width, decoded.Width);
        Assert.Equal(stored.Height, decoded.Height);
        Assert.True(stored.ContentType is "image/jpeg" or "image/webp");
    }

    [Fact]
    public async Task ASmallCover_IsNotEnlarged()
    {
        var (_, asset) = await SeedMovieAsync(WriteCover("small.png", 100, 150));
        var service = PersonalActivityTestKit.CreateArtwork(_world.Database);

        var key = await service.CaptureAsync(asset, default);

        var stored = await _world.Repository.GetArtworkAsync(key!);
        Assert.Equal((100, 150), (stored!.Width, stored.Height));
    }

    [Fact]
    public async Task TwoItemsWithTheSameSourceCover_ShareOneStoredPicture()
    {
        var cover = WriteCover("shared.png", 600, 900);
        var (_, first) = await SeedMovieAsync(cover);
        var (_, second) = await SeedMovieAsync(cover);
        var service = PersonalActivityTestKit.CreateArtwork(_world.Database);

        var one = await service.CaptureAsync(first, default);
        var two = await service.CaptureAsync(second, default);
        await service.CaptureAsync(first, default);

        Assert.Equal(one, two);
        using var connection = _world.Database.CreateConnection();
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM personal_activity_artwork;"));
    }

    [Fact]
    public async Task ThePicture_SurvivesDeletingTheAssetTheWorkAndTheCoverFile()
    {
        var cover = WriteCover("gone.png", 600, 900);
        var (work, asset) = await SeedMovieAsync(cover);
        var service = PersonalActivityTestKit.CreateArtwork(_world.Database);
        var key = await service.CaptureAsync(asset, default);
        await _world.AddAsync(_world.MemberA, "watch", "Tremors", DateTimeOffset.UtcNow, artwork: key, work: work);

        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute("DELETE FROM entity_assets;");
            connection.Execute("DELETE FROM media_assets WHERE id = @asset;", new { asset });
            connection.Execute("DELETE FROM editions WHERE work_id = @work;", new { work });
            connection.Execute("DELETE FROM works WHERE id = @work;", new { work });
        }

        File.Delete(cover);

        var picture = await _world.Service.ArtworkAsync(_world.MemberOfA, key!, null, default);
        Assert.True(picture.IsSuccess);
        Assert.NotEmpty(picture.Value!.Image);
    }

    [Fact]
    public async Task AnItemWithNoCover_HasNoPicture_AndAnUnreadableCoverIsReportedAsNone()
    {
        var (_, noCover) = await SeedMovieAsync(null);
        var notAnImage = Path.Combine(_directory, "broken.png");
        await File.WriteAllTextAsync(notAnImage, "this is not an image");
        var (_, broken) = await SeedMovieAsync(notAnImage);
        var service = PersonalActivityTestKit.CreateArtwork(_world.Database);

        Assert.Null(await service.CaptureAsync(noCover, default));
        Assert.Null(await service.CaptureAsync(broken, default));
        Assert.Null(await service.CaptureAsync(Guid.NewGuid(), default));
        Assert.Null(await service.CaptureAsync(Guid.Empty, default));
    }

    private string WriteCover(string name, int width, int height)
    {
        var path = Path.Combine(_directory, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private async Task<(Guid Work, Guid Asset)> SeedMovieAsync(string? coverPath)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works(id,media_type,work_kind,is_catalog_only,ownership) VALUES(@work,'Movies','standalone',0,'Owned');
                INSERT INTO editions(id,work_id) VALUES(@edition,@work);
                INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id) VALUES(@asset,@edition,@hash,@path,@library);
                """, new
            {
                work,
                edition,
                asset,
                hash = asset.ToString("N"),
                path = $"C:/fixtures/{asset:N}.media",
                library = Guid.NewGuid().ToString("D"),
            });
        }

        if (coverPath is not null)
        {
            await new EntityAssetRepository(_world.Database).UpsertAsync(new EntityAsset
            {
                EntityId = work.ToString("D"),
                EntityType = "Work",
                AssetTypeValue = "CoverArt",
                LocalImagePath = coverPath,
                IsPreferred = true,
            });
        }

        return (work, asset);
    }
}
