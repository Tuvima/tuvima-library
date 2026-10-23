using Dapper;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The files inside the "Suitable for limited profiles" folders (simpler access D7), on real temp SQLite. The data store and the in-memory
/// <see cref="FolderFirstCheckPaths.IsInFolder(string, string?)"/> must give the same answer on every tricky path.
/// </summary>
public sealed class SuitableFolderAssetRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_suitable_folders_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly SuitableFolderAssetRepository _folders;
    private readonly Guid _edition = Guid.NewGuid();

    public SuitableFolderAssetRepositoryTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _folders = new SuitableFolderAssetRepository(_database);
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
    public async Task OnlyFilesInsideAMarkedFolderAreFound_AndASiblingSharingThePrefixIsNot()
    {
        var inside = AddAsset("/media/kids/a.mkv");
        var nested = AddAsset("/media/kids/deeper/b.mkv");
        var backslashes = AddAsset(@"\media\kids\c.mkv");
        var sibling = AddAsset("/media/kids2/d.mkv");
        var other = AddAsset("/media/other/e.mkv");
        var unasked = AddAsset("/media/kids/not-asked.mkv");

        var found = await _folders.FindAssetsInFoldersAsync([inside, nested, backslashes, sibling, other, Guid.NewGuid()], ["/media/kids"]);

        Assert.Equivalent(new[] { inside, nested, backslashes }, found);
        Assert.DoesNotContain(unasked, found);
    }

    [Fact]
    public async Task ATrailingSeparatorOnTheFolder_ChangesNothing()
    {
        var inside = AddAsset("/media/kids/a.mkv");
        var sibling = AddAsset("/media/kids2/b.mkv");

        Assert.Equal(await _folders.FindAssetsInFoldersAsync([inside, sibling], ["/media/kids"]),
            await _folders.FindAssetsInFoldersAsync([inside, sibling], ["/media/kids/"]));
        Assert.Equal(await _folders.FindAssetsInFoldersAsync([inside, sibling], ["/media/kids"]),
            await _folders.FindAssetsInFoldersAsync([inside, sibling], [@"\media\kids\"]));
    }

    [Fact]
    public async Task SeveralFoldersAreMatchedTogether_AndNoFolderFindsNothing()
    {
        var kids = AddAsset("/media/kids/a.mkv");
        var films = AddAsset("/media/films/b.mkv");
        var other = AddAsset("/media/other/c.mkv");

        var found = await _folders.FindAssetsInFoldersAsync([kids, films, other], ["/media/kids", "/media/films"]);

        Assert.Equivalent(new[] { kids, films }, found);
        Assert.Empty(await _folders.FindAssetsInFoldersAsync([kids, films, other], []));
        Assert.Empty(await _folders.FindAssetsInFoldersAsync([], ["/media/kids"]));
    }

    [Theory]
    [InlineData("/media/kids", "/media/kids/a.mkv")]
    [InlineData("/media/kids", "/media/kids2/a.mkv")]
    [InlineData("/media/kids/", "/media/kids/a.mkv")]
    [InlineData("/media/kids", "/media/kid/a.mkv")]
    [InlineData("/media/kids", "/MEDIA/Kids/a.mkv")]
    [InlineData("/Media/Kids", "/media/kids/a.mkv")]
    [InlineData("D:\\Media\\Kids", "d:/media/kids/a.mkv")]
    [InlineData("D:\\Media\\Kids", "D:\\Media\\Kids2\\a.mkv")]
    [InlineData("/", "/anything/a.mkv")]
    public async Task TheDataStoreAndTheInMemoryRuleAgree(string folder, string file)
    {
        var asset = AddAsset(file);

        var inStore = (await _folders.FindAssetsInFoldersAsync([asset], [folder])).Contains(asset);
        var inMemory = FolderFirstCheckPaths.IsInFolder(FolderFirstCheckPaths.Prefix(folder), file);

        Assert.Equal(inMemory, inStore);
    }

    private Guid AddAsset(string path)
    {
        var asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO media_assets(id,edition_id,content_hash,file_path_root)
            VALUES(@asset,@edition,@hash,@path);
            """, new { asset, edition = _edition, hash = Guid.NewGuid().ToString("N"), path });
        return asset;
    }
}
