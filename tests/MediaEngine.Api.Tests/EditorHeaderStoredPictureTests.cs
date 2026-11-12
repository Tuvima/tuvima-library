using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Issue 871 and Editor v6 WP8: the editor header's cover and the pictures in its episode list. Ingestion and the editor keep a picture under
/// the work, its edition or its media asset, and the same image can be shared between items, so the header has to find and address the
/// picture the way /stream/artwork serves it (an entity_assets id). Pictures here are stored through the real artwork repository on a real
/// temp SQLite data store, the way ingestion stores them.
/// </summary>
public sealed class EditorHeaderStoredPictureTests : IDisposable
{
    private readonly EditorHeaderTestWorld _world = new("Library");
    private readonly string _images = Path.Combine(Path.GetTempPath(), $"tuvima_editor_pictures_{Guid.NewGuid():N}");

    // -- A: the header cover -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMoviesCover_IsAddressedByItsOwnPictureRow_WhenAnotherItemStoredTheSameImageFirst()
    {
        var other = _world.Work("Movies", "standalone", "Arrival (copy)");
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");
        var image = Image("arrival");
        var first = await StoreAsync(other, "Work", "CoverArt", image);
        var own = await StoreAsync(movie, "Work", "CoverArt", image);
        // The first item is later removed; the shared artwork record still names its (now gone) picture row.
        using (var connection = _world.Database.CreateConnection())
        {
            connection.Execute("DELETE FROM entity_assets WHERE id = @first;", new { first });
        }

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.Equal($"/stream/artwork/{own:D}", header!.Root.CoverUrl);
    }

    [Fact]
    public async Task AMoviesCover_ShowsInTheHeader_WhenIngestionStoredItUnderTheWork()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");
        var cover = await StoreAsync(movie, "Work", "CoverArt", Image("arrival"));

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.Equal($"/stream/artwork/{cover:D}", header!.Root.CoverUrl);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("edition")]
    public async Task AMoviesCover_ShowsInTheHeader_WhenItIsStoredUnderItsFileOrEdition(string owner)
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        var asset = _world.File(movie, "Arrival (2016).mkv");
        var ownerId = owner == "asset" ? asset : EditionOf(movie);
        var cover = await StoreAsync(ownerId, owner == "asset" ? "Work" : "Edition", "CoverArt", Image("arrival"));

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.Equal($"/stream/artwork/{cover:D}", header!.Root.CoverUrl);
    }

    [Fact]
    public async Task ABooksCover_ShowsInTheHeader_WhenTheBookStandsOnItsOwn()
    {
        var book = _world.Work("Books", "standalone", "Dune");
        _world.File(book, "Dune.epub");
        var cover = await StoreAsync(book, "Work", "CoverArt", Image("dune"));

        var header = await _world.Header().GetHeaderAsync(book, null);

        Assert.Equal($"/stream/artwork/{cover:D}", header!.Root.CoverUrl);
    }

    [Fact]
    public async Task ABookInASeries_ShowsItsOwnCover_BecauseTheSeriesHasNoPictureOfItsOwn()
    {
        var series = _world.Work("Books", "parent", "Dune Chronicles");
        var first = _world.Work("Books", "child", "Dune", series, 1);
        var second = _world.Work("Books", "child", "Dune Messiah", series, 2);
        _world.File(first, "Dune.epub");
        _world.File(second, "Dune Messiah.epub");
        var cover = await StoreAsync(second, "Work", "CoverArt", Image("messiah"));

        var header = await _world.Header().GetHeaderAsync(second, null);

        Assert.Equal($"/stream/artwork/{cover:D}", header!.Root.CoverUrl);
    }

    [Fact]
    public async Task ATvEpisode_ShowsTheShowsPoster_InTheHeader()
    {
        var (show, _, episode, _) = SeedShow();
        var poster = await StoreAsync(show, "Work", "CoverArt", Image("poster"));

        var header = await _world.Header().GetHeaderAsync(episode, null);

        Assert.Equal($"/stream/artwork/{poster:D}", header!.Root.CoverUrl);
    }

    [Fact]
    public async Task AnEpisodeWhoseShowHasNoPicture_ShowsThePictureKeptUnderItsFile()
    {
        var (_, _, episode, asset) = SeedShow();
        var cover = await StoreAsync(asset, "Work", "CoverArt", Image("episode"));

        var header = await _world.Header().GetHeaderAsync(episode, null);

        Assert.Equal($"/stream/artwork/{cover:D}", header!.Root.CoverUrl);
    }

    [Fact]
    public async Task AnItemWithNoStoredPicture_HasNoCoverAddress_SoTheDashboardKeepsThePagesPicture()
    {
        var movie = _world.Work("Movies", "standalone", "Home Video");
        _world.File(movie, "Home Video.mkv");

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.Null(header!.Root.CoverUrl);
    }

    [Fact]
    public async Task TheCoversColours_AreTheOnesStoredWithThePictureTheHeaderShows()
    {
        var movie = _world.Work("Movies", "standalone", "Arrival");
        _world.File(movie, "Arrival (2016).mkv");
        var cover = await StoreAsync(movie, "Work", "CoverArt", Image("arrival"), primaryHex: "#102030", secondaryHex: "#405060");

        var header = await _world.Header().GetHeaderAsync(movie, null);

        Assert.Equal($"/stream/artwork/{cover:D}", header!.Root.CoverUrl);
        Assert.Equal("#102030", header.Colors!.PrimaryHex);
        Assert.Equal("#405060", header.Colors.SecondaryHex);
    }

    // -- C: pictures in the episode list --------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEpisodeRowWithAStillKeptUnderItsWork_ShowsThatPicture()
    {
        var (_, _, episode, _) = SeedShow();
        var still = await StoreAsync(episode, "Work", "EpisodeStill", Image("still"));

        var header = await _world.Header().GetHeaderAsync(episode, null);

        Assert.Equal($"/stream/artwork/{still:D}", Assert.Single(header!.Level!.Items, item => item.IsCurrent).StillUrl);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("edition")]
    public async Task AnEpisodeRowWithAStillKeptUnderItsFileOrEdition_ShowsThatPicture(string owner)
    {
        var (_, _, episode, asset) = SeedShow();
        var ownerId = owner == "asset" ? asset : EditionOf(episode);
        var still = await StoreAsync(ownerId, owner == "asset" ? "Work" : "Edition", "EpisodeStill", Image("still"));

        var header = await _world.Header().GetHeaderAsync(episode, null);

        Assert.Equal($"/stream/artwork/{still:D}", Assert.Single(header!.Level!.Items, item => item.IsCurrent).StillUrl);
    }

    [Fact]
    public async Task AnEpisodeRowWithNoStoredStill_HasNoPictureAddress_AndKeepsItsPlaceholder()
    {
        var (_, other, episode, asset) = SeedShow();
        await StoreAsync(asset, "Work", "EpisodeStill", Image("still"));

        var header = await _world.Header().GetHeaderAsync(episode, null);

        var items = header!.Level!.Items;
        Assert.NotNull(Assert.Single(items, item => item.Id == episode).StillUrl);
        Assert.Null(Assert.Single(items, item => item.Id == other).StillUrl);
    }

    // -- Seeding --------------------------------------------------------------------------------------------------------------------

    // A show with one season of two episodes; returns the show, the second episode (no stored picture), the first episode and its file.
    private (Guid Show, Guid Other, Guid Episode, Guid Asset) SeedShow()
    {
        var show = _world.Work("TV", "parent", "Severance");
        var season = _world.Work("TV", "parent", "Season 1", show, 1);
        var episode = _world.Work("TV", "child", "Good News About Hell", season, 1);
        var other = _world.Work("TV", "child", "Half Loop", season, 2);
        var asset = _world.File(episode, "Season 01/Severance - S01E01.mkv");
        _world.File(other, "Season 01/Severance - S01E02.mkv");
        return (show, other, episode, asset);
    }

    private Guid EditionOf(Guid workId)
    {
        using var connection = _world.Database.CreateConnection();
        return connection.ExecuteScalar<Guid>("SELECT id FROM editions WHERE work_id = @workId LIMIT 1;", new { workId });
    }

    // A real image file; the same name gives the same bytes, so two items can share one image the way ingestion's content hash sees it.
    private string Image(string name)
    {
        Directory.CreateDirectory(_images);
        var path = Path.Combine(_images, name + ".jpg");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "image:" + name);
        }

        return path;
    }

    // Stores a picture through the real artwork repository, the way ingestion does (a preferred, provider-sourced variant).
    private async Task<Guid> StoreAsync(
        Guid entityId, string entityType, string assetType, string path, string? primaryHex = null, string? secondaryHex = null)
    {
        var variant = new EntityAsset
        {
            Id = Guid.NewGuid(),
            EntityId = entityId.ToString(),
            EntityType = entityType,
            AssetTypeValue = assetType,
            ImageUrl = $"https://images.example/{Guid.NewGuid():N}.jpg",
            LocalImagePath = path,
            LocalImagePathMedium = path,
            SourceProvider = "tmdb",
            PrimaryHex = primaryHex,
            SecondaryHex = secondaryHex,
            OwnerScope = entityType == "Edition" ? "Edition" : "Work",
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
