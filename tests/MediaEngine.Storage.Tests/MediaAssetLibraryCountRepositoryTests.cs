using Dapper;

namespace MediaEngine.Storage.Tests;

/// <summary>Files per library for a folder or a list of files (simpler access D13), on real temp SQLite.</summary>
public sealed class MediaAssetLibraryCountRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_library_counts_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly MediaAssetLibraryCountRepository _counts;
    private readonly Guid _edition = Guid.NewGuid();
    private readonly Guid _movies = Guid.NewGuid();
    private readonly Guid _kids = Guid.NewGuid();

    public MediaAssetLibraryCountRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _counts = new MediaAssetLibraryCountRepository(_database);
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works(id,media_type,work_kind) VALUES(@work,'Movies','child');", new { work });
        connection.Execute("INSERT INTO editions(id,work_id) VALUES(@edition,@work);", new { edition = _edition, work });
    }

    public void Dispose()
    {
        _database.Dispose();
        DatabaseConnection.ReleasePooledConnections(_path);
        File.Delete(_path);
    }

    [Fact]
    public async Task AFolder_CountsOnlyTheFilesInsideIt_PerLibrary_AndASiblingWithTheSameStartIsNotInside()
    {
        AddAsset("/media/kids/a.mkv", _kids);
        AddAsset("/media/kids/deeper/b.mkv", _kids);
        AddAsset(@"\media\kids\c.mkv", _movies);
        AddAsset("/media/kids2/d.mkv", _kids);
        AddAsset("/media/other/e.mkv", _movies);

        var counts = await _counts.CountByLibraryInFolderAsync("/media/kids/", null);

        Assert.Equal(2, counts[_kids]);
        Assert.Equal(1, counts[_movies]);
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public async Task AFolderWithALibrary_CountsOnlyTheFilesInThatLibrary()
    {
        AddAsset("/media/kids/a.mkv", _kids);
        AddAsset("/media/kids/b.mkv", _movies);

        var counts = await _counts.CountByLibraryInFolderAsync("/media/kids", _kids);

        Assert.Equal(new Dictionary<Guid, int> { [_kids] = 1 }, counts);
    }

    [Fact]
    public async Task AFolder_LeavesOutOrphanedFilesAndFilesWithNoLibrary()
    {
        AddAsset("/media/kids/a.mkv", _kids);
        var gone = AddAsset("/media/kids/b.mkv", _kids);
        AddAsset("/media/kids/c.mkv", null);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE media_assets SET is_orphaned = 1 WHERE id = @gone;", new { gone });
        }

        var counts = await _counts.CountByLibraryInFolderAsync("/media/kids", null);

        Assert.Equal(new Dictionary<Guid, int> { [_kids] = 1 }, counts);
    }

    [Fact]
    public async Task AListOfFiles_CountsEachFileOnceInTheLibraryItIsIn_AndIgnoresIdsThatAreNotFiles()
    {
        var one = AddAsset("/media/a.mkv", _movies);
        var two = AddAsset("/media/b.mkv", _movies);
        var three = AddAsset("/media/c.mkv", _kids);
        AddAsset("/media/not-asked.mkv", _kids);

        var counts = await _counts.CountByLibraryForAssetsAsync([one, two, two, three, Guid.NewGuid()]);

        Assert.Equal(new Dictionary<Guid, int> { [_movies] = 2, [_kids] = 1 }, counts);
    }

    [Fact]
    public async Task AnEmptyList_CountsNothing() =>
        Assert.Empty(await _counts.CountByLibraryForAssetsAsync([]));

    private Guid AddAsset(string path, Guid? library)
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root,library_id)
            VALUES(@asset,@edition,@hash,@path,@library);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path, library = library?.ToString("D") });
        return asset;
    }
}
