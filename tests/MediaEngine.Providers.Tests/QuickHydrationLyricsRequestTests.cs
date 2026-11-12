using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Workers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// The automatic lyrics request each identified song gets (spec music-player-and-listen-fixes D3, D4). It has one fixed key per song, so a
/// request that gave up because its source could not be reached is put back in the queue by the next pass over the song (a rescan), once,
/// and a request that is still open is never touched. Real temp SQLite; no other part of the worker runs.
/// </summary>
public sealed class QuickHydrationLyricsRequestTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_lyrics_request_{Guid.NewGuid():N}");
    private readonly DatabaseConnection _db;
    private readonly MediaOperationRepository _operations;
    private readonly QuickHydrationWorker _worker;
    private readonly IdentityJob _job = new() { EntityId = Guid.NewGuid(), MediaType = "Music" };

    public QuickHydrationLyricsRequestTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_tempRoot);
        _db = new DatabaseConnection(Path.Combine(_tempRoot, "library.db"));
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _operations = new MediaOperationRepository(_db);
        _worker = new QuickHydrationWorker(
            null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<QuickHydrationWorker>.Instance,
            operations: _operations);
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that cannot be removed right now is left for the operating system's temp cleanup.
        }
    }

    private async Task<MediaOperation> TheRequestAsync()
        => (await _operations.GetByIdempotencyKeyAsync($"auto-text-track:{_job.EntityId:D}:Lyrics"))!;

    [Fact]
    public async Task AnIdentifiedSong_GetsOneQueuedLyricsRequest_EvenWhenTheSongIsPassedOverTwice()
    {
        await _worker.QueueLyricsAsync(_job, CancellationToken.None);
        var first = await TheRequestAsync();
        await _worker.QueueLyricsAsync(_job, CancellationToken.None);

        var again = await TheRequestAsync();
        Assert.Equal(MediaOperationStatus.Queued, first.Status);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(MediaOperationStatus.Queued, again.Status);
    }

    [Fact]
    public async Task ARescan_PutsADeadLetteredLyricsRequestBackInTheQueue_SoLyricsAreFoundOnceTheSourceIsBack()
    {
        await _worker.QueueLyricsAsync(_job, CancellationToken.None);
        var request = await TheRequestAsync();
        await _operations.MarkDeadLetteredAsync(request.Id, "Lyrics source could not be reached.");

        await _worker.QueueLyricsAsync(_job, CancellationToken.None);

        var after = await TheRequestAsync();
        Assert.Equal(request.Id, after.Id);
        Assert.Equal(MediaOperationStatus.Queued, after.Status);
        var leased = await _operations.LeaseNextAsync("test", [MediaOperationType.TextTrackLyrics], 4, TimeSpan.FromMinutes(1));
        Assert.Equal(request.Id, Assert.Single(leased).Id);
    }

    [Theory]
    [InlineData("retry_waiting")]
    [InlineData("succeeded")]
    [InlineData("no_result")]
    public async Task ARescan_LeavesALyricsRequestAloneThatIsStillOpenOrAlreadyAnswered(string status)
    {
        await _worker.QueueLyricsAsync(_job, CancellationToken.None);
        var request = await TheRequestAsync();
        switch (status)
        {
            case "retry_waiting":
                await _operations.MarkFailedRetryableAsync(request.Id, "Could not be reached.", DateTimeOffset.UtcNow.AddHours(1));
                break;
            case "succeeded":
                await _operations.MarkSucceededAsync(request.Id, "Lyrics found.");
                break;
            default:
                await _operations.MarkNoResultAsync(request.Id, "NoResult", "None.");
                break;
        }

        var before = await TheRequestAsync();
        await _worker.QueueLyricsAsync(_job, CancellationToken.None);

        var after = await TheRequestAsync();
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.NextRetryAt, after.NextRetryAt);
    }

    [Fact]
    public async Task ASongThatIsNotMusic_GetsNoLyricsRequest()
    {
        _job.MediaType = "Movies";

        await _worker.QueueLyricsAsync(_job, CancellationToken.None);

        Assert.Null(await _operations.GetByIdempotencyKeyAsync($"auto-text-track:{_job.EntityId:D}:Lyrics"));
    }
}
