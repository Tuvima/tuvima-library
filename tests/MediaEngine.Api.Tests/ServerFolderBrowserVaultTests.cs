using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The folder picker never shows the Vault's folders to a caller whose NSFW mode is off: they and everything under them are left out
/// of listings and answer exactly as a folder that does not exist (Videos spec D17, D45). Real temporary folders.
/// </summary>
public sealed class ServerFolderBrowserVaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-server-folder-vault-tests", Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly string _vaultFolder;
    private readonly ServerFolderBrowserService _service;

    public ServerFolderBrowserVaultTests()
    {
        _shared = Path.Combine(_root, "shared");
        _vaultFolder = Path.Combine(_shared, "Private");
        Directory.CreateDirectory(Path.Combine(_vaultFolder, "Inner"));
        Directory.CreateDirectory(Path.Combine(_shared, "Movies"));
        var viewRoot = Directory.CreateDirectory(Path.Combine(_root, "view")).FullName;
        var configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        configuration.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations =
            [
                new ServerStorageLocationConfig { Id = "shared", Label = "Shared", Path = _shared, AllowWrite = true },
                new ServerStorageLocationConfig { Id = "view", Label = "View", Path = viewRoot, AllowWrite = true },
            ],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "view", RelativeRoot = "." },
        });
        _service = new ServerFolderBrowserService(configuration, NullLogger<ServerFolderBrowserService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup is best effort.
        }
    }

    [Fact]
    public void Browse_LeavesAHiddenVaultFolderOutOfTheListing_ButAnUnlockedCallerSeesIt()
    {
        var request = new BrowseServerFoldersRequest { StorageLocationId = "shared" };

        var locked = _service.Browse(request, [_vaultFolder]);
        var unlocked = _service.Browse(request, []);

        Assert.Equal(["Movies"], locked.Directories.Select(item => item.Name));
        Assert.Equal(["Movies", "Private"], unlocked.Directories.Select(item => item.Name));
    }

    [Fact]
    public void Browse_OfAHiddenVaultFolderOrOneInsideIt_AnswersExactlyAsAFolderThatDoesNotExist()
    {
        var missing = Assert.Throws<ServerFolderAccessException>(() => _service.Browse(
            new BrowseServerFoldersRequest { StorageLocationId = "shared", RelativePath = "Nowhere" }, [_vaultFolder]));

        foreach (var relative in new[] { "Private", "Private/Inner" })
        {
            var hidden = Assert.Throws<ServerFolderAccessException>(() => _service.Browse(
                new BrowseServerFoldersRequest { StorageLocationId = "shared", RelativePath = relative }, [_vaultFolder]));
            Assert.Equal(missing.Message, hidden.Message);
        }
    }

    [Fact]
    public void Validate_OfATypedVaultPath_SaysNotFoundLikeAMissingFolder_NotAnotherLibrary()
    {
        ValidateServerFolderRequest Typed(string path) => new()
        {
            StorageLocationId = "shared",
            ManualPath = path,
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        };

        var missing = _service.Validate(Typed(Path.Combine(_shared, "Nowhere")), [_vaultFolder]);
        foreach (var path in new[] { _vaultFolder, Path.Combine(_vaultFolder, "Inner") })
        {
            var hidden = _service.Validate(Typed(path), [_vaultFolder]);
            Assert.False(hidden.CanSelect);
            Assert.False(hidden.Exists);
            Assert.Equal(missing.Issues.Select(issue => (issue.Code, issue.Message)), hidden.Issues.Select(issue => (issue.Code, issue.Message)));
        }

        var visible = _service.Validate(Typed(_vaultFolder), []);
        Assert.True(visible.Exists);
    }
}
