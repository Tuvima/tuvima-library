using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Issue 1044: the hero of an episode's own page uses the same picture the episode list shows. A frame Library took from the video is a
/// stored still with no provider address and no canonical still value, so a hero that read only the canonical value stayed empty.
/// </summary>
public sealed class EpisodeHeroPictureTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("Library");
    private readonly string _images = Path.Combine(Path.GetTempPath(), $"tuvima_episode_hero_{Guid.NewGuid():N}");

    [Fact]
    public async Task AnEpisodeWithOnlyAFrameTakenFromItsVideo_HasThatFrameAsItsHeroPicture()
    {
        var episode = SeedEpisode();
        var frame = await StoreAsync(episode, "generated", "frame");

        var picture = await Composer().ResolveEpisodeHeroPictureAsync(episode, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal($"/stream/artwork/{frame:D}", picture);
    }

    [Fact]
    public async Task AnEpisodeWithAFrameAndAProviderStill_HasTheFrameAsItsHeroPicture()
    {
        var episode = SeedEpisode();
        var frame = await StoreAsync(episode, "generated", "frame");
        var values = new Dictionary<string, string> { ["episode_still_url"] = "https://tvdb.example/still.jpg" };

        var picture = await Composer().ResolveEpisodeHeroPictureAsync(episode, values, CancellationToken.None);

        Assert.Equal($"/stream/artwork/{frame:D}", picture);
    }

    [Fact]
    public async Task AnEpisodeWithNoStoredStill_KeepsTheProvidersStillAsItsHeroPicture()
    {
        var episode = SeedEpisode();
        var values = new Dictionary<string, string> { ["episode_still_url"] = "https://tvdb.example/still.jpg" };

        var picture = await Composer().ResolveEpisodeHeroPictureAsync(episode, values, CancellationToken.None);

        Assert.Equal("https://tvdb.example/still.jpg", picture);
    }

    [Fact]
    public async Task AnEpisodeWithNoPictureAtAll_HasAnEmptyHeroPicture()
    {
        var episode = SeedEpisode();

        var picture = await Composer().ResolveEpisodeHeroPictureAsync(episode, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(string.Empty, picture);
    }

    private DetailCompositionOrchestrator Composer() =>
        new(_world.Database, null!, null!, null!, null!, null!, null!, null!);

    private Guid SeedEpisode()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        _world.File(episode, "Season 01/Severance - S01E01.mkv");
        return episode;
    }

    private async Task<Guid> StoreAsync(Guid workId, string source, string imageName)
    {
        Directory.CreateDirectory(_images);
        var path = Path.Combine(_images, imageName + ".jpg");
        File.WriteAllText(path, "image:" + imageName);
        var variant = new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = workId.ToString(),
            EntityType = "Work",
            AssetTypeValue = "EpisodeStill",
            ImageUrl = string.Empty,
            LocalImagePath = path,
            SourceProvider = source,
            OwnerScope = "Episode",
            IsPreferred = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await new EntityAssetRepository(_world.Database).UpsertAsync(variant);
        return variant.Id;
    }

    public void Dispose()
    {
        _world.Dispose();
        if (Directory.Exists(_images))
        {
            Directory.Delete(_images, recursive: true);
        }
    }
}
