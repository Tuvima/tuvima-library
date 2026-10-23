using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

public sealed class ImportUploadRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ImportUploadRepository _uploads;

    public ImportUploadRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_import_uploads_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _uploads = new ImportUploadRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task AnUpload_ComesBackWithEveryFactItWasStoredWith()
    {
        var upload = Row(pieceCount: 3);

        await _uploads.CreateAsync(upload);

        var read = await _uploads.GetAsync(upload.Id);
        Assert.Equal(upload, read);
        Assert.Null(await _uploads.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task APieceRecordedTwice_CountsOnce_AndOnlyTheFirstRecordMovesTheLastPieceTime()
    {
        var upload = Row(pieceCount: 3);
        await _uploads.CreateAsync(upload);

        var first = await _uploads.RecordPieceAsync(upload.Id, 1, Start.AddHours(1));
        var again = await _uploads.RecordPieceAsync(upload.Id, 1, Start.AddHours(2));
        await _uploads.RecordPieceAsync(upload.Id, 0, Start.AddHours(3));

        Assert.True(first);
        Assert.False(again);
        Assert.Equal([0, 1], await _uploads.ListPiecesAsync(upload.Id));
        Assert.Equal(Start.AddHours(3), (await _uploads.GetAsync(upload.Id))!.LastPieceAt);
    }

    [Fact]
    public async Task APieceOutsideTheUpload_OrForAnUploadThatIsNotOpen_IsNeverRecorded()
    {
        var upload = Row(pieceCount: 2);
        await _uploads.CreateAsync(upload);

        Assert.False(await _uploads.RecordPieceAsync(upload.Id, 2, Start));
        Assert.False(await _uploads.RecordPieceAsync(upload.Id, -1, Start));
        Assert.False(await _uploads.RecordPieceAsync(Guid.NewGuid(), 0, Start));
        await _uploads.CloseAsync(upload.Id, ImportUpload.CancelledState, null, Start);
        Assert.False(await _uploads.RecordPieceAsync(upload.Id, 0, Start));
        Assert.Empty(await _uploads.ListPiecesAsync(upload.Id));
    }

    [Fact]
    public async Task AnUpload_ClosesOnce_AsCompletedWithItsFinalPathOrAsCancelled()
    {
        var done = Row(pieceCount: 1);
        var dropped = Row(pieceCount: 1);
        await _uploads.CreateAsync(done);
        await _uploads.CreateAsync(dropped);

        Assert.True(await _uploads.CloseAsync(done.Id, ImportUpload.CompletedState, "/import/Movies/Film.mkv", Start.AddHours(1)));
        Assert.False(await _uploads.CloseAsync(done.Id, ImportUpload.CancelledState, null, Start.AddHours(2)));
        Assert.True(await _uploads.CloseAsync(dropped.Id, ImportUpload.CancelledState, null, Start.AddHours(1)));

        var read = (await _uploads.GetAsync(done.Id))!;
        Assert.Equal(ImportUpload.CompletedState, read.State);
        Assert.Equal("/import/Movies/Film.mkv", read.FinalPath);
        Assert.Equal(Start.AddHours(1), read.ClosedAt);
        Assert.Equal(ImportUpload.CancelledState, (await _uploads.GetAsync(dropped.Id))!.State);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _uploads.CloseAsync(dropped.Id, "open", null, Start));
    }

    [Fact]
    public async Task OnlyOpenUploadsIdleSinceTheCutoff_AreListedAsAbandoned()
    {
        var idle = Row(pieceCount: 2) with { LastPieceAt = Start.AddHours(-30) };
        var recent = Row(pieceCount: 2) with { LastPieceAt = Start.AddHours(-1) };
        var closed = Row(pieceCount: 2) with { LastPieceAt = Start.AddHours(-30) };
        await _uploads.CreateAsync(idle);
        await _uploads.CreateAsync(recent);
        await _uploads.CreateAsync(closed);
        await _uploads.CloseAsync(closed.Id, ImportUpload.CancelledState, null, Start);

        var listed = await _uploads.ListIdleOpenAsync(Start.AddHours(-24));

        Assert.Equal(idle.Id, Assert.Single(listed).Id);
    }

    [Fact]
    public async Task FinishedUploadsOlderThanTheCutoff_AreDeletedWithTheirPieces_AndOpenOnesNever()
    {
        var old = Row(pieceCount: 1);
        var open = Row(pieceCount: 1);
        await _uploads.CreateAsync(old);
        await _uploads.CreateAsync(open);
        await _uploads.RecordPieceAsync(old.Id, 0, Start);
        await _uploads.CloseAsync(old.Id, ImportUpload.CancelledState, null, Start);

        var deleted = await _uploads.DeleteClosedBeforeAsync(Start.AddDays(1));

        Assert.Equal(1, deleted);
        Assert.Null(await _uploads.GetAsync(old.Id));
        Assert.Empty(await _uploads.ListPiecesAsync(old.Id));
        Assert.NotNull(await _uploads.GetAsync(open.Id));
    }

    private static ImportUpload Row(int pieceCount, long pieceBytes = 64) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "movies",
        "library-1",
        "Movies",
        "Film.mkv",
        pieceBytes * pieceCount,
        pieceBytes,
        pieceCount,
        "/import/.data/uploads/x.part",
        "/import/Movies",
        ImportUpload.OpenState,
        null,
        Start,
        Start,
        null);
}
