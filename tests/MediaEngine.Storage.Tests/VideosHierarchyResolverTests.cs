using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>Videos placement: loose videos stand alone, series folders become parents, sections become numbered parents.</summary>
public sealed class VideosHierarchyResolverTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_videos_hier_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly HierarchyResolver _resolver;

    public VideosHierarchyResolverTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _resolver = new HierarchyResolver(new WorkRepository(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    private static Dictionary<string, string> Meta(string? series, string? section = null, int? sectionIndex = null, int episode = 1)
    {
        var meta = new Dictionary<string, string> { [MetadataFieldConstants.VideoEpisodeIndex] = episode.ToString() };
        if (series is not null)
        {
            meta[MetadataFieldConstants.VideoSeriesKey] = series;
        }

        if (section is not null)
        {
            meta[MetadataFieldConstants.VideoSection] = section;
            meta[MetadataFieldConstants.VideoSectionIndex] = (sectionIndex ?? 1).ToString();
        }

        return meta;
    }

    [Fact]
    public async Task AVideoWithoutASeriesStandsAlone()
    {
        var result = await _resolver.ResolveAsync(MediaType.Videos, Meta(null));

        Assert.Equal(WorkKind.Standalone, result.WorkKind);
        Assert.Null(result.ParentWorkId);
    }

    [Fact]
    public async Task VideosOfOneSeriesShareAParentAndKeepTheirOwnWork()
    {
        var first = await _resolver.ResolveAsync(MediaType.Videos, Meta("python course", episode: 1));
        var second = await _resolver.ResolveAsync(MediaType.Videos, Meta("python course", episode: 2));
        var other = await _resolver.ResolveAsync(MediaType.Videos, Meta("rust course", episode: 1));

        Assert.NotNull(first.ParentWorkId);
        Assert.Equal(first.ParentWorkId, second.ParentWorkId);
        Assert.NotEqual(first.ParentWorkId, other.ParentWorkId);
        Assert.NotEqual(first.WorkId, second.WorkId);
        Assert.Equal(1, first.Ordinal);
        Assert.Equal(2, second.Ordinal);
    }

    [Fact]
    public async Task TwoVideosWithTheSameNumberNeverMergeIntoOneWork()
    {
        var a = await _resolver.ResolveAsync(MediaType.Videos, Meta("mixed", episode: 1));
        var b = await _resolver.ResolveAsync(MediaType.Videos, Meta("mixed", episode: 1));

        Assert.NotEqual(a.WorkId, b.WorkId);
        Assert.Equal(a.ParentWorkId, b.ParentWorkId);
    }

    [Fact]
    public async Task SectionsAreNumberedParentsInsideTheSeries()
    {
        var intro = await _resolver.ResolveAsync(MediaType.Videos, Meta("python course", "Basics", 1, 1));
        var more = await _resolver.ResolveAsync(MediaType.Videos, Meta("python course", "Basics", 1, 2));
        var advanced = await _resolver.ResolveAsync(MediaType.Videos, Meta("python course", "Advanced", 2, 1));

        Assert.Equal(intro.ParentWorkId, more.ParentWorkId);
        Assert.NotEqual(intro.ParentWorkId, advanced.ParentWorkId);

        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT parent_work_id) FROM works WHERE id IN (@a, @b) AND parent_work_id IS NOT NULL;";
        cmd.Parameters.AddWithValue("@a", GuidSql.ToBlob(intro.ParentWorkId!.Value));
        cmd.Parameters.AddWithValue("@b", GuidSql.ToBlob(advanced.ParentWorkId!.Value));
        Assert.Equal(1L, cmd.ExecuteScalar());
    }
}
