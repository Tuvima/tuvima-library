using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>Reordering the videos of a series by hand: saved in Tuvima's own data, whole lists only, never a dropped video.</summary>
public sealed class VideoOrderRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_video_order_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly HierarchyResolver _resolver;
    private readonly VideoOrderRepository _order;

    public VideoOrderRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _resolver = new HierarchyResolver(new WorkRepository(_db));
        _order = new VideoOrderRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    private async Task<(Guid Work, Guid Parent)> AddVideoAsync(string title, int episode, string? section = null, int sectionIndex = 1)
    {
        var meta = new Dictionary<string, string>
        {
            [MetadataFieldConstants.VideoSeriesKey] = "course",
            [MetadataFieldConstants.VideoEpisodeIndex] = episode.ToString(),
        };
        if (section is not null)
        {
            meta[MetadataFieldConstants.VideoSection] = section;
            meta[MetadataFieldConstants.VideoSectionIndex] = sectionIndex.ToString();
        }

        var placed = await _resolver.ResolveAsync(MediaType.Videos, meta);
        var edition = Guid.NewGuid();
        var asset = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work);", new { edition, work = placed.WorkId });
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@asset, @edition, @hash, @path, 'Normal');",
            new { asset, edition, hash = asset.ToString("N"), path = $"course/{title}.mp4" });
        conn.Execute(
            "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@asset, 'title', @title, '2026-01-01');",
            new { asset, title });
        return (placed.WorkId, placed.ParentWorkId!.Value);
    }

    [Fact]
    public async Task Reorder_SavesTheNewOrder_AndLoadReturnsItInThatOrder()
    {
        var a = await AddVideoAsync("Intro", 1);
        var b = await AddVideoAsync("Basics", 2);
        var c = await AddVideoAsync("Advanced", 3);

        var placements = await _order.ReorderAsync(a.Parent, [c.Work, a.Work, b.Work]);

        Assert.NotNull(placements);
        Assert.Equal([1, 2, 3], placements!.Select(p => p.Position));
        var group = Assert.Single(await _order.LoadAsync(a.Work));
        Assert.Equal(["Advanced", "Intro", "Basics"], group.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task Reorder_RefusesAListThatDropsOrInventsAVideo()
    {
        var a = await AddVideoAsync("Intro", 1);
        var b = await AddVideoAsync("Basics", 2);

        Assert.Null(await _order.ReorderAsync(a.Parent, [a.Work]));
        Assert.Null(await _order.ReorderAsync(a.Parent, [a.Work, Guid.NewGuid()]));
        Assert.Null(await _order.ReorderAsync(a.Parent, [a.Work, a.Work]));
        var group = Assert.Single(await _order.LoadAsync(b.Work));
        Assert.Equal(["Intro", "Basics"], group.Entries.Select(e => e.Title));
    }

    [Fact]
    public async Task Load_OfASeriesWithSections_GivesOneGroupPerSection()
    {
        var one = await AddVideoAsync("Welcome", 1, "Getting started", 1);
        await AddVideoAsync("Install", 2, "Getting started", 1);
        var two = await AddVideoAsync("Loops", 1, "Core ideas", 2);

        var groups = await _order.LoadAsync(two.Work);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["Welcome", "Install"], groups[0].Entries.Select(e => e.Title));
        Assert.Equal(["Loops"], groups[1].Entries.Select(e => e.Title));
        Assert.Equal(one.Parent, groups[0].ParentWorkId);
    }

    [Fact]
    public async Task Load_OfSomethingThatIsNotAVideoSeries_IsEmpty()
    {
        Assert.Empty(await _order.LoadAsync(Guid.NewGuid()));
    }
}
