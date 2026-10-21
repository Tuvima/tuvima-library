using MediaEngine.Api.Services.Libraries;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class RecycleBinServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-bin-" + Guid.NewGuid().ToString("N"));
    private readonly RecycleBinService _store;
    private readonly string _library;

    public RecycleBinServiceTests()
    {
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);
        _store = new RecycleBinService(Path.Combine(_root, "data"), NullLogger.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string NewFile(string name)
    {
        var path = Path.Combine(_library, name);
        File.WriteAllText(path, "video bytes");
        return path;
    }

    [Fact]
    public void MoveIn_TakesTheFileOutOfTheLibraryAndListsIt()
    {
        var path = NewFile("a.mp4");

        var entry = _store.MoveIn(path, "A video", "videos");

        Assert.False(File.Exists(path));
        var listed = Assert.Single(_store.List());
        Assert.Equal(entry.Id, listed.Id);
        Assert.Equal("A video", listed.Title);
        Assert.Equal(path, listed.OriginalPath);
    }

    [Fact]
    public void Restore_PutsTheFileBackWhereItWas()
    {
        var path = NewFile("a.mp4");
        var entry = _store.MoveIn(path, null, null);

        var result = _store.Restore(entry.Id);

        Assert.Equal(RecycleBinRestoreResult.Restored, result);
        Assert.True(File.Exists(path));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Restore_WhenSomethingIsAlreadyThere_DoesNotOverwriteIt()
    {
        var path = NewFile("a.mp4");
        var entry = _store.MoveIn(path, null, null);
        File.WriteAllText(path, "newer file");

        var result = _store.Restore(entry.Id);

        Assert.Equal(RecycleBinRestoreResult.PlaceTaken, result);
        Assert.Equal("newer file", File.ReadAllText(path));
        Assert.Single(_store.List());
    }

    [Fact]
    public void Restore_ForAnUnknownEntry_ReportsNotFound() =>
        Assert.Equal(RecycleBinRestoreResult.NotFound, _store.Restore(Guid.NewGuid()));

    [Fact]
    public void Empty_RemovesEveryFileForGood()
    {
        _store.MoveIn(NewFile("a.mp4"), null, null);
        _store.MoveIn(NewFile("b.mp4"), null, null);

        Assert.Equal(2, _store.Empty());
        Assert.Empty(_store.List());
    }

    // The Vault's deleted files are visible only inside NSFW mode (Videos spec D34).

    private static readonly IReadOnlySet<string> LockedView = new HashSet<string> { "vault-library" };

    [Fact]
    public void List_WhileLocked_HidesVaultEntries_ButShowsThemWhenUnlocked()
    {
        var vault = _store.MoveIn(NewFile("private.mp4"), "Private", "vault-library");
        var ordinary = _store.MoveIn(NewFile("home.mp4"), "Home", "videos");

        Assert.Equal([ordinary.Id], _store.List(LockedView).Select(entry => entry.Id));
        Assert.Equal(2, _store.List().Count);
        Assert.Contains(vault.Id, _store.List().Select(entry => entry.Id));
    }

    [Fact]
    public void Restore_WhileLocked_AnswersNotFoundForAVaultEntry_AndLeavesTheFileInTheBin()
    {
        var path = NewFile("private.mp4");
        var vault = _store.MoveIn(path, "Private", "vault-library");

        Assert.Equal(RecycleBinRestoreResult.NotFound, _store.Restore(vault.Id, LockedView));
        Assert.False(File.Exists(path));
        Assert.Single(_store.List());

        Assert.Equal(RecycleBinRestoreResult.Restored, _store.Restore(vault.Id));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Empty_WhileLocked_NeitherRemovesNorCountsVaultEntries()
    {
        _store.MoveIn(NewFile("private.mp4"), "Private", "vault-library");
        _store.MoveIn(NewFile("home.mp4"), "Home", "videos");

        Assert.Equal(1, _store.Empty(LockedView));

        var remaining = Assert.Single(_store.List());
        Assert.Equal("vault-library", remaining.LibraryId);
    }
}
