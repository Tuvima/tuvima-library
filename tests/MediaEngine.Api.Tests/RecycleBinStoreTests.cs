using MediaEngine.Api.Services.Libraries;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class RecycleBinStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-bin-" + Guid.NewGuid().ToString("N"));
    private readonly RecycleBinStore _store;
    private readonly string _library;

    public RecycleBinStoreTests()
    {
        _library = Path.Combine(_root, "library");
        Directory.CreateDirectory(_library);
        _store = new RecycleBinStore(Path.Combine(_root, "data"), NullLogger.Instance);
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
}
