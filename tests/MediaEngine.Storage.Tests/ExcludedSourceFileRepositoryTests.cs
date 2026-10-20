using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

public sealed class ExcludedSourceFileRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ExcludedSourceFileRepository _excluded;

    public ExcludedSourceFileRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_excluded_files_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _excluded = new ExcludedSourceFileRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task AnExcludedFile_ComesBackWithEveryFactItWasStoredWith()
    {
        var account = Guid.NewGuid();
        var file = Row("movies", "source-1", "Rips/Dune.mkv") with
        {
            ContentHash = "ab12",
            FileSize = 1234,
            LastWriteUtc = new DateTimeOffset(2026, 10, 19, 8, 30, 0, TimeSpan.Zero),
            Title = "Dune",
            ExcludedByAccountId = account,
            ExcludedByName = "Shy",
            Reason = ExcludedSourceFileReasons.AsIs,
        };

        await _excluded.AddOrReplaceAsync(file);

        var read = await _excluded.GetAsync("source-1", "Rips/Dune.mkv");
        Assert.NotNull(read);
        Assert.Equal(file.Id, read.Id);
        Assert.Equal("movies", read.LibraryId);
        Assert.Equal("ab12", read.ContentHash);
        Assert.Equal(1234, read.FileSize);
        Assert.Equal(file.LastWriteUtc, read.LastWriteUtc);
        Assert.Equal("Dune", read.Title);
        Assert.Equal(file.ExcludedAt, read.ExcludedAt);
        Assert.Equal(account, read.ExcludedByAccountId);
        Assert.Equal("Shy", read.ExcludedByName);
        Assert.Equal(ExcludedSourceFileReasons.AsIs, read.Reason);
    }

    [Fact]
    public async Task AddingTheSamePlaceAgain_ReplacesTheRow_AndKeepsItsId()
    {
        var first = Row("movies", "source-1", "Dune.mkv") with { ContentHash = "old", Reason = ExcludedSourceFileReasons.AsIs };
        await _excluded.AddOrReplaceAsync(first);

        await _excluded.AddOrReplaceAsync(Row("movies", "source-1", "Dune.mkv") with
        {
            ContentHash = "new",
            Reason = ExcludedSourceFileReasons.DeleteFailed,
        });

        var all = await _excluded.ListAllAsync();
        var only = Assert.Single(all);
        Assert.Equal(first.Id, only.Id);
        Assert.Equal("new", only.ContentHash);
        Assert.Equal(ExcludedSourceFileReasons.DeleteFailed, only.Reason);
    }

    [Fact]
    public async Task TheSamePathInTwoSourceFolders_IsTwoExclusions()
    {
        await _excluded.AddOrReplaceAsync(Row("movies", "source-1", "Dune.mkv"));
        await _excluded.AddOrReplaceAsync(Row("movies", "source-2", "Dune.mkv"));

        Assert.Equal(2, (await _excluded.ListAllAsync()).Count);
    }

    [Fact]
    public async Task ListByLibrary_ReturnsOnlyThatLibrary_NewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        await _excluded.AddOrReplaceAsync(Row("movies", "s1", "old.mkv") with { ExcludedAt = now.AddHours(-2) });
        await _excluded.AddOrReplaceAsync(Row("movies", "s1", "new.mkv") with { ExcludedAt = now });
        await _excluded.AddOrReplaceAsync(Row("books", "s2", "book.epub") with { ExcludedAt = now.AddHours(-1) });

        var movies = await _excluded.ListByLibraryAsync("movies");

        Assert.Equal(["new.mkv", "old.mkv"], movies.Select(file => file.RelativePath));
        Assert.Equal(["book.epub"], (await _excluded.ListByLibraryAsync("books")).Select(file => file.RelativePath));
        Assert.Empty(await _excluded.ListByLibraryAsync("music"));
    }

    [Fact]
    public async Task Remove_DeletesTheRow_AndSaysWhetherThereWasOne()
    {
        var file = Row("movies", "source-1", "Dune.mkv");
        await _excluded.AddOrReplaceAsync(file);

        Assert.True(await _excluded.RemoveAsync(file.Id));
        Assert.False(await _excluded.RemoveAsync(file.Id));
        Assert.Null(await _excluded.GetAsync("source-1", "Dune.mkv"));
    }

    [Fact]
    public async Task AFileThatIsNotExcluded_IsNull()
    {
        Assert.Null(await _excluded.GetAsync("source-1", "nothing.mkv"));
    }

    [Fact]
    public async Task AReasonOutsideTheThree_IsRefusedByTheDataStore()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _excluded.AddOrReplaceAsync(Row("movies", "source-1", "Dune.mkv") with { Reason = "because" }));
    }

    [Theory]
    [InlineData("C:\\Media\\Movies", "C:\\Media\\Movies\\Rips\\Dune.mkv", "Rips/Dune.mkv")]
    [InlineData("C:\\Media\\Movies\\", "C:\\Media\\Movies\\Dune.mkv", "Dune.mkv")]
    public void TheRelativePath_IsWrittenWithForwardSlashes(string root, string file, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only Windows reads a backslash as a folder separator.
            return;
        }

        Assert.Equal(expected, ExcludedSourceFilePaths.RelativePathOf(root, file));
    }

    [Fact]
    public void TheSourceFolderItself_AndAnythingOutsideIt_HaveNoRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "tuvima-root");

        Assert.Null(ExcludedSourceFilePaths.RelativePathOf(root, root));
        Assert.Null(ExcludedSourceFilePaths.RelativePathOf(root, Path.Combine(Path.GetTempPath(), "elsewhere", "file.mkv")));
        Assert.Null(ExcludedSourceFilePaths.RelativePathOf(root, string.Empty));
    }

    private static ExcludedSourceFile Row(string library, string source, string relative) => new(
        Guid.NewGuid(),
        library,
        source,
        relative,
        null,
        null,
        null,
        null,
        new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero),
        null,
        null,
        ExcludedSourceFileReasons.AsIs);
}
