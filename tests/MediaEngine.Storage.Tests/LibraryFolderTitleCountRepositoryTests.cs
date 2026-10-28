using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class LibraryFolderTitleCountRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_folder_titles_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _db;
    private readonly LibraryFolderTitleCountRepository _counts;

    public LibraryFolderTitleCountRepositoryTests()
    {
        DapperConfiguration.Configure();
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _counts = new LibraryFolderTitleCountRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        DatabaseConnection.ReleasePooledConnections(_dbPath);
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task CountByFolder_CountsEachTitleOnceUnderItsFolder_AndNotASiblingThatSharesTheStartOfTheName()
    {
        AddWork("/media/Movies/Dune.mkv", "/media/Movies/Dune.srt");
        AddWork("/media/Movies/Arrival.mkv");
        AddWork("/media/Movies 4K/Heat.mkv");

        var counts = await _counts.CountByFolderAsync(["/media/Movies", "/media/Movies 4K", "/media/Empty"]);

        Assert.Equal(2, counts["/media/Movies"]);
        Assert.Equal(1, counts["/media/Movies 4K"]);
        Assert.Equal(0, counts["/media/Empty"]);
    }

    [Fact]
    public async Task CountByFolder_LeavesOutOrphanedFilesAndCatalogOnlyTitles()
    {
        AddWork("/media/Movies/Kept.mkv");
        var gone = AddWork("/media/Movies/Gone.mkv");
        using (var connection = _db.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET is_orphaned = 1 WHERE edition_id IN (SELECT id FROM editions WHERE work_id = @gone);", new { gone });
        }

        AddWork("/media/Movies/Wanted.mkv", catalogOnly: true);

        var counts = await _counts.CountByFolderAsync(["/media/Movies"]);

        Assert.Equal(1, counts["/media/Movies"]);
    }

    [Fact]
    public async Task CountByFolder_LeavesOutTitlesThatAreNotReadyYet()
    {
        AddWork("/media/Movies/Dune.mkv");
        AddWork("/media/Movies/Still working.mkv", ready: false);

        var counts = await _counts.CountByFolderAsync(["/media/Movies"]);

        Assert.Equal(1, counts["/media/Movies"]);
    }

    [Fact]
    public async Task CountByFolder_UnderstandsWindowsPaths()
    {
        AddWork(@"E:\Library\Movies\Dune.mkv");
        AddWork(@"E:\Library\Movies Old\Heat.mkv");

        var counts = await _counts.CountByFolderAsync([@"E:\Library\Movies"]);

        Assert.Equal(1, counts[@"E:\Library\Movies"]);
    }

    private Guid AddWork(string firstPath, string? secondPath = null, bool catalogOnly = false, bool ready = true)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        using var connection = _db.CreateConnection();
        connection.Execute(
            "INSERT INTO works (id, media_type, is_catalog_only, ready_at) VALUES (@workId, 'Movies', @catalogOnly, @readyAt); INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);",
            new { workId, editionId, catalogOnly = catalogOnly ? 1 : 0, readyAt = ready ? "2026-10-28T09:00:00.0000000+00:00" : null });
        foreach (var path in new[] { firstPath, secondPath }.OfType<string>())
        {
            connection.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@id, @editionId, @hash, @path);",
                new { id = Guid.NewGuid(), editionId, hash = Guid.NewGuid().ToString("N"), path });
        }

        return workId;
    }
}
