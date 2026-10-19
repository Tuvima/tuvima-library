using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Api.Tests;

public sealed class ViewStorageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-view-storage-{Guid.NewGuid():N}");
    private readonly string _storageRoot;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly DatabaseConnection _database;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly ViewStorageService _service;

    public ViewStorageServiceTests()
    {
        _storageRoot = Directory.CreateDirectory(Path.Combine(_root, "storage")).FullName;
        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations =
            [
                new ServerStorageLocationConfig
                {
                    Id = "view",
                    Label = "View",
                    Path = _storageRoot,
                    AllowWrite = true,
                },
            ],
            ViewStorage = new ViewStorageConfig
            {
                StorageLocationId = "view",
                RelativeRoot = "View",
            },
        });
        _database = new DatabaseConnection(Path.Combine(_root, "view.db"));
        _database.InitializeSchema();
        _spaces = new ViewPersonalSpaceRepository(_database);
        _service = new ViewStorageService(_configuration, _spaces, new ViewSharedLibraryRepository(_database));
    }

    [Fact]
    public async Task EnsurePersonalSpace_ReservesOneStableProfileAndSourceWithoutDirectories()
    {
        var profileId = await AddProfileAsync();

        var first = await _service.EnsurePersonalSpaceAsync(profileId);
        var second = await _service.EnsurePersonalSpaceAsync(profileId);
        var source = Assert.Single(await _spaces.GetSourcesAsync(first.Id));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(ViewSourceStorageMode.Managed, source.StorageMode);
        Assert.Equal("Browser uploads", source.Name);
        Assert.Equal(
            Path.Combine(_service.GetRootPath(), "Profiles", first.StorageLabel),
            _service.GetProfileRoot(first));
        Assert.Equal(
            Path.Combine(_service.GetProfileRoot(first), "Timeline"),
            _service.GetSourcePath(first, source));
        Assert.False(Directory.Exists(_service.GetRootPath()));
    }

    [Fact]
    public async Task ImportFolder_CopiesOriginalsIntoManagedSource()
    {
        var profileId = await AddProfileAsync();
        var space = await _service.EnsurePersonalSpaceAsync(profileId);
        var origin = Directory.CreateDirectory(Path.Combine(_root, "phone-export"));
        var nested = Directory.CreateDirectory(Path.Combine(origin.FullName, "Camera"));
        var original = Path.Combine(nested.FullName, "photo.jpg");
        await File.WriteAllTextAsync(original, "original bytes");

        var source = await _service.ImportFolderAsync(space, "Shy's phone", origin.FullName);
        var copy = Path.Combine(_service.GetSourcePath(space, source), "Camera", "photo.jpg");

        Assert.Equal(ViewSourceStorageMode.Managed, source.StorageMode);
        Assert.True(File.Exists(original));
        Assert.Equal("original bytes", await File.ReadAllTextAsync(copy));
    }

    [Fact]
    public async Task ProfileLabelsAreUniqueStableAndSeparateFromShared()
    {
        var firstId = await AddProfileAsync();
        var secondId = await AddProfileAsync();
        var first = await _service.EnsurePersonalSpaceAsync(firstId);
        var second = await _service.EnsurePersonalSpaceAsync(secondId);
        Assert.NotEqual(first.StorageLabel, second.StorageLabel);
        Assert.StartsWith("View-owner", first.StorageLabel);
        var profiles = new ProfileRepository(_database);
        var profile = (await profiles.GetByIdAsync(firstId))!;
        profile.DisplayName = "Different display name";
        await profiles.UpdateAsync(profile);
        Assert.Equal(_service.GetProfileRoot(first), _service.GetProfileRoot(await _service.EnsurePersonalSpaceAsync(firstId)));
        Assert.Equal(Path.Combine(_service.GetRootPath(), "Shared"), _service.GetSharedRoot());
        Assert.False(ViewStorageService.Contains(_service.GetProfileRoot(first), _service.GetSharedRoot()));
    }

    [Fact]
    public async Task NestedLinkedRootsCannotRegisterAnotherOwnerForTheSameTree()
    {
        var first = await _service.EnsurePersonalSpaceAsync(await AddProfileAsync());
        var second = await _service.EnsurePersonalSpaceAsync(await AddProfileAsync());
        var parent = Directory.CreateDirectory(Path.Combine(_root, "archive"));
        var child = Directory.CreateDirectory(Path.Combine(parent.FullName, "Home Movies"));
        await _service.AddLinkedSourceAsync(first, "Archive", parent.FullName, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.AddLinkedSourceAsync(second, "Movies", child.FullName, true));
        Assert.True(Directory.Exists(child.FullName));
    }

    [Fact]
    public async Task ManagedSourceCannotEscapeItsProfileEvenWithinTheViewRoot()
    {
        var first = await _service.EnsurePersonalSpaceAsync(await AddProfileAsync());
        var second = await _service.EnsurePersonalSpaceAsync(await AddProfileAsync());
        var source = Assert.Single(await _spaces.GetSourcesAsync(first.Id));
        Assert.Throws<InvalidOperationException>(() => _service.GetSourcePath(first,
            source with { RelativePath = $"Profiles/{second.StorageLabel}/Timeline" }));
        Assert.Throws<InvalidOperationException>(() => _service.GetSourcePath(first,
            source with { RelativePath = "Shared/Timeline" }));
    }

    [Fact]
    public async Task LinkFolder_RemainsExternalAndRejectsManagedRootAliases()
    {
        var profileId = await AddProfileAsync();
        var space = await _service.EnsurePersonalSpaceAsync(profileId);
        var external = Directory.CreateDirectory(Path.Combine(_root, "family-archive"));

        var source = await _service.AddLinkedSourceAsync(space, "Family archive", external.FullName, true);

        Assert.Equal(ViewSourceStorageMode.Linked, source.StorageMode);
        Assert.Equal(external.FullName, _service.GetSourcePath(space, source));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AddLinkedSourceAsync(
            space, "Managed alias", _service.GetProfileRoot(space), true));
    }

    [Fact]
    public async Task ConcurrentFirstUseAndEmptyManagedSourcesDoNotCreateFoldersOrCollide()
    {
        var owner = await AddProfileAsync();
        var spaces = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _service.EnsurePersonalSpaceAsync(owner)));
        Assert.Single(spaces.Select(space => space.Id).Distinct());
        var first = await _service.EnsureManagedSourceAsync(spaces[0], "Camera", ViewSourceType.Folder, "first");
        var second = await _service.EnsureManagedSourceAsync(spaces[0], "Camera", ViewSourceType.Folder, "second");
        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.False(Directory.Exists(_service.GetRootPath()));
    }

    private async Task<Guid> AddProfileAsync()
    {
        var profileId = Guid.NewGuid();
        await ProfileTestData.InsertAsync(_database, new Profile
        {
            Id = profileId,
            DisplayName = "View owner",
            Role = ProfileRole.RestrictedProfile,
        });
        return profileId;
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
