using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Storage;

namespace MediaEngine.Ingestion.Tests;

/// <summary>Issue 997: a frame taken from a video is saved as that video's own picture, like any other picture.</summary>
public sealed class GeneratedVideoStillWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-frame-writer-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly EntityAssetRepository _assets;
    private readonly GeneratedVideoStillWriter _writer;

    public GeneratedVideoStillWriterTests()
    {
        Directory.CreateDirectory(_root);
        _db = new DatabaseConnection(Path.Combine(_root, "test.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _assets = new EntityAssetRepository(_db);
        _writer = new GeneratedVideoStillWriter(
            _assets,
            new CanonicalValueRepository(_db),
            new AssetPathService(Path.Combine(_root, "library"), dataRoot: Path.Combine(_root, "data")));
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The scratch folder is in the temp folder; a file still open there is cleaned up with it later.
        }
    }

    [Fact]
    public async Task AFrame_IsSavedAsThePreferredGeneratedPictureOfTheVideo()
    {
        var work = Guid.NewGuid();

        var outcome = await _writer.SaveAsync(work, Frame(1));

        Assert.Equal(GeneratedStillOutcome.Saved, outcome);
        var stored = await _assets.GetPreferredAsync(work.ToString(), "EpisodeStill");
        Assert.NotNull(stored);
        Assert.Equal("generated", stored.SourceProvider);
        Assert.True(File.Exists(stored.LocalImagePath));
    }

    [Fact]
    public async Task TheSameFrameTwice_IsReusedNotSavedAgain()
    {
        var work = Guid.NewGuid();
        await _writer.SaveAsync(work, Frame(1));

        var outcome = await _writer.SaveAsync(work, Frame(1));

        Assert.Equal(GeneratedStillOutcome.Reused, outcome);
        Assert.Single(await _assets.GetByEntityAsync(work.ToString(), "EpisodeStill"));
    }

    [Fact]
    public async Task ARealStill_IsNeverReplacedByAGeneratedFrame()
    {
        var work = Guid.NewGuid();
        var real = new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = work.ToString(),
            EntityType = "Work",
            AssetTypeValue = "EpisodeStill",
            AssetClassValue = "Artwork",
            StorageLocationValue = "Central",
            OwnerScope = "Work",
            ImageUrl = "https://example.test/still.jpg",
            SourceProvider = "tvdb",
            LocalImagePath = Path.Combine(_root, "real.jpg"),
            IsPreferred = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllBytesAsync(real.LocalImagePath, Frame(9));
        await _assets.UpsertAsync(real);
        await _assets.SetPreferredAsync(real.Id);

        var outcome = await _writer.SaveAsync(work, Frame(1));

        Assert.Equal(GeneratedStillOutcome.KeptRealStill, outcome);
        Assert.Equal(real.Id, (await _assets.GetPreferredAsync(work.ToString(), "EpisodeStill"))!.Id);
    }

    private static byte[] Frame(int seed)
    {
        using var bitmap = new SkiaSharp.SKBitmap(64, 36);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        canvas.Clear(new SkiaSharp.SKColor((byte)(seed * 20), 90, 160));
        using var data = SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}
