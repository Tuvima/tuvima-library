using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Safer deleting in View: the sweep that notices files deleted outside Library, and the purge after the keep-for setting.</summary>
public sealed class ViewMissingFileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-missing-{Guid.NewGuid():N}");
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly DatabaseConnection _database;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly LocalAssetRepository _assets;
    private readonly ViewMissingFileRepository _missing;
    private readonly ViewMissingFileService _service;
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly string _folder;
    private Guid _sourceId;
    private Guid _spaceId;
    private Guid _profileId;

    public ViewMissingFileServiceTests()
    {
        Directory.CreateDirectory(_root);
        _folder = Directory.CreateDirectory(Path.Combine(_root, "photos")).FullName;
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _database = new DatabaseConnection(Path.Combine(_root, "view.db"));
        _database.InitializeSchema();
        _spaces = new ViewPersonalSpaceRepository(_database);
        _assets = new LocalAssetRepository(_database);
        _missing = new ViewMissingFileRepository(_database);
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "view-tests", Label = "View tests", Path = _root, AllowWrite = true }],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "view-tests", RelativeRoot = "managed" },
        });
        var storage = new ViewStorageService(_configuration, _spaces, new ViewSharedLibraryRepository(_database));
        _service = new ViewMissingFileService(storage, _missing, _configuration, new SystemActivityRepository(_database),
            TimeProvider.System, NullLogger<ViewMissingFileService>.Instance);
        CreateLinkedSource();
    }

    [Fact]
    public async Task ADeletedFile_IsMarkedMissingAndNothingElseIsRemoved()
    {
        var keep = await AddFileAsync("keep.jpg", '1');
        var gone = await AddFileAsync("gone.jpg", '2');

        File.Delete(Path.Combine(_folder, "gone.jpg"));
        await _service.SweepAsync(_libraryId);

        Assert.False(_assets.Find(keep)!.FileMissing);
        Assert.True(_assets.Find(gone)!.FileMissing);
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM local_items"));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM local_file_sources"));
        Assert.True(File.Exists(Path.Combine(_folder, "keep.jpg")));
    }

    [Fact]
    public async Task AFolderThatCannotBeReached_MarksNothingAndIsFlaggedOffline_UntilItIsBack()
    {
        await AddFileAsync("a.jpg", '1');
        await AddFileAsync("b.jpg", '2');
        var saved = Path.Combine(_root, "photos-away");
        Directory.Move(_folder, saved);

        await _service.SweepAsync(_libraryId);

        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_items WHERE missing_since IS NOT NULL"));
        Assert.Equal(0, Scalar("SELECT folder_reachable FROM view_sources"));

        Directory.Move(saved, _folder);
        await _service.SweepAsync(_libraryId);

        Assert.Equal(1, Scalar("SELECT folder_reachable FROM view_sources"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM local_items WHERE missing_since IS NOT NULL"));
    }

    [Fact]
    public async Task AFileThatReturns_IsRestoredWithItsData()
    {
        var item = await AddFileAsync("back.jpg", '1');
        var path = Path.Combine(_folder, "back.jpg");
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        await _service.SweepAsync(_libraryId);
        Assert.True(_assets.Find(item)!.FileMissing);

        File.WriteAllBytes(path, bytes);
        await _service.SweepAsync(_libraryId);

        Assert.False(_assets.Find(item)!.FileMissing);
    }

    [Fact]
    public async Task Purge_RemovesRecordsPastTheSetting_NeverTouchesFiles_AndWritesOneCountOnlyEntry()
    {
        var old = await AddFileAsync("old.jpg", '1');
        await AddFileAsync("fresh.jpg", '2');
        var copy = Path.Combine(_root, "elsewhere-copy.jpg");
        File.Copy(Path.Combine(_folder, "old.jpg"), copy);
        File.Delete(Path.Combine(_folder, "old.jpg"));
        await _service.SweepAsync(_libraryId);
        Backdate(old, days: 120);

        var removed = await _service.PurgeAsync();

        Assert.Equal(1, removed);
        Assert.Null(_assets.Find(old));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM local_items"));
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(Path.Combine(_folder, "fresh.jpg")));
        var entry = Assert.Single(Rows("SELECT collection_name, detail, changes_json FROM system_activity WHERE action_type = 'PersonalMissingPurged'"));
        Assert.DoesNotContain("old.jpg", entry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NeverRemove_DisablesThePurge()
    {
        var old = await AddFileAsync("old.jpg", '1');
        File.Delete(Path.Combine(_folder, "old.jpg"));
        await _service.SweepAsync(_libraryId);
        Backdate(old, days: 400);
        var settings = _configuration.LoadPhotoFiling();
        settings.MissingFileRetentionDays = 0;
        _configuration.SavePhotoFiling(settings);

        Assert.Equal(0, await _service.PurgeAsync());
        Assert.NotNull(_assets.Find(old));
    }

    [Fact]
    public async Task Purge_SkipsAnItemInAFolderThatIsOffline()
    {
        var old = await AddFileAsync("old.jpg", '1');
        File.Delete(Path.Combine(_folder, "old.jpg"));
        await _service.SweepAsync(_libraryId);
        Backdate(old, days: 200);
        Directory.Move(_folder, Path.Combine(_root, "photos-away"));
        await _service.SweepAsync(_libraryId);

        Assert.Equal(0, await _service.PurgeAsync());
        Assert.NotNull(_assets.Find(old));
    }

    private void Backdate(Guid itemId, int days)
    {
        var stamp = DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'");
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE local_items SET missing_since = @stamp WHERE id = @itemId;", new { stamp, itemId });
        connection.Execute("UPDATE local_file_sources SET missing_since = @stamp WHERE missing_since IS NOT NULL;", new { stamp });
    }

    private async Task<Guid> AddFileAsync(string name, char hash)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [(byte)hash, 1, 2, 3]);
        var result = await _assets.UpsertAsync(new LocalAssetRegistration(
            _libraryId, _spaceId, _profileId, LocalAssetMediaKinds.Image, name, DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration(path, new string(hash, 64), name, "image/jpeg", 4, DateTimeOffset.UtcNow, SourceId: _sourceId)]));
        return result.ItemId;
    }

    private void CreateLinkedSource()
    {
        _profileId = Guid.NewGuid();
        ProfileTestData.InsertAsync(_database, new MediaEngine.Domain.Aggregates.Profile
        {
            Id = _profileId,
            DisplayName = "Owner",
            Role = MediaEngine.Domain.Enums.ProfileRole.RestrictedProfile,
        }).GetAwaiter().GetResult();
        var space = _spaces.CreateAsync(_profileId, _libraryId).GetAwaiter().GetResult();
        _spaceId = space.Id;
        _sourceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        _spaces.UpsertSourceAsync(new ViewSource(
            _sourceId, space.Id, ViewSourceType.Folder, "Photos", "test:photos", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: _folder, IncludeSubdirectories: true, Enabled: true)).GetAwaiter().GetResult();
    }

    private long Scalar(string sql)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<long>(sql);
    }

    private List<string> Rows(string sql)
    {
        using var connection = _database.CreateConnection();
        return connection.Query(sql).Select(row => string.Join('|', ((IDictionary<string, object>)row).Values)).ToList();
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        TestTemp.ReleaseSqlitePoolsIn(_root);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
