using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

public sealed class ExcludedFileServiceTests : IDisposable
{
    private readonly ExcludedFileTestWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task TheList_HoldsOnlyThatLibrarysFiles_NewestFirst_WithNameAndFolderLabelButNoDiskPath()
    {
        var now = DateTimeOffset.UtcNow;
        _world.WriteFile(_world.Managed, "Dune/Dune.mkv");
        _world.WriteFile(_world.Managed, "Arrival.mkv");
        _world.WriteFile(_world.AsIs, "Old/Alien.mkv");
        await _world.ExcludeAsync("movies", "Dune/Dune.mkv", now.AddHours(-3), title: "Dune");
        await _world.ExcludeAsync("movies", "Arrival.mkv", now.AddHours(-1), title: "Arrival");
        await _world.ExcludeAsync("old-rips", "Old/Alien.mkv", now.AddHours(-2), title: "Alien");

        var movies = await _world.Service.ListAsync("movies");

        Assert.Equal(["Arrival.mkv", "Dune.mkv"], movies.Select(file => file.FileName));
        Assert.All(movies, file => Assert.Equal("movies", file.LibraryId));
        Assert.Equal("Movies", movies[0].FolderLabel);
        Assert.Equal("Movies › Dune", movies[1].FolderLabel);
        Assert.Equal("Shy", movies[0].ExcludedBy);
        Assert.Equal("Arrival", movies[0].Title);
        Assert.All(movies, file =>
        {
            Assert.DoesNotContain(_world.Root, file.FolderLabel, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_world.Root, file.FileName, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task TheListWithNoLibrary_HoldsEveryLibrarysFiles_NewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        _world.WriteFile(_world.Managed, "Dune.mkv");
        _world.WriteFile(_world.AsIs, "Alien.mkv");
        await _world.ExcludeAsync("movies", "Dune.mkv", now.AddHours(-2));
        await _world.ExcludeAsync("old-rips", "Alien.mkv", now.AddHours(-1));

        var all = await _world.Service.ListAsync(null);

        Assert.Equal(["Alien.mkv", "Dune.mkv"], all.Select(file => file.FileName));
    }

    [Fact]
    public async Task AddBack_RemovesTheRow_MakesTheScansForgetIt_AndQueuesThatOneFile()
    {
        var path = _world.WriteFile(_world.Managed, "Dune/Dune.mkv");
        var row = await _world.ExcludeAsync("movies", "Dune/Dune.mkv", DateTimeOffset.UtcNow);

        var result = await _world.Service.AddBackAsync(row.Id);

        Assert.NotNull(result);
        Assert.True(result.Queued);
        Assert.Equal(ExcludedFileService.QueuedMessage, result.Message);
        Assert.Empty(await _world.Excluded.ListByLibraryAsync("movies"));
        Assert.Equal(1, _world.Filter.Invalidations);
        var request = Assert.Single(_world.Intake.Requests);
        Assert.Equal(Path.GetFullPath(path), request.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("movies", request.LibraryId);
        Assert.Equal(_world.ManagedSourceId.ToString("D"), request.SourceId);
    }

    [Fact]
    public async Task AnExcludedAsIsFile_IsSkippedByScansUntilAddedBack_ThenTheScanTakesItIn()
    {
        var path = _world.WriteFile(_world.AsIs, "Old/Alien.mkv", bytes: 321);
        var row = await _world.ExcludeAsync("old-rips", "Old/Alien.mkv", DateTimeOffset.UtcNow);
        Assert.True(await _world.RealFilter.ShouldSkipAsync(path));

        var result = await _world.Service.AddBackAsync(row.Id);

        Assert.True(result!.Queued);
        Assert.False(await _world.RealFilter.ShouldSkipAsync(path));
        var request = Assert.Single(_world.Intake.Requests);
        Assert.Equal(Path.GetFullPath(path), request.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Equal("old-rips", request.LibraryId);
        Assert.Equal(_world.AsIsSourceId.ToString("D"), request.SourceId);
    }

    [Fact]
    public async Task AddBack_OfAFileThatIsGone_StillRemovesTheExclusion_AndQueuesNothing()
    {
        var row = await _world.ExcludeAsync("movies", "Gone/Gone.mkv", DateTimeOffset.UtcNow);

        var result = await _world.Service.AddBackAsync(row.Id);

        Assert.NotNull(result);
        Assert.False(result.Queued);
        Assert.Equal("The file is no longer on disk.", result.Message);
        Assert.Empty(await _world.Excluded.ListAllAsync());
        Assert.Equal(1, _world.Filter.Invalidations);
        Assert.Empty(_world.Intake.Requests);
    }

    [Fact]
    public async Task AddBack_WithAnUnknownId_ChangesNothing()
    {
        _world.WriteFile(_world.Managed, "Dune.mkv");
        await _world.ExcludeAsync("movies", "Dune.mkv", DateTimeOffset.UtcNow);

        var result = await _world.Service.AddBackAsync(Guid.NewGuid());

        Assert.Null(result);
        Assert.Single(await _world.Excluded.ListAllAsync());
        Assert.Equal(0, _world.Filter.Invalidations);
        Assert.Empty(_world.Intake.Requests);
    }

    [Fact]
    public async Task AddBack_WhenTheScanCannotBeQueued_StillRemovesTheExclusion_AndSaysSo()
    {
        _world.WriteFile(_world.Managed, "Dune.mkv");
        var row = await _world.ExcludeAsync("movies", "Dune.mkv", DateTimeOffset.UtcNow);
        _world.Intake.Failure = new InvalidOperationException("The destination library is not configured.");

        var result = await _world.Service.AddBackAsync(row.Id);

        Assert.NotNull(result);
        Assert.False(result.Queued);
        Assert.Equal(ExcludedFileService.NotQueuedMessage, result.Message);
        Assert.Empty(await _world.Excluded.ListAllAsync());
    }

    [Fact]
    public async Task AddBack_OfAPlaceThatPointsOutsideItsFolder_ReadsNothing()
    {
        var outside = _world.WriteFile(_world.Root, "secret.mkv");
        Assert.True(File.Exists(outside));
        var row = await _world.ExcludeAsync("movies", "../secret.mkv", DateTimeOffset.UtcNow);

        var result = await _world.Service.AddBackAsync(row.Id);

        Assert.False(result!.Queued);
        Assert.Empty(_world.Intake.Requests);
    }
}
