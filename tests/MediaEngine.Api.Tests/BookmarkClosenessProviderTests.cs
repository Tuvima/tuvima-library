using MediaEngine.Api.Services.Playback;

namespace MediaEngine.Api.Tests;

// The closeness limits live in config/bookmarks.json (Global bookmarks spec D7): editable without a rebuild, and a broken file never
// stops bookmarks from working.
public sealed class BookmarkClosenessProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bookmark-closeness-{Guid.NewGuid():N}");

    public BookmarkClosenessProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* cleaned by the operating system later */ }
    }

    [Fact]
    public void WithoutAFile_TheBuiltInValuesApply()
    {
        var provider = new BookmarkClosenessProvider(() => _directory);

        Assert.Equal(3, provider.Current.Time.SameSpot);
        Assert.Equal(30, provider.Current.Time.Close);
        Assert.Equal(180, provider.Current.Time.Far);
        Assert.Equal(2, provider.Current.Page.Close);
        Assert.Equal(10, provider.Current.Page.Far);
    }

    [Fact]
    public void ChangingTheFile_ChangesTheValues_WithoutRestartingAnything()
    {
        var path = Path.Combine(_directory, "bookmarks.json");
        File.WriteAllText(path, """{ "time": { "same_spot": 5, "close": 45, "far": 240 } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        var provider = new BookmarkClosenessProvider(() => _directory);
        Assert.Equal(5, provider.Current.Time.SameSpot);
        Assert.Equal(5, provider.SameSpotSeconds);

        File.WriteAllText(path, """{ "time": { "same_spot": 8, "close": 60, "far": 300 } }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        Assert.Equal(8, provider.Current.Time.SameSpot);
        Assert.Equal(300, provider.Current.Time.Far);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "time": { "same_spot": 50, "close": 30, "far": 180 } }""")]
    [InlineData("""{ "time": { "same_spot": -1, "close": 30, "far": 180 } }""")]
    public void ABrokenOrNonsenseFile_FallsBackToTheBuiltInValues(string content)
    {
        File.WriteAllText(Path.Combine(_directory, "bookmarks.json"), content);

        var provider = new BookmarkClosenessProvider(() => _directory);

        Assert.Equal(3, provider.Current.Time.SameSpot);
        Assert.Equal(180, provider.Current.Time.Far);
    }

    [Fact]
    public void TheShippedFile_HoldsTheThreeBlocksTheSpecNames()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "config", "bookmarks.json")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var provider = new BookmarkClosenessProvider(() => Path.Combine(root!, "config"));
        Assert.Equal(3, provider.Current.Time.SameSpot);
        Assert.Equal(0, provider.Current.Page.SameSpot);
        Assert.Equal(2, provider.Current.Page.Close);
        Assert.Equal(10, provider.Current.Page.Far);
        Assert.Equal(2, provider.Current.Text.Far);
    }
}
