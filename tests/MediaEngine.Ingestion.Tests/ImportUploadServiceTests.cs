using System.Reflection;
using Dapper;
using MediaEngine.Contracts.Imports;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Services.ImportUploads;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// Resumable uploads into the Import folder (Library setup spec D44), run against a real Import folder on temp folders and a real
/// SQLite data store. Nothing is mocked on disk; the Engine's queue and the clock are hand-written stand-ins.
/// </summary>
public sealed class ImportUploadServiceTests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private static readonly Guid Owner = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly ImportFolderHarness _h = new();
    private readonly RecordingIngestionEngine _engine = new();

    public ImportUploadServiceTests()
    {
        // One MiB pieces so a three-piece upload is a few MiB, not hundreds.
        _h.Config.Maintenance = new MaintenanceSettings { UploadPieceBytes = MiB };
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Start_WithNoImportFolder_IsRefusedWithAReasonCode_AndNothingIsCreated()
    {
        _h.Config.Libraries.StorageLocations.Clear();
        var service = NewService();

        var result = await service.StartAsync(Owner, Request("song.mp3", MiB, "music"));

        Assert.Equal(ImportUploadRefusalCodes.NoImportFolder, Assert.IsType<ImportUploadRefusal>(result.Refusal).Code);
        AssertNothingCreated();
    }

    [Fact]
    public async Task Start_WhenTheKindHasNoManagedDestination_IsRefused_AndNothingIsCreated()
    {
        // Music has no library at all, so there is nowhere to file it.
        var service = NewService();

        var result = await service.StartAsync(Owner, Request("song.mp3", MiB, "music"));

        Assert.Equal(ImportUploadRefusalCodes.NoManagedDestination, result.Refusal?.Code);
        Assert.Equal(ImportUploadRefusalKind.Conflict, result.Refusal?.Kind);
        AssertNothingCreated();
    }

    [Fact]
    public async Task Start_WhenTheKindsFolderIsOnlyReadOnly_IsRefused_AndNothingIsCreated()
    {
        _h.SetLibrary(ImportFolderHarness.NewLibrary(
            "aaaaaaaa-0000-4000-8000-000000000009", "Music", "Music", destinationId: "music-asis",
            ImportFolderHarness.AsIs("music-asis", _h.AsIsDir)));
        var service = NewService();

        var result = await service.StartAsync(Owner, Request("song.mp3", MiB, "music"));

        Assert.Equal(ImportUploadRefusalCodes.NoManagedDestination, result.Refusal?.Code);
        AssertNothingCreated();
    }

    [Fact]
    public async Task Start_ForAVaultLibrary_IsRefusedWithAPlainReasonThatItIsNotAvailableYet()
    {
        var service = NewService(vault: new VaultHookStub(isVault: true));

        var kindsLibrary = await service.StartAsync(Owner, Request("film.mkv", MiB));
        var namedLibrary = await service.StartAsync(Owner, Request("film.mkv", MiB, libraryId: ImportFolderHarness.MoviesLibraryId));

        foreach (var result in new[] { kindsLibrary, namedLibrary })
        {
            Assert.Equal(ImportUploadRefusalCodes.VaultNotAvailable, result.Refusal?.Code);
            Assert.Equal("Adding files to the Vault by drag and drop isn't available yet.", result.Refusal!.Message);
        }

        AssertNothingCreated();
    }

    [Fact]
    public async Task Start_AboveTheWarningSize_AsksForConfirmation_WithTheFolderPath_AndCreatesNothing()
    {
        _h.Config.Maintenance = new MaintenanceSettings { UploadPieceBytes = MiB, LargeUploadWarningBytes = 10 * MiB };
        var service = NewService();

        var atTheSize = await service.StartAsync(Owner, Request("a.mkv", 10 * MiB));
        await service.CancelAsync(Owner, atTheSize.Value!.UploadId);
        var rowsBefore = CountRows("import_uploads");
        var above = await service.StartAsync(Owner, Request("b.mkv", 10 * MiB + 1));

        Assert.True(atTheSize.Succeeded);
        Assert.Equal(ImportUploadRefusalCodes.LargeUploadNeedsConfirm, above.Refusal?.Code);
        Assert.Equal(ImportUploadRefusalKind.Conflict, above.Refusal?.Kind);
        Assert.Equal(Path.Combine(_h.ImportDir, "Movies"), above.Refusal?.ImportFolderPath);
        Assert.Contains("copying straight into the Import folder on the server is more reliable", above.Refusal!.Message, StringComparison.Ordinal);
        Assert.Equal(rowsBefore, CountRows("import_uploads"));
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public async Task Start_AboveTheWarningSize_WhenConfirmed_Starts_ButStillNeedsRoomOnTheDrive()
    {
        _h.Config.Maintenance = new MaintenanceSettings { UploadPieceBytes = MiB, LargeUploadWarningBytes = 10 * MiB };
        var roomy = NewService();
        var tight = NewService(availableBytes: _ => 1_000);

        var confirmed = await roomy.StartAsync(Owner, Request("b.mkv", 12 * MiB, confirmLarge: true));
        var noRoom = await tight.StartAsync(Owner, Request("c.mkv", 12 * MiB, confirmLarge: true));

        Assert.True(confirmed.Succeeded, confirmed.Refusal?.Message);
        Assert.Equal(12, confirmed.Value!.PieceCount);
        Assert.Equal(ImportUploadRefusalCodes.NotEnoughSpace, noRoom.Refusal?.Code);
        Assert.Equal(1, CountRows("import_uploads"));
    }

    [Fact]
    public async Task Start_WithoutEnoughFreeSpaceOnTheImportFoldersDrive_IsRefused_AndNothingIsCreated()
    {
        var service = NewService(availableBytes: _ => 1_000);

        var result = await service.StartAsync(Owner, Request("film.mkv", 5 * MiB));

        Assert.Equal(ImportUploadRefusalCodes.NotEnoughSpace, result.Refusal?.Code);
        Assert.Equal(ImportUploadRefusalKind.InsufficientStorage, result.Refusal?.Kind);
        AssertNothingCreated();
    }

    [Fact]
    public async Task Start_CountsAnOpenUploadsSpaceOnce_BecauseItsWorkingFileIsAlreadySetAside()
    {
        // An 8 MiB drive: what the drive reports free is what is left after the working files already set aside.
        var service = NewService(availableBytes: _ => 8 * MiB - WorkingFiles().Sum(file => new FileInfo(file).Length));

        var first = await service.StartAsync(Owner, Request("one.mkv", 5 * MiB));
        var fitsInTheRest = await service.StartAsync(Owner, Request("two.mkv", 2 * MiB));
        var tooBigForTheRest = await service.StartAsync(Owner, Request("three.mkv", 2 * MiB));

        Assert.True(first.Succeeded);
        Assert.True(fitsInTheRest.Succeeded, fitsInTheRest.Refusal?.Message);
        Assert.Equal(ImportUploadRefusalCodes.NotEnoughSpace, tooBigForTheRest.Refusal?.Code);
        Assert.Equal(2, CountRows("import_uploads"));
    }

    [Fact]
    public async Task Start_WhenDragAndDropIsOff_IsRefused_AndNothingIsCreated()
    {
        _h.Config.Libraries.PersonalLibraryPolicy.AllowDragAndDrop = false;
        var service = NewService();

        var result = await service.StartAsync(Owner, Request("film.mkv", MiB));

        Assert.Equal(ImportUploadRefusalCodes.DragAndDropOff, result.Refusal?.Code);
        AssertNothingCreated();
    }

    [Theory]
    [InlineData("movies/film.mkv")]
    [InlineData("..\\film.mkv")]
    [InlineData("C:\\film.mkv")]
    [InlineData("../film.mkv")]
    [InlineData("CON.mkv")]
    [InlineData("nul")]
    [InlineData("lpt1.txt")]
    [InlineData("film.mkv.")]
    [InlineData(".hidden.mkv")]
    [InlineData("fi:lm.mkv")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Start_WithAFileNameThatHasPathPartsOrIsReserved_IsRefused_AndNothingIsCreated(string name)
    {
        var service = NewService();

        var result = await service.StartAsync(Owner, Request(name, MiB));

        Assert.Equal(ImportUploadRefusalCodes.InvalidFileName, result.Refusal?.Code);
        AssertNothingCreated();
    }

    [Fact]
    public async Task Start_WithANameTooLongInUtf8Bytes_OrWithFormatCharacters_IsRefused_AndNothingIsCreated()
    {
        var service = NewService();
        var names = new[]
        {
            // 123 characters but 242 bytes: under the character limit, over the byte limit.
            new string('\u00e9', 119) + ".mkv",
            string.Concat(Enumerable.Repeat("\U0001F3AC", 70)) + ".mkv",
            "film\u202Evkm.exe",
            "film\u200B.mkv",
            "film\u2060.mkv",
            "\u2066film.mkv",
        };

        foreach (var name in names)
        {
            var result = await service.StartAsync(Owner, Request(name, MiB));
            Assert.Equal(ImportUploadRefusalCodes.InvalidFileName, result.Refusal?.Code);
        }

        AssertNothingCreated();
        // The same length in bytes, built from one-byte characters, is fine.
        Assert.True((await service.StartAsync(Owner, Request(new string('a', ImportUploadFileNames.MaxLength - 4) + ".mkv", MiB))).Succeeded);
    }

    [SymbolicLinkFact]
    public async Task Start_WhenTheKindsSubfolderLeadsOutOfTheImportFolderThroughALink_IsRefused_AndNothingIsCreated()
    {
        var outside = Path.Combine(_h.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        var movies = Path.Combine(_h.ImportDir, "Movies");
        if (Directory.Exists(movies))
        {
            Directory.Delete(movies, recursive: true);
        }
        Directory.CreateSymbolicLink(movies, outside);
        var service = NewService();

        var result = await service.StartAsync(Owner, Request("film.mkv", MiB));

        Assert.Equal(ImportUploadRefusalCodes.DestinationUnavailable, result.Refusal?.Code);
        Assert.Equal(0, CountRows("import_uploads"));
        Assert.Empty(WorkingFiles());
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Start_WithAZeroWidthJoinerInTheName_IsAccepted_ForEmojiNames()
    {
        var service = NewService();

        // A joiner is not a direction control, and emoji sequences (the family and profession emoji) are built with it.
        var result = await service.StartAsync(Owner, Request("\U0001F468\u200D\U0001F469 home movie.mkv", MiB));

        Assert.True(result.Succeeded, result.Refusal?.Message);
    }

    [SymbolicLinkFact]
    public async Task Complete_WhenTheKindsSubfolderWasPointedOutOfTheImportFolderMeanwhile_CancelsTheUploadWithAReason()
    {
        var service = NewService();
        var content = Content(MiB);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);
        var outside = Path.Combine(_h.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        var movies = Path.Combine(_h.ImportDir, "Movies");
        if (Directory.Exists(movies))
        {
            Directory.Delete(movies, recursive: true);
        }
        Directory.CreateSymbolicLink(movies, outside);

        var result = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.Equal(ImportUploadRefusalCodes.DestinationUnavailable, result.Refusal?.Code);
        Assert.Equal(ImportUploadStates.Cancelled, (await service.GetStatusAsync(Owner, upload.UploadId)).Value?.State);
        Assert.Empty(WorkingFiles());
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Complete_WhenTheFileIsNotTheSizePromised_CancelsTheUploadWithAReason_NotAFailure()
    {
        var service = NewService();
        var content = Content(2 * MiB);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);
        await SendPieceAsync(service, upload, content, 1);
        await using (var working = new FileStream(Assert.Single(WorkingFiles()), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            working.SetLength(MiB);
        }

        var result = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.Equal(ImportUploadRefusalCodes.SizeMismatch, result.Refusal?.Code);
        Assert.Equal(ImportUploadStates.Cancelled, (await service.GetStatusAsync(Owner, upload.UploadId)).Value?.State);
        Assert.Empty(WorkingFiles());
        Assert.Empty(_h.FilesUnder(Path.Combine(_h.ImportDir, "Movies")));
        Assert.Empty(_engine.Requests);
    }

    [Fact]
    public async Task Complete_WhenTheImportFolderWasRemovedMeanwhile_CancelsTheUploadWithAReason()
    {
        var service = NewService();
        var content = Content(MiB);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);
        _h.Config.Libraries.StorageLocations.Clear();

        var result = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.Equal(ImportUploadRefusalCodes.DestinationUnavailable, result.Refusal?.Code);
        Assert.Equal(ImportUploadStates.Cancelled, (await service.GetStatusAsync(Owner, upload.UploadId)).Value?.State);
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public async Task Complete_WhenTheMoveFails_LeavesTheUploadOpen_AndCompleteWorksOnceTheCauseIsGone()
    {
        var service = NewService();
        var content = Content(2 * MiB);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);
        // A plain file where the kind's folder should be: the folder cannot be created, so the move cannot happen.
        var movies = Path.Combine(_h.ImportDir, "Movies");
        if (Directory.Exists(movies))
        {
            Directory.Delete(movies, recursive: true);
        }
        await File.WriteAllTextAsync(movies, "in the way");
        await SendPieceAsync(service, upload, content, 1);

        var refused = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.Equal(ImportUploadRefusalCodes.DestinationUnavailable, refused.Refusal?.Code);
        var status = (await service.GetStatusAsync(Owner, upload.UploadId)).Value!;
        Assert.Equal(ImportUploadStates.Open, status.State);
        Assert.Equal([0, 1], status.ReceivedPieces);
        Assert.Single(WorkingFiles());
        Assert.Empty(_engine.Requests);

        // The upload accepts pieces again (a re-sent piece is harmless) and Complete can be asked once more.
        Assert.True((await SendPieceAsync(service, upload, content, 1)).Succeeded);
        File.Delete(movies);
        Directory.CreateDirectory(movies);
        var done = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.Equal(content, await File.ReadAllBytesAsync(done.Value!.DestinationPath));
        Assert.Empty(WorkingFiles());
        Assert.Single(_engine.Requests);
    }

    [Fact]
    public async Task TwoCopiesOfTheSamePieceArrivingAtOnce_AreWrittenOneAfterTheOther_NotBothAtOnce()
    {
        var service = NewService();
        var first = Content(MiB);
        var upload = await StartAsync(service, "Film.mkv", MiB);
        await using var slow = new GatedStream(first);
        var firstCopy = service.WritePieceAsync(Owner, upload.UploadId, 0, MiB, slow);
        await slow.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10));

        var secondCopy = service.WritePieceAsync(Owner, upload.UploadId, 0, MiB, new MemoryStream(new byte[MiB]));
        await Task.Delay(300);
        var secondRanAlongside = secondCopy.IsCompleted;
        slow.Release();
        var firstResult = await firstCopy.WaitAsync(TimeSpan.FromSeconds(10));
        var secondResult = await secondCopy.WaitAsync(TimeSpan.FromSeconds(10));
        var done = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.False(secondRanAlongside);
        Assert.True(firstResult.Succeeded);
        Assert.True(secondResult.Succeeded);
        Assert.Equal(first, await File.ReadAllBytesAsync(done.Value!.DestinationPath));
    }

    [Fact]
    public async Task Start_ForAnUnknownKind_IsRefused_AndForALibraryOfAnotherKind_IsRefusedToo()
    {
        var service = NewService();

        var unknown = await service.StartAsync(Owner, Request("film.mkv", MiB, "not_a_kind"));
        var otherLibrary = await service.StartAsync(Owner, Request("film.mkv", MiB, libraryId: "aaaaaaaa-0000-4000-8000-0000000000ff"));

        Assert.Equal(ImportUploadRefusalCodes.UnknownKind, unknown.Refusal?.Code);
        Assert.Equal(ImportUploadRefusalCodes.UnknownLibrary, otherLibrary.Refusal?.Code);
        AssertNothingCreated();
    }

    [Fact]
    public async Task AThreePieceUpload_InOrder_LandsInTheKindsImportFolderUnderItsName_AndIsQueuedForFiling()
    {
        var service = NewService();
        var content = Content(2 * MiB + MiB / 2);

        var started = await service.StartAsync(Owner, Request("Film.mkv", content.Length, libraryId: ImportFolderHarness.MoviesLibraryId));
        var upload = Assert.IsType<ImportUploadStartResponse>(started.Value);
        Assert.Equal(MiB, upload.PieceBytes);
        Assert.Equal(3, upload.PieceCount);
        for (var index = 0; index < 3; index++)
        {
            var sent = await SendPieceAsync(service, upload, content, index);
            Assert.True(sent.Succeeded, sent.Refusal?.Message);
        }

        var done = await service.CompleteAsync(Owner, upload.UploadId);

        var response = Assert.IsType<ImportUploadCompleteResponse>(done.Value);
        var expectedPath = Path.Combine(_h.ImportDir, "Movies", "Film.mkv");
        Assert.Equal("Film.mkv", response.FileName);
        Assert.False(response.Renamed);
        Assert.Equal(expectedPath, response.DestinationPath);
        Assert.Equal(content, await File.ReadAllBytesAsync(expectedPath));
        var queued = Assert.Single(_engine.Requests);
        Assert.Equal(expectedPath, queued.Path);
        Assert.Equal(IntakeSourceKinds.BrowserUpload, queued.SourceKind);
        Assert.Equal(ImportFolderRules.LocationId, queued.SourceId);
        Assert.Equal(ImportFolderHarness.MoviesLibraryId, queued.DestinationLibraryId);
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public async Task WhileItArrives_TheFileIsInTheImportFoldersHiddenWorkingFolder_WhichTheScannerNeverPicksUp()
    {
        var service = NewService();
        var upload = await StartAsync(service, "Film.mkv", 2 * MiB);

        var working = Assert.Single(WorkingFiles());

        Assert.StartsWith(Path.Combine(_h.ImportDir, ".data", "uploads") + Path.DirectorySeparatorChar, working, StringComparison.Ordinal);
        Assert.Equal(upload.UploadId.ToString("N"), Path.GetFileNameWithoutExtension(working));
        Assert.True(_h.Importer.IsWorkingPath(working));
        Assert.True(IsIgnoredScanFile(working));
        Assert.True(IsIgnoredScanFile(working.Replace(Path.DirectorySeparatorChar, '/')));
        // The filer enumerates the Import folder and skips working paths, so a half-sent file is never offered for filing.
        Assert.Equal(0, await _h.Filer.FilePendingAsync());
        Assert.Empty(await _h.Waiting.GetWaitingAsync());
    }

    [Fact]
    public async Task AFileNameThatIsAlreadyTaken_GetsANumberedName_AndTheExistingFileIsNeverOverwritten()
    {
        var existing = _h.WriteImportFile(Path.Combine("Movies", "Film.mkv"), "the file already here");
        var service = NewService();

        var first = await UploadAsync(service, "Film.mkv", Content(MiB / 2));
        var second = await UploadAsync(service, "Film.mkv", Content(MiB / 3));

        Assert.Equal("the file already here", await File.ReadAllTextAsync(existing));
        Assert.Equal("Film (1).mkv", first.FileName);
        Assert.True(first.Renamed);
        Assert.Equal("Film (2).mkv", second.FileName);
        Assert.Equal(Path.Combine(_h.ImportDir, "Movies", "Film (1).mkv"), first.DestinationPath);
        Assert.Equal(MiB / 2, new FileInfo(first.DestinationPath).Length);
        Assert.Equal(MiB / 3, new FileInfo(second.DestinationPath).Length);
    }

    [Fact]
    public async Task APieceSentAgainAfterADroppedConnection_IsAcceptedWithoutDamage()
    {
        var service = NewService();
        var content = Content(2 * MiB + 10);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);

        // The connection dropped before the answer arrived, so the client sends piece 0 again with the same bytes ...
        var again = await SendPieceAsync(service, upload, content, 0);
        // ... and, as a hostile or confused client might, with other bytes: the piece already received is not rewritten.
        var other = await service.WritePieceAsync(Owner, upload.UploadId, 0, MiB, new MemoryStream(new byte[MiB]));
        await SendPieceAsync(service, upload, content, 1);
        await SendPieceAsync(service, upload, content, 2);
        var done = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.True(again.Succeeded);
        Assert.Equal([0], again.Value!.ReceivedPieces);
        Assert.True(other.Succeeded);
        Assert.Equal(content, await File.ReadAllBytesAsync(done.Value!.DestinationPath));
    }

    [Fact]
    public async Task Status_ListsThePiecesReceivedSoFar_AndTheUploadResumesAfterTheEngineIsRestarted()
    {
        var content = Content(3 * MiB);
        var before = NewService();
        var upload = await StartAsync(before, "Film.mkv", content.Length);
        await SendPieceAsync(before, upload, content, 0);
        await SendPieceAsync(before, upload, content, 2);

        // A restart: a new service over the same data store and the same working file.
        var after = NewService();
        var status = await after.GetStatusAsync(Owner, upload.UploadId);

        var response = Assert.IsType<ImportUploadStatusResponse>(status.Value);
        Assert.Equal([0, 2], response.ReceivedPieces);
        Assert.Equal(2 * MiB, response.ReceivedBytes);
        Assert.Equal(ImportUploadStates.Open, response.State);
        var early = await after.CompleteAsync(Owner, upload.UploadId);
        Assert.Equal(ImportUploadRefusalCodes.PiecesMissing, early.Refusal?.Code);
        await SendPieceAsync(after, upload, content, 1);
        var done = await after.CompleteAsync(Owner, upload.UploadId);
        Assert.Equal(content, await File.ReadAllBytesAsync(done.Value!.DestinationPath));
    }

    [Fact]
    public async Task APieceOfTheWrongLength_IsRefused_AndIsNotRecorded()
    {
        var service = NewService();
        var upload = await StartAsync(service, "Film.mkv", 2 * MiB);

        var declaredWrong = await service.WritePieceAsync(Owner, upload.UploadId, 0, MiB - 1, new MemoryStream(new byte[MiB - 1]));
        var endedEarly = await service.WritePieceAsync(Owner, upload.UploadId, 0, null, new MemoryStream(new byte[MiB / 2]));
        var tooLong = await service.WritePieceAsync(Owner, upload.UploadId, 0, null, new MemoryStream(new byte[MiB + 1]));
        var outOfRange = await service.WritePieceAsync(Owner, upload.UploadId, 2, MiB, new MemoryStream(new byte[MiB]));

        Assert.All([declaredWrong, endedEarly, tooLong, outOfRange], result => Assert.Equal(ImportUploadRefusalCodes.InvalidPiece, result.Refusal?.Code));
        Assert.Empty((await service.GetStatusAsync(Owner, upload.UploadId)).Value!.ReceivedPieces);
    }

    [Fact]
    public async Task Cancel_DeletesTheWorkingFile_AndLaterPiecesAndCompleteAreRefused()
    {
        var service = NewService();
        var content = Content(2 * MiB);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);
        Assert.Single(WorkingFiles());

        var cancelled = await service.CancelAsync(Owner, upload.UploadId);

        Assert.Equal(ImportUploadStates.Cancelled, cancelled.Value?.State);
        Assert.Empty(WorkingFiles());
        Assert.Empty(_h.FilesUnder(Path.Combine(_h.ImportDir, "Movies")));
        var piece = await SendPieceAsync(service, upload, content, 1);
        var complete = await service.CompleteAsync(Owner, upload.UploadId);
        Assert.Equal(ImportUploadRefusalCodes.UploadClosed, piece.Refusal?.Code);
        Assert.Equal(ImportUploadRefusalCodes.UploadClosed, complete.Refusal?.Code);
        // Cancelling twice is harmless.
        Assert.True((await service.CancelAsync(Owner, upload.UploadId)).Succeeded);
    }

    [Fact]
    public async Task Cancel_WhileAPieceIsBeingWritten_StopsThePieceFirst_ThenDeletesTheFile()
    {
        var service = NewService();
        var upload = await StartAsync(service, "Film.mkv", 2 * MiB);
        await using var body = new StallingStream(firstBytes: 64 * 1024);
        var piece = service.WritePieceAsync(Owner, upload.UploadId, 0, MiB, body);
        await body.FirstBytesDelivered.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = await service.CancelAsync(Owner, upload.UploadId).WaitAsync(TimeSpan.FromSeconds(10));
        var pieceResult = await piece.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(cancelled.Succeeded);
        Assert.Equal(ImportUploadRefusalCodes.UploadClosed, pieceResult.Refusal?.Code);
        Assert.Empty(WorkingFiles());
    }

    [Fact]
    public async Task Complete_AskedTwice_AnswersTheSameAndDoesNotMoveAnythingAgain()
    {
        var service = NewService();
        var content = Content(MiB / 2);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        await SendPieceAsync(service, upload, content, 0);

        var first = await service.CompleteAsync(Owner, upload.UploadId);
        var second = await service.CompleteAsync(Owner, upload.UploadId);

        Assert.Equal(first.Value!.DestinationPath, second.Value!.DestinationPath);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_h.ImportDir, "Movies")));
        Assert.Single(_engine.Requests);
    }

    [Fact]
    public async Task AnotherCaller_CannotSeeSendToCompleteOrCancelSomeoneElsesUpload()
    {
        var service = NewService();
        var content = Content(MiB / 2);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        var stranger = Guid.NewGuid();

        var status = await service.GetStatusAsync(stranger, upload.UploadId);
        var piece = await service.WritePieceAsync(stranger, upload.UploadId, 0, content.Length, new MemoryStream(content));
        var complete = await service.CompleteAsync(stranger, upload.UploadId);
        var cancel = await service.CancelAsync(stranger, upload.UploadId);

        Assert.All([status.Refusal, piece.Refusal, complete.Refusal, cancel.Refusal], refusal =>
        {
            Assert.Equal(ImportUploadRefusalKind.NotFound, refusal?.Kind);
            Assert.Equal(ImportUploadRefusalCodes.UploadNotFound, refusal?.Code);
        });
        Assert.Single(WorkingFiles());
        Assert.Empty((await service.GetStatusAsync(Owner, upload.UploadId)).Value!.ReceivedPieces);
    }

    [Fact]
    public async Task Sweep_AbandonsAnUploadWithNoPieceForTheConfiguredHours_AndDeletesItsWorkingFile()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero));
        _h.Config.Maintenance = new MaintenanceSettings { UploadPieceBytes = MiB, UploadAbandonHours = 24 };
        var service = NewService(time: clock);
        var content = Content(2 * MiB);
        var upload = await StartAsync(service, "Film.mkv", content.Length);
        clock.Advance(TimeSpan.FromHours(23));
        await SendPieceAsync(service, upload, content, 0);

        clock.Advance(TimeSpan.FromHours(23));
        var tooSoon = await service.SweepAsync();
        Assert.Equal(0, tooSoon);
        Assert.Single(WorkingFiles());

        clock.Advance(TimeSpan.FromHours(2));
        var swept = await service.SweepAsync();

        Assert.Equal(1, swept);
        Assert.Empty(WorkingFiles());
        Assert.Equal(ImportUploadStates.Cancelled, (await service.GetStatusAsync(Owner, upload.UploadId)).Value!.State);
    }

    [Fact]
    public async Task Sweep_RemovesAnOldWorkingFileNoOpenUploadOwns_AndKeepsAYoungOne()
    {
        var service = NewService();
        var folder = Path.Combine(_h.ImportDir, ".data", "uploads");
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        var young = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
        await File.WriteAllTextAsync(old, "left behind");
        await File.WriteAllTextAsync(young, "just arrived");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

        await service.SweepAsync();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(young));
    }

    private ImportUploadService NewService(
        Func<string, long?>? availableBytes = null,
        TimeProvider? time = null,
        IVaultIngestionHook? vault = null) =>
        new(
            new ImportUploadRepository(_h.Db.Connection),
            _h.Importer,
            _h.Config,
            _engine,
            vault ?? new NoVaultIngestionHook(),
            Options.Create(new IngestionOptions { UploadFreeSpaceBufferBytes = 0 }),
            NullLogger<ImportUploadService>.Instance,
            time: time,
            availableBytes: availableBytes ?? (_ => long.MaxValue));

    private static ImportUploadStartRequest Request(string name, long size, string kind = "movies", string? libraryId = null, bool confirmLarge = false) => new()
    {
        KindKey = kind,
        FileName = name,
        TotalBytes = size,
        LibraryId = libraryId,
        ConfirmLarge = confirmLarge,
    };

    private static async Task<ImportUploadStartResponse> StartAsync(ImportUploadService service, string name, long size)
    {
        var started = await service.StartAsync(Owner, Request(name, size));
        return started.Value ?? throw new InvalidOperationException(started.Refusal?.Message);
    }

    private static Task<ImportUploadResult<ImportUploadStatusResponse>> SendPieceAsync(
        ImportUploadService service, ImportUploadStartResponse upload, byte[] content, int index)
    {
        var start = (long)index * upload.PieceBytes;
        var length = (int)Math.Min(upload.PieceBytes, content.Length - start);
        return service.WritePieceAsync(Owner, upload.UploadId, index, length, new MemoryStream(content, (int)start, length));
    }

    private async Task<ImportUploadCompleteResponse> UploadAsync(ImportUploadService service, string name, byte[] content)
    {
        var upload = await StartAsync(service, name, content.Length);
        for (var index = 0; index < upload.PieceCount; index++)
        {
            Assert.True((await SendPieceAsync(service, upload, content, index)).Succeeded);
        }

        return (await service.CompleteAsync(Owner, upload.UploadId)).Value!;
    }

    private static byte[] Content(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31 + i / 7) % 251);
        }

        return bytes;
    }

    private IEnumerable<string> WorkingFiles()
    {
        var folder = Path.Combine(_h.ImportDir, ".data", "uploads");
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : [];
    }

    private int CountRows(string table)
    {
        using var connection = _h.Db.Connection.CreateConnection();
        return connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table};");
    }

    // A refused start creates nothing: no row, no working file, no file anywhere in the Import folder.
    private void AssertNothingCreated()
    {
        Assert.Equal(0, CountRows("import_uploads"));
        Assert.Empty(WorkingFiles());
        Assert.Empty(Directory.EnumerateFiles(_h.ImportDir, "*", SearchOption.AllDirectories));
    }

    private static bool IsIgnoredScanFile(string path) =>
        (bool)typeof(IngestionEngine).GetMethod("IsIgnoredScanFile", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path])!;

    private sealed class RecordingIngestionEngine : IIngestionEngine
    {
        public List<IntakeFileRequest> Requests { get; } = [];

        public Task EnqueueIntakeAsync(IntakeFileRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public Task<FileMetadataRereadResult> RereadAssetMetadataAsync(Guid assetId, CancellationToken ct = default) => throw new NotSupportedException();

        public void Start() => throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PendingOperation>> DryRunAsync(string rootPath, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ScanDirectory(string directory, bool includeSubdirectories = true, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ScanDirectories(IReadOnlyList<IngestionScanTarget> targets, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RefreshImportFolderAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task PauseWatcherAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task ResumeWatcherAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class VaultHookStub(bool isVault) : IVaultIngestionHook
    {
        public Task<bool> IsVaultLibraryAsync(string? libraryId, CancellationToken ct = default) => Task.FromResult(isVault);

        public Task<bool> IsVaultAssetAsync(Guid assetId, CancellationToken ct = default) => Task.FromResult(isVault);

        public Task OnFileRegisteredAsync(VaultRegisteredFile file, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A request body that waits, once reading starts, until it is released, as a slow connection does.</summary>
    private sealed class GatedStream(byte[] data) : Stream
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _inner = new(data);

        public Task ReadStarted => _started.Task;

        public void Release() => _release.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A request body that delivers a few bytes and then waits, as a slow connection does, until the read is cancelled.</summary>
    private sealed class StallingStream(int firstBytes) : Stream
    {
        private readonly TaskCompletionSource _delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _sentFirst;

        public Task FirstBytesDelivered => _delivered.Task;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                var count = Math.Min(firstBytes, buffer.Length);
                buffer.Span[..count].Fill(7);
                _delivered.TrySetResult();
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
