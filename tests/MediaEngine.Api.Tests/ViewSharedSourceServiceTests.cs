using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class ViewSharedSourceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-shared-sources-{Guid.NewGuid():N}");
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly DatabaseConnection _database;
    private readonly ViewPersonalSpaceRepository _personal;
    private readonly ViewSharedLibraryRepository _shared;
    private readonly ViewStorageService _storage;
    private readonly ViewSharedSourceService _service;

    public ViewSharedSourceServiceTests()
    {
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations =
            [
                new ServerStorageLocationConfig
                {
                    Id = "view",
                    Label = "View",
                    Path = Path.Combine(_root, "storage"),
                    AllowWrite = true,
                },
            ],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "View" },
        });
        _database = new DatabaseConnection(Path.Combine(_root, "view.db"));
        _database.InitializeSchema();
        _personal = new ViewPersonalSpaceRepository(_database);
        _shared = new ViewSharedLibraryRepository(_database);
        _storage = new ViewStorageService(_configuration, _personal, _shared);
        _service = new ViewSharedSourceService(_shared, _personal, _storage);
    }

    [Fact]
    public async Task MultipleManagedSourcesUseStableSharedIdentityWithoutPersonalOwner()
    {
        var first = await _service.CreateAsync(new CreateViewSourceRequest(
            "Family archive", "managed", IncludeInTimeline: true));
        var second = await _service.CreateAsync(new CreateViewSourceRequest(
            "Scanned albums", "managed"));
        var shared = await _shared.GetAsync();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(shared.LibraryId, first.LibraryId);
        Assert.Equal(shared.LibraryId, second.LibraryId);
        Assert.StartsWith("Shared/Folders/", first.RelativePath, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_storage.GetSharedRoot()));

        using var connection = _database.CreateConnection();
        var rows = connection.Query<(string ScopeKind, Guid? PersonalSpaceId, Guid LibraryId)>(
            "SELECT scope_kind AS ScopeKind, personal_space_id AS PersonalSpaceId, library_id AS LibraryId FROM view_sources WHERE id IN (@first, @second);",
            new { first = first.Id, second = second.Id }).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal("shared", row.ScopeKind);
            Assert.Null(row.PersonalSpaceId);
            Assert.Equal(shared.LibraryId, row.LibraryId);
        });
    }

    [Fact]
    public async Task SharedSourceIdentityCannotOverwritePersonalSource()
    {
        var profileId = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = profileId,
            DisplayName = "Private owner",
            Role = ProfileRole.StandardUser,
        });
        var space = await _storage.EnsurePersonalSpaceAsync(profileId);
        var personalSource = Assert.Single(await _personal.GetSourcesAsync(space.Id));
        var library = await _shared.GetAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _shared.UpsertSourceAsync(
            new ViewSharedSource(personalSource.Id, library.LibraryId, ViewSourceType.Folder,
                "Intruder", "shared:collision", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                ViewSourceStorageMode.Managed, "Shared/Folders/Intruder")));

        Assert.Single(await _personal.GetSourcesAsync(space.Id));
        Assert.Empty(await _shared.GetSourcesAsync());
    }

    [Fact]
    public async Task SharedLinkedSourceCannotOverlapPrivateLinkedSource()
    {
        var profileId = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = profileId,
            DisplayName = "Private owner",
            Role = ProfileRole.StandardUser,
        });
        var space = await _storage.EnsurePersonalSpaceAsync(profileId);
        var linked = Directory.CreateDirectory(Path.Combine(_root, "linked"));
        await _storage.AddLinkedSourceAsync(space, "Private phone", linked.FullName, true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(
            new CreateViewSourceRequest("Shared phone", "linked", linked.FullName)));

        Assert.Empty(await _shared.GetSourcesAsync());
    }

    [Theory]
    [InlineData("same")]
    [InlineData("child")]
    [InlineData("ancestor")]
    public async Task ManagedImportCannotBypassPrivateLinkedSourceBoundary(string overlap)
    {
        var profileId = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = profileId,
            DisplayName = "Private owner",
            Role = ProfileRole.StandardUser,
        });
        var space = await _storage.EnsurePersonalSpaceAsync(profileId);
        var external = Directory.CreateDirectory(Path.Combine(_root, "external"));
        var privateRoot = Directory.CreateDirectory(Path.Combine(external.FullName, "private"));
        var child = Directory.CreateDirectory(Path.Combine(privateRoot.FullName, "child"));
        await File.WriteAllTextAsync(Path.Combine(child.FullName, "private.jpg"), "private bytes");
        await _storage.AddLinkedSourceAsync(space, "Private photos", privateRoot.FullName, true);
        var importPath = overlap switch
        {
            "same" => privateRoot.FullName,
            "child" => child.FullName,
            "ancestor" => external.FullName,
            _ => throw new ArgumentOutOfRangeException(nameof(overlap)),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateAsync(
            new CreateViewSourceRequest("Unsafe import", "managed", importPath)));

        Assert.Empty(await _shared.GetSourcesAsync());
        Assert.False(Directory.Exists(_storage.GetSharedRoot()));
        Assert.Equal("private bytes", await File.ReadAllTextAsync(Path.Combine(child.FullName, "private.jpg")));
    }

    [Fact]
    public async Task EmptySourceCanBeRenamedAndDetachedWithoutTouchingFolders()
    {
        var created = await _service.CreateAsync(new CreateViewSourceRequest("Archive", "managed"));
        var updated = await _service.UpdateAsync(created.Id,
            new UpdateViewSourceRequest("Renamed archive", false, true));

        Assert.NotNull(updated);
        Assert.Equal("Renamed archive", updated.Name);
        Assert.False(updated.Enabled);
        Assert.True(updated.IncludeInTimeline);
        Assert.Equal(ViewSharedSourceDeleteOutcome.Deleted, await _service.DeleteAsync(created.Id));
        Assert.Empty(await _shared.GetSourcesAsync());
        Assert.False(Directory.Exists(_storage.GetSharedRoot()));
    }

    [Fact]
    public async Task LinkedSharedSourceAppearsAsItsOwnAuthorizedFolderRoot()
    {
        var linked = Directory.CreateDirectory(Path.Combine(_root, "shared-linked"));
        var source = await _service.CreateAsync(new CreateViewSourceRequest(
            "Shared NAS", "linked", linked.FullName, IncludeSubdirectories: false));
        var library = await _shared.GetAsync();
        var folders = new ViewFolderService(
            _database,
            _personal,
            new ProfileRepository(_database),
            new LocalAssetRepository(_database),
            _storage);

        var page = await folders.QueryAsync(Guid.Empty,
            new ResolvedViewScope(ViewScopeKind.Shared, null, new HashSet<Guid> { library.LibraryId }),
            null, null, false, null, 0, 100);

        var listed = Assert.Single(page.Sources);
        Assert.Equal(source.Id, listed.SourceId);
        Assert.Equal("linked", listed.StorageMode);
        Assert.True(listed.Available);
    }

    public void Dispose()
    {
        _configuration.Dispose();
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
