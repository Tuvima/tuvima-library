using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MediaEngine.Api.Services.FileWrites;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.FileWrites;
using MediaEngine.Api.Services.Tools.ExifTool;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using UglyToad.PdfPig;
using Spreadsheet = DocumentFormat.OpenXml.Spreadsheet;
using Word = DocumentFormat.OpenXml.Wordprocessing;

namespace MediaEngine.Api.Tests;

/// <summary>Runs only where a real ExifTool (<c>TUVIMA_TEST_EXIFTOOL</c>) and ffmpeg are both installed, so every file kind can be made.</summary>
public sealed class RealExifToolAndFfmpegTheoryAttribute : TheoryAttribute
{
    public RealExifToolAndFfmpegTheoryAttribute()
    {
        if (RealExifTool.Find() is null)
        {
            Skip = $"Set {RealExifTool.Variable} to the path of an ExifTool script and install Perl to run this test.";
        }
        else if (ViewWriteHarness.FindFfmpeg() is null)
        {
            Skip = "ffmpeg is needed to make the sample pictures and videos.";
        }
    }
}

/// <summary>A hand-written stand-in for the write list: remembers what was asked, writes nothing.</summary>
internal sealed class RecordingFileWriteQueue : IFileWriteQueue
{
    public List<(Guid ItemId, string Trigger)> ViewItems { get; } = [];

    public Task EnqueueAssetAsync(Guid assetId, string trigger, CancellationToken ct = default) => Task.CompletedTask;

    public Task EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default) => Task.CompletedTask;

    public Task EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default)
    {
        ViewItems.Add((localItemId, trigger));
        return Task.CompletedTask;
    }
}

/// <summary>An ExifTool that is never ready (not installed, or no Perl).</summary>
internal sealed class MissingExifToolRunner : IExifToolRunner
{
    public int Opens { get; private set; }

    public Task<ExifToolStatus> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ExifToolStatus(ToolRequirementStates.Missing, null, false));

    public Task<IExifToolSession?> OpenSessionAsync(CancellationToken ct)
    {
        Opens++;
        return Task.FromResult<IExifToolSession?>(null);
    }
}

/// <summary>An ExifTool that does what the test tells it to with the copy, without running anything.</summary>
internal sealed class ScriptedExifToolRunner(Func<string, ExifToolWriteOptions, ExifToolFileResult> write, string readBack) : IExifToolRunner
{
    public Task<ExifToolStatus> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ExifToolStatus(ToolRequirementStates.Ready, "13.59", false));

    public Task<IExifToolSession?> OpenSessionAsync(CancellationToken ct) =>
        Task.FromResult<IExifToolSession?>(new Session(write, readBack));

    private sealed class Session(Func<string, ExifToolWriteOptions, ExifToolFileResult> write, string readBack) : IExifToolSession
    {
        public string Version => "13.59";
        public bool IsRunning => true;

        public Task<ExifToolFileResult> WriteAsync(
            string path, IReadOnlyDictionary<string, string> tags, ExifToolWriteOptions options, CancellationToken ct) =>
            Task.FromResult(write(path, options));

        public Task<IReadOnlyDictionary<string, string>> ReadAsync(string path, IReadOnlyList<string> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                readBack.Length == 0 ? new Dictionary<string, string>() : tags.ToDictionary(tag => tag, _ => readBack));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// An ExifTool that stamps the name onto the end of the copy it writes and reads it back from there, so what a file carries depends on
/// the file. <see cref="DuringWrite"/> runs while the copy is being written, to act as someone editing the original at that moment.
/// </summary>
internal sealed class MarkerExifToolRunner : IExifToolRunner
{
    public const string Marker = "<<tuvima-added-by:";

    public Action<string>? DuringWrite { get; set; }
    public List<ExifToolWriteOptions> Writes { get; } = [];

    public Task<ExifToolStatus> CheckAsync(CancellationToken ct) =>
        Task.FromResult(new ExifToolStatus(ToolRequirementStates.Ready, "13.59", false));

    public Task<IExifToolSession?> OpenSessionAsync(CancellationToken ct) => Task.FromResult<IExifToolSession?>(new Session(this));

    public static bool Carries(string path, string name) =>
        Encoding.Latin1.GetString(File.ReadAllBytes(path)).EndsWith(Marker + name + ">>", StringComparison.Ordinal);

    private sealed class Session(MarkerExifToolRunner owner) : IExifToolSession
    {
        public string Version => "13.59";
        public bool IsRunning => true;

        public Task<ExifToolFileResult> WriteAsync(
            string path, IReadOnlyDictionary<string, string> tags, ExifToolWriteOptions options, CancellationToken ct)
        {
            owner.Writes.Add(options);
            File.Copy(path, options.OutputPath!);
            File.AppendAllText(options.OutputPath!, Marker + tags.Values.Single() + ">>", Encoding.Latin1);
            owner.DuringWrite?.Invoke(path);
            return Task.FromResult(ExifToolFileResult.Succeeded());
        }

        public Task<IReadOnlyDictionary<string, string>> ReadAsync(string path, IReadOnlyList<string> tags, CancellationToken ct)
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
            var start = text.LastIndexOf(Marker, StringComparison.Ordinal);
            var found = new Dictionary<string, string>();
            if (start >= 0 && text.EndsWith(">>", StringComparison.Ordinal))
            {
                var value = text[(start + Marker.Length)..^2];
                foreach (var tag in tags)
                {
                    found[tag] = value;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, string>>(found);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A swap that takes the original away and then fails, as a failed replace on Windows can.</summary>
internal sealed class DeletingThenFailingSwap : IViewFileSwap
{
    public void Replace(string temporary, string path)
    {
        File.Delete(path);
        throw new IOException("simulated failure after the original was removed");
    }
}

/// <summary>
/// A swap that takes the original away and leaves a folder in its place, so the copy can be neither moved into the original's place
/// nor left alone: the last-resort rename is the only way to keep the content.
/// </summary>
internal sealed class DeletingThenBlockingSwap : IViewFileSwap
{
    public void Replace(string temporary, string path)
    {
        File.Delete(path);
        Directory.CreateDirectory(path);
        throw new IOException("simulated failure after the original was removed");
    }
}

/// <summary>A swap that records what the copy looked like just before it took the original's place, then does the ordinary move.</summary>
internal sealed class RecordingSwap : IViewFileSwap
{
    public DateTime CopyCreationUtc { get; private set; }
    public FileAttributes CopyAttributes { get; private set; }

    public void Replace(string temporary, string path)
    {
        CopyCreationUtc = File.GetCreationTimeUtc(temporary);
        CopyAttributes = File.GetAttributes(temporary);
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Remembers what was logged, so a test can see that a failure was reported in words.</summary>
internal sealed class ListLogger : ILogger<ViewItemFileWriter>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}

/// <summary>A swap that fails before touching anything.</summary>
internal sealed class FailingSwap : IViewFileSwap
{
    public void Replace(string temporary, string path) => throw new UnauthorizedAccessException("simulated refusal");
}

/// <summary>
/// The profile name written into View files (batched write-back D6 to D9, acceptance A5) over a real temp data store and real temp
/// folders. ExifTool is the real one where <c>TUVIMA_TEST_EXIFTOOL</c> names it; the failure paths use a scripted stand-in.
/// </summary>
public sealed class ViewItemFileWriterTests : IDisposable
{
    private const string AddedByTag = "XMP-tuvima:AddedBy";
    private readonly ViewWriteHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ----- ExifTool formats, with the real ExifTool --------------------------------------------------------------------------

    [RealExifToolAndFfmpegTheory]
    [InlineData("jpg")]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("tif")]
    [InlineData("gif")]
    [InlineData("avif")]
    [InlineData("mp4")]
    [InlineData("mov")]
    [InlineData("m4v")]
    public async Task AnExifToolFormat_CarriesTheProfileNameAfterTheWrite_AndItsPictureOrVideoDataIsUntouched(string extension)
    {
        _h.UseRealExifTool();
        var (itemId, path) = await _h.AddAsync("sample." + extension, _h.MakeMedia(extension));
        var before = _h.MediaFingerprint(path);
        Assert.False(string.IsNullOrEmpty(before), "The sample should have media data to compare.");

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.False(result.ReadAgainFirst);
        Assert.Equal(ViewWriteHarness.OwnerName, await _h.ReadTagAsync(path, AddedByTag));
        Assert.Equal(before, _h.MediaFingerprint(path));
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*_original"));
    }

    [RealExifToolFact]
    public async Task APdf_OpensAsBeforeWithTheSamePages_AndCarriesTheProfileName()
    {
        _h.UseRealExifTool();
        var (itemId, path) = await _h.AddAsync("letter.pdf", MinimalPdf.Plain());

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.Equal(ViewWriteHarness.OwnerName, await _h.ReadTagAsync(path, AddedByTag));
        using var document = PdfDocument.Open(path);
        Assert.Equal(1, document.NumberOfPages);
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact(Skip = "No sample HEIC, HEIF or camera RAW (DNG, CR2, CR3, NEF, ARW) file can be made here: ffmpeg has no HEIC container and no RAW encoder. They share the ExifTool path proved by the other formats; prove them on a real photo with the local checks.")]
    public void HeicAndCameraRawFiles_CarryTheProfileName()
    {
    }

    [RealExifToolFact]
    public async Task OneExifToolStartServesARunOverManyFiles_AcrossBatches()
    {
        var runner = _h.UseRealExifTool();
        var items = new List<Guid>();
        for (var index = 0; index < 12; index++)
        {
            items.Add((await _h.AddAsync($"photo {index}.jpg", ViewWriteHarness.Jpeg(index))).ItemId);
        }

        await _h.Writer.BeginRunAsync(CancellationToken.None);
        var first = await _h.Writer.WriteBatchAsync(_h.Intents(items.Take(7)), CancellationToken.None);
        var second = await _h.Writer.WriteBatchAsync(_h.Intents(items.Skip(7)), CancellationToken.None);
        await _h.Writer.EndRunAsync(CancellationToken.None);

        Assert.All(first.Concat(second), result => Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind));
        Assert.Equal(12, first.Count + second.Count);
        Assert.Equal(1, runner.ProcessesStarted);
    }

    // ----- Open XML formats -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("docx")]
    [InlineData("xlsx")]
    [InlineData("pptx")]
    public async Task AnOfficeFile_GetsTheCustomPropertyAfterTheWrite_KeepsItsAuthor_AndStillOpens(string extension)
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("report." + extension, OfficeFiles.Make(extension, "Original Author"));

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.Equal(ViewWriteHarness.OwnerName, OfficeProfileName.Read(path));
        Assert.Equal("Original Author", OfficeFiles.Author(path));
        Assert.True(OfficeFiles.Opens(path));
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task APersonWhoseNameChangedAfterTheFileWasQueued_IsWrittenWithTheNewName()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("minutes.docx", OfficeFiles.Make("docx", "Original Author"));
        _h.RenameOwner("Renamed Person");

        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(itemId)).Outcome.Kind);

        Assert.Equal("Renamed Person", OfficeProfileName.Read(path));
    }

    // ----- Kept in Library only ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASignedPdf_IsKeptInLibraryOnly_AndTheFileIsNotChangedByAByte()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("contract.pdf", MinimalPdf.Signed());
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Contains("signed or encrypted", result.Outcome.Reason);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task AnEncryptedPdf_IsKeptInLibraryOnly()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("locked.pdf", MinimalPdf.Encrypted());
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
    }

    [Fact]
    public async Task AnEncryptedOfficeFile_ThatIsNotAZipPackage_IsKeptInLibraryOnly()
    {
        var (itemId, path) = await _h.AddAsync("secret.docx", OfficeFiles.EncryptedContainer());
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Contains("signed or encrypted", result.Outcome.Reason);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task ADigitallySignedOfficeFile_IsKeptInLibraryOnly()
    {
        var (itemId, path) = await _h.AddAsync("signed.docx", OfficeFiles.WithSignature(OfficeFiles.Make("docx", "Original Author")));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
    }

    [Fact]
    public async Task AFileKindThatCannotHoldTheName_IsKeptInLibraryOnly_AndNeverOpenedForWriting()
    {
        var runner = new MissingExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("holiday.mkv", Encoding.ASCII.GetBytes("not really a video but named like one"));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(ViewProfileNameFormats.KeptFormatReason, result.Outcome.Reason);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Equal(0, runner.Opens);
    }

    [Fact]
    public async Task AFileInAReadOnlyLinkedFolder_IsNeverQueued_NeverWritten_AndIsBlockedIfAskedAnyway()
    {
        var queue = new RecordingFileWriteQueue();
        using var linked = new ViewWriteHarness(addedByAdministrator: true, queue: queue);
        linked.UseRunner(new MissingExifToolRunner());
        var path = linked.Fixture.WritePersonal("beach.jpg", ViewWriteHarness.Jpeg(1));
        var before = ViewWriteHarness.Sha256(path);
        await linked.Fixture.Service.ScanAsync(linked.Fixture.PersonalLibraryId);
        var itemId = Assert.Single(linked.Fixture.Repository.Query(
            new MediaEngine.Storage.Contracts.LocalAssetQuery(linked.Fixture.PersonalLibraryId, Limit: 5)).Items).Id;

        Assert.Empty(queue.ViewItems);
        var result = Assert.Single(await linked.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Blocked, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
    }

    [Fact]
    public async Task WhenWriteBackIsOff_NothingIsWritten()
    {
        _h.UseRealExifToolOrNone();
        _h.SaveWriteBack(settings => settings.Enabled = false);
        var (itemId, path) = await _h.AddAsync("memo.docx", OfficeFiles.Make("docx", "Original Author"));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Blocked, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
    }

    [Fact]
    public async Task AnItemWithNoRecordedAdder_HasNothingToWrite_AndTheFileIsNotTouched()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("memo.docx", OfficeFiles.Make("docx", "Original Author"));
        _h.ClearAdder(itemId);
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
    }

    [Fact]
    public async Task ACopyOfAFileThatIsAlsoKeptInAnotherLibrary_GetsTheNameOnItsOwn_AndTheOtherLibrarysRecordKeepsItsBytes()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("shared.docx", OfficeFiles.Make("docx", "Original Author"));
        var elsewhere = Path.Combine(Path.GetDirectoryName(path)!, "elsewhere.docx");
        var otherLibrary = _h.AddSecondPlaceFor(itemId, elsewhere);
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.Equal(ViewWriteHarness.OwnerName, OfficeProfileName.Read(path));
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
        Assert.Equal(before, _h.HashRecordedForPlace(otherLibrary, elsewhere));
        var after = (await _h.Files.GetTargetAsync(itemId))!;
        Assert.Equal(1, after.PlacesInLibrary);
        Assert.Equal(0, after.PlacesElsewhere);
    }

    [Fact]
    public async Task ACopyKeptAtTwoPlacesInTheSameLibrary_IsKeptInLibraryOnly_AndTheFileIsNotChanged()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("twice.docx", OfficeFiles.Make("docx", "Original Author"));
        _h.AddSecondPlaceFor(itemId, Path.Combine(Path.GetDirectoryName(path)!, "twice copy.docx"), _h.Fixture.PersonalLibraryId);
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(ViewProfileNameFormats.KeptCopiedReason, result.Outcome.Reason);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task ACopyThatAlreadyCarriesTheName_IsVerifiedNotKept_AndTheFileIsNotRewritten()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("named.docx", OfficeFiles.Make("docx", "Original Author"));
        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(itemId)).Outcome.Kind);
        var written = ViewWriteHarness.Sha256(path);
        _h.AddSecondPlaceFor(itemId, Path.Combine(Path.GetDirectoryName(path)!, "named copy.docx"), _h.Fixture.PersonalLibraryId);

        var sameLibrary = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, sameLibrary.Outcome.Kind);
        Assert.Equal(written, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task SharingAPhotoFromAReadOnlyFolder_GivesTheSharedCopyTheName_AndTheLinkedOriginalIsNotChangedByAByte()
    {
        var runner = new MarkerExifToolRunner();
        var queue = new RecordingFileWriteQueue();
        using var linked = new ViewWriteHarness(addedByAdministrator: true, queue: queue);
        linked.UseRunner(runner);
        var original = linked.Fixture.WritePersonal("beach.jpg", ViewWriteHarness.Jpeg(21));
        var before = File.ReadAllBytes(original);
        await linked.Fixture.Service.ScanAsync(linked.Fixture.PersonalLibraryId);
        var personalId = Assert.Single(linked.Fixture.Repository.Query(
            new MediaEngine.Storage.Contracts.LocalAssetQuery(linked.Fixture.PersonalLibraryId, Limit: 5)).Items).Id;
        await linked.Transfers.ExecuteAsync(personalId, linked.Fixture.OwnerProfileId, "timeline", null);
        var sharedId = linked.SharedItemOf(personalId);

        var result = Assert.Single(await linked.RunAsync(sharedId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        var sharedPath = (await linked.Files.GetTargetAsync(sharedId))!.FilePath!;
        Assert.NotEqual(Path.GetFullPath(original), Path.GetFullPath(sharedPath));
        Assert.True(MarkerExifToolRunner.Carries(sharedPath, ViewWriteHarness.OwnerName));
        Assert.Equal(before, File.ReadAllBytes(original));
        Assert.Equal(ViewWriteHarness.Sha256(sharedPath), linked.RecordedHash(sharedId));
        Assert.Equal(ViewWriteHarness.Sha256(original), linked.RecordedHash(personalId));
        Assert.Empty(linked.TemporaryCopiesBeside(sharedPath));

        // Neither item is now a copy of the other's record: the shared photo is no longer counted as kept in Library only.
        var shared = (await linked.Files.GetTargetAsync(sharedId))!;
        Assert.Equal(1, shared.PlacesInLibrary);
        Assert.Equal(0, shared.PlacesElsewhere);
        Assert.Equal(1, (await linked.Files.GetTargetAsync(personalId))!.PlacesInLibrary);
    }

    [Fact]
    public async Task ATransferThatHasNotFinished_MakesTheFileWait_AndItIsWrittenOnceTheTransferIsDone()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (personalId, personalPath) = await _h.AddAsync("trip.jpg", ViewWriteHarness.Jpeg(22));
        await _h.Transfers.ExecuteAsync(personalId, _h.Fixture.OwnerProfileId, "timeline", null, copyOnly: true);
        var sharedId = _h.SharedItemOf(personalId);
        var sharedPath = (await _h.Files.GetTargetAsync(sharedId))!.FilePath!;
        var sharedBefore = ViewWriteHarness.Sha256(sharedPath);
        _h.SetTransferState(personalId, "cleanup_pending");

        var waitingShared = Assert.Single(await _h.RunAsync(sharedId));
        var waitingPersonal = Assert.Single(await _h.RunAsync(personalId));

        Assert.True(waitingShared.ReadAgainFirst);
        Assert.Equal(ViewItemFileWriter.TransferNotFinishedReason, waitingShared.Outcome.Reason);
        Assert.True(waitingPersonal.ReadAgainFirst);
        Assert.Empty(runner.Writes);
        Assert.Equal(sharedBefore, ViewWriteHarness.Sha256(sharedPath));

        _h.SetTransferState(personalId, "completed");
        var done = Assert.Single(await _h.RunAsync(sharedId));

        Assert.False(done.ReadAgainFirst);
        Assert.Equal(WriteBackOutcomeKind.Verified, done.Outcome.Kind);
        Assert.True(MarkerExifToolRunner.Carries(sharedPath, ViewWriteHarness.OwnerName));
        Assert.False(MarkerExifToolRunner.Carries(personalPath, ViewWriteHarness.OwnerName));
    }

    [Fact]
    public async Task AnOfficeFileWhosePackageNamesADigitalSignature_IsKeptInLibraryOnly_EvenWithoutASignatureFolder()
    {
        var (itemId, path) = await _h.AddAsync("signed.docx", OfficeFiles.WithSignatureRelationship(OfficeFiles.Make("docx", "Original Author")));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(ViewProfileNameFormats.KeptSignedOfficeReason, result.Outcome.Reason);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task AStrictOpenXmlFile_IsKeptInLibraryOnly()
    {
        var (itemId, path) = await _h.AddAsync("strict.docx", OfficeFiles.AsStrict(OfficeFiles.Make("docx", "Original Author")));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(ViewProfileNameFormats.KeptStrictOfficeReason, result.Outcome.Reason);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
    }

    // ----- Never losing the original (D9) ----------------------------------------------------------------------------------

    [Fact]
    public async Task AWrittenFile_HoldsTheNewBytes_KeepsItsModifiedTime_AndLeavesNoCopyBehind()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("photo.jpg", ViewWriteHarness.Jpeg(31));
        var modified = DateTime.UtcNow.AddDays(-40);
        File.SetLastWriteTimeUtc(path, modified);
        await _h.Fixture.Service.IndexPathAsync(_h.Fixture.PersonalLibraryId, path, _h.Fixture.OwnerProfileId);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.True(MarkerExifToolRunner.Carries(path, ViewWriteHarness.OwnerName));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task WhenTheSwapFailsAfterTheOriginalIsGone_TheNewCopyIsPutInPlace_AndTheFileIsNotLost()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("precious.jpg", ViewWriteHarness.Jpeg(32));
        _h.Writer.FileSwap = new DeletingThenFailingSwap();

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        Assert.True(File.Exists(path), "The file must still be there.");
        Assert.True(MarkerExifToolRunner.Carries(path, ViewWriteHarness.OwnerName));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task WhenTheSwapFailsAndTheOriginalIsStillThere_TheOriginalIsUnchangedAndTheCopyIsRemoved()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("kept.jpg", ViewWriteHarness.Jpeg(33));
        var before = ViewWriteHarness.Sha256(path);
        _h.Writer.FileSwap = new FailingSwap();

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
        Assert.Equal(before, _h.RecordedHash(itemId));
    }

    [Fact]
    public async Task WhenTheSwapFailsAndTheCopyCannotBePutInPlace_ItIsKeptUnderARecoveredName_AnErrorNamesBothPaths_AndTheSweepNeverClearsIt()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("blocked.jpg", ViewWriteHarness.Jpeg(36));
        var (otherId, _) = await _h.AddAsync("neighbour.jpg", ViewWriteHarness.Jpeg(37));
        var folder = Path.GetDirectoryName(path)!;
        var taken = Path.Combine(folder, "blocked.tuvima-recovered.jpg");
        File.WriteAllBytes(taken, [9, 9, 9]);
        _h.Writer.FileSwap = new DeletingThenBlockingSwap();

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        var recovered = Path.Combine(folder, "blocked.tuvima-recovered-2.jpg");
        Assert.True(File.Exists(recovered), "The only copy of the content must be kept beside the original.");
        Assert.True(MarkerExifToolRunner.Carries(recovered, ViewWriteHarness.OwnerName));
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(taken));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
        var error = Assert.Single(_h.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(recovered, error.Message);
        Assert.Contains(path, error.Message);

        // A later run in the same folder, long after: the recovered copy has a name the sweep never matches.
        Directory.Delete(path);
        File.SetLastWriteTimeUtc(recovered, DateTime.UtcNow.AddDays(-3));
        File.SetCreationTimeUtc(recovered, DateTime.UtcNow.AddDays(-3));
        _h.Writer.FileSwap = new MoveViewFileSwap();
        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(otherId)).Outcome.Kind);
        Assert.True(File.Exists(recovered));
    }

    [Fact]
    public async Task AStrandedCopy_IsRecordedOnDisk_AndAfterARestartTheSweepStillKeepsItWhenItIsOverAnHourOld()
    {
        var (neighbourId, path, copy) = await _h.StrandACopyAsync("stranded.jpg");
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(copy)).Trim('"'), File.ReadAllText(_h.StrandedRecordPath));

        var restarted = _h.NewWriter();
        var result = Assert.Single(await _h.RunWithAsync(restarted, neighbourId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.True(File.Exists(copy), "The only copy of the content must survive the restart and the sweep.");
        Assert.Contains(_h.Logger.Entries, entry => entry.Level == LogLevel.Error
            && entry.Message.Contains(path) && entry.Message.Contains(copy) && entry.Message.Contains("nothing will delete it"));
    }

    [Fact]
    public async Task WhenTheRecordOfStrandedCopiesCannotBeRead_TheSweepDeletesNothingThatRun()
    {
        var (neighbourId, _, copy) = await _h.StrandACopyAsync("unreadable.jpg");
        var otherLeftover = Path.Combine(Path.GetDirectoryName(copy)!, ViewStorageService.WriteCopyPrefix + new string('a', 32) + ".jpg");
        File.WriteAllBytes(otherLeftover, [1, 2, 3]);
        File.SetLastWriteTimeUtc(otherLeftover, DateTime.UtcNow.AddDays(-3));
        File.SetCreationTimeUtc(otherLeftover, DateTime.UtcNow.AddDays(-3));
        File.WriteAllText(_h.StrandedRecordPath, "this is not a list {");

        var restarted = _h.NewWriter();
        var result = Assert.Single(await _h.RunWithAsync(restarted, neighbourId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(otherLeftover), "With the record unreadable, no leftover is cleared.");
        Assert.Contains(_h.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("could not be read"));
        Assert.Equal("this is not a list {", File.ReadAllText(_h.StrandedRecordPath));
    }

    [Fact]
    public async Task ARecordedCopyWhoseFileIsGone_DropsOutOfTheRecord_WhenTheWriterStarts()
    {
        var (_, _, copy) = await _h.StrandACopyAsync("gone.jpg");
        Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(copy)).Trim('"'), File.ReadAllText(_h.StrandedRecordPath));
        File.Delete(copy);

        _ = _h.NewWriter();

        Assert.DoesNotContain(Path.GetFullPath(copy), File.ReadAllText(_h.StrandedRecordPath));
    }

    [Fact]
    public async Task TheCopyDoesNotCarryTheOriginalsOldCreationTimeIntoTheSwap_AndTheFileGetsItBackAfterTheSwap()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("aged.jpg", ViewWriteHarness.Jpeg(38));
        var old = DateTime.UtcNow.AddDays(-30);
        File.SetCreationTimeUtc(path, old);
        await _h.Fixture.Service.IndexPathAsync(_h.Fixture.PersonalLibraryId, path, _h.Fixture.OwnerProfileId);
        var creationCanBeSet = (File.GetCreationTimeUtc(path) - old).Duration() < TimeSpan.FromMinutes(1);
        var expected = File.GetCreationTimeUtc(path);
        var swap = new RecordingSwap();
        _h.Writer.FileSwap = swap;

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);

        // A copy that looked old at once would be swept as stale while it was the only copy of the file (Windows).
        Assert.True(DateTime.UtcNow - swap.CopyCreationUtc < ViewItemFileWriter.StaleCopyAge);
        if (creationCanBeSet)
        {
            // Linux cannot set a creation time, so only Windows and macOS check that it is given back.
            Assert.True((File.GetCreationTimeUtc(path) - expected).Duration() < TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task AReadOnlyOriginal_IsKeptInLibraryOnly_WithAPlainReason_AndNeverOpenedForWriting()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("locked.jpg", ViewWriteHarness.Jpeg(39));
        var before = File.ReadAllBytes(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var result = Assert.Single(await _h.RunAsync(itemId));

            Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
            Assert.Equal("Kept in Library only: the file is read-only.", result.Outcome.Reason);
            Assert.Empty(runner.Writes);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(_h.TemporaryCopiesBeside(path));
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public async Task AReadOnlyLeftoverCopy_IsStillClearedWhenItIsOld()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("sweep.jpg", ViewWriteHarness.Jpeg(42));
        var leftover = Path.Combine(Path.GetDirectoryName(path)!, ViewStorageService.WriteCopyPrefix + new string('d', 32) + ".jpg");
        File.WriteAllBytes(leftover, [1, 2, 3]);
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-3));
        File.SetCreationTimeUtc(leftover, DateTime.UtcNow.AddHours(-3));
        File.SetAttributes(leftover, File.GetAttributes(leftover) | FileAttributes.ReadOnly);

        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(itemId)).Outcome.Kind);

        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task OneLeftoverThatCannotBeDeleted_DoesNotStopTheOthersBeingCleared_AndIsReported()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("sweep2.jpg", ViewWriteHarness.Jpeg(43));
        var folder = Path.GetDirectoryName(path)!;
        var stuck = Path.Combine(folder, ViewStorageService.WriteCopyPrefix + new string('e', 32) + ".jpg");
        var others = new[] { 'a', 'b', 'c' }
            .Select(letter => Path.Combine(folder, ViewStorageService.WriteCopyPrefix + new string(letter, 32) + ".jpg"))
            .ToList();
        foreach (var file in others.Append(stuck))
        {
            File.WriteAllBytes(file, [1, 2, 3]);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-3));
            File.SetCreationTimeUtc(file, DateTime.UtcNow.AddHours(-3));
        }

        _h.Writer.DeleteCopy = candidate =>
        {
            if (string.Equals(candidate, stuck, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("simulated: this leftover cannot be deleted");
            }

            File.Delete(candidate);
        };

        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(itemId)).Outcome.Kind);

        Assert.True(File.Exists(stuck));
        Assert.All(others, file => Assert.False(File.Exists(file)));
        Assert.Contains(_h.Logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("could not be removed"));
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("planned", true)]
    [InlineData("transferring", true)]
    [InlineData("cleanup_pending", true)]
    public async Task OnlyATransferStillInFlightMakesTheFileWait_AFailedOneNeverBlocksTheWrite(string state, bool waits)
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (personalId, _) = await _h.AddAsync("flight.jpg", ViewWriteHarness.Jpeg(44));
        await _h.Transfers.ExecuteAsync(personalId, _h.Fixture.OwnerProfileId, "timeline", null, copyOnly: true);
        var sharedId = _h.SharedItemOf(personalId);
        _h.SetTransferState(personalId, state);

        var result = Assert.Single(await _h.RunAsync(sharedId));

        Assert.Equal(waits, result.ReadAgainFirst);
        Assert.Equal(waits ? WriteBackOutcomeKind.Failed : WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        Assert.Equal(waits ? 0 : 1, runner.Writes.Count);
    }

    [Fact]
    public async Task AnotherItemOfTheSameLibraryThatUsesTheSameFile_FollowsTheWrittenFile_WhenTheRecordIsSplit()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (stillId, _) = await _h.AddAsync("live.jpg", ViewWriteHarness.Jpeg(45));
        var (videoId, videoPath) = await _h.AddAsync("live-video.jpg", ViewWriteHarness.Jpeg(46));
        var otherLibrary = _h.AddSecondPlaceFor(videoId, Path.Combine(Path.GetDirectoryName(videoPath)!, "elsewhere.jpg"));
        var oldHash = _h.RecordedHash(videoId);
        using (var connection = _h.Fixture.Database.CreateConnection())
        {
            // The still holds the video's file as its Live Photo video, as well as the video being an item of its own.
            connection.Execute("""
                INSERT INTO local_item_files (item_id, file_id, role, position, added_at)
                SELECT @stillId, file_id, 'live_photo_video', 0, @now FROM local_item_files WHERE item_id = @videoId AND role = 'primary';
                """, new { stillId, videoId, now = DateTimeOffset.UtcNow });
        }

        var result = Assert.Single(await _h.RunAsync(videoId));

        Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind);
        var writtenHash = ViewWriteHarness.Sha256(videoPath);
        Assert.Equal(writtenHash, _h.RecordedHash(videoId));
        using var check = _h.Fixture.Database.CreateConnection();
        var stillsRecord = check.QuerySingle<Guid>(
            "SELECT file_id FROM local_item_files WHERE item_id = @stillId AND role = 'live_photo_video';", new { stillId });
        Assert.Equal(
            writtenHash,
            check.ExecuteScalar<string>("SELECT content_hash FROM local_files WHERE id = @stillsRecord;", new { stillsRecord }));
        Assert.Equal(
            1,
            check.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM local_file_sources WHERE file_id = @stillsRecord AND library_id = @library;",
                new { stillsRecord, library = _h.Fixture.PersonalLibraryId }));
        Assert.Equal(oldHash, _h.HashRecordedForPlace(otherLibrary, Path.Combine(Path.GetDirectoryName(videoPath)!, "elsewhere.jpg")));
    }

    [Fact]
    public async Task AFileEditedWhileItsCopyIsBeingWritten_IsNeverOverwritten_AndIsReadAgainFirst()
    {
        var runner = new MarkerExifToolRunner();
        var edited = ViewWriteHarness.Jpeg(77);
        runner.DuringWrite = source =>
        {
            File.WriteAllBytes(source, edited);
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(2));
        };
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("busy.jpg", ViewWriteHarness.Jpeg(34));

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.True(result.ReadAgainFirst);
        Assert.Equal(edited, File.ReadAllBytes(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
    }

    [Fact]
    public async Task LeftoverHiddenCopiesOlderThanAnHour_AreClearedBeforeAWrite_AndNothingElseIs()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, path) = await _h.AddAsync("clean.jpg", ViewWriteHarness.Jpeg(35));
        var folder = Path.GetDirectoryName(path)!;
        var oldTime = DateTime.UtcNow.AddHours(-3);
        string Leftover(string name, DateTime time)
        {
            var file = Path.Combine(folder, name);
            File.WriteAllBytes(file, [1, 2, 3]);
            File.SetLastWriteTimeUtc(file, time);
            File.SetCreationTimeUtc(file, time);
            return file;
        }

        var stale = Leftover(ViewStorageService.WriteCopyPrefix + new string('a', 32) + ".jpg", oldTime);
        var fresh = Leftover(ViewStorageService.WriteCopyPrefix + new string('b', 32) + ".jpg", DateTime.UtcNow.AddMinutes(-5));
        var otherName = Leftover(ViewStorageService.WriteCopyPrefix + "notes.txt", oldTime);
        var ordinary = Leftover("holiday.jpg", oldTime);
        var link = Path.Combine(folder, ViewStorageService.WriteCopyPrefix + new string('c', 32) + ".jpg");
        var linkTarget = Leftover("linked-target.jpg", oldTime);
        var linked = false;
        try
        {
            File.CreateSymbolicLink(link, linkTarget);
            linked = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Links cannot be made here (Windows without the privilege); the rest of the check still runs.
        }

        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(itemId)).Outcome.Kind);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(otherName));
        Assert.True(File.Exists(ordinary));
        Assert.True(File.Exists(linkTarget));
        if (linked)
        {
            Assert.NotNull(new FileInfo(link).LinkTarget);
        }
    }

    [Fact]
    public async Task TheWriterAllowsExifToolTimeForTheSizeOfTheFile()
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (itemId, _) = await _h.AddAsync("small.jpg", ViewWriteHarness.Jpeg(36));

        Assert.Equal(WriteBackOutcomeKind.Verified, Assert.Single(await _h.RunAsync(itemId)).Outcome.Kind);

        var timeout = Assert.Single(runner.Writes).Timeout;
        Assert.NotNull(timeout);
        Assert.InRange(timeout!.Value, ExifToolRunner.DefaultCommandTimeout, ExifToolRunner.DefaultCommandTimeout + TimeSpan.FromSeconds(1));
    }

    // ----- A write that fails part-way --------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenExifToolFailsOnTheCopy_TheOriginalIsUnchangedAndNoTemporaryCopyIsLeft()
    {
        _h.UseRunner(new ScriptedExifToolRunner((_, options) =>
        {
            File.WriteAllBytes(options.OutputPath!, [1, 2, 3]);
            return ExifToolFileResult.Failed("Error: simulated failure part-way");
        }, ViewWriteHarness.OwnerName));
        var (itemId, path) = await _h.AddAsync("photo.jpg", ViewWriteHarness.Jpeg(3));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        Assert.False(result.ReadAgainFirst);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
        Assert.Equal(before, _h.RecordedHash(itemId));
    }

    [Fact]
    public async Task WhenTheNameCannotBeReadBackFromTheCopy_TheOriginalIsKeptAndTheCopyIsRemoved()
    {
        _h.UseRunner(new ScriptedExifToolRunner((source, options) =>
        {
            File.Copy(source, options.OutputPath!);
            return ExifToolFileResult.Succeeded();
        }, readBack: string.Empty));
        var (itemId, path) = await _h.AddAsync("photo.jpg", ViewWriteHarness.Jpeg(4));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task WhenExifToolSaysItCannotWriteThisKindOfFile_ItIsKeptInLibraryOnly_AndNothingIsLeftBehind()
    {
        _h.UseRunner(new ScriptedExifToolRunner(
            (_, _) => ExifToolFileResult.Unsupported("Can't currently write this kind of file"), ViewWriteHarness.OwnerName));
        var (itemId, path) = await _h.AddAsync("photo.jpg", ViewWriteHarness.Jpeg(5));
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Unsupported, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    [Fact]
    public async Task WhenOpenXmlCannotOpenTheCopy_TheOriginalIsUnchangedAndNoTemporaryCopyIsLeft()
    {
        var (itemId, path) = await _h.AddAsync("broken.docx", OfficeFiles.PackageThatIsNotADocument());
        var before = ViewWriteHarness.Sha256(path);

        var result = Assert.Single(await _h.RunAsync(itemId));

        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        Assert.Equal(before, ViewWriteHarness.Sha256(path));
        Assert.Empty(_h.TemporaryCopiesBeside(path));
    }

    // ----- ExifTool missing -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenExifToolIsMissing_ItsFilesWaitWithoutSpendingAnAttempt_AndOfficeFilesAreStillWritten()
    {
        var runner = new MissingExifToolRunner();
        _h.UseRunner(runner);
        var (photoId, photo) = await _h.AddAsync("photo.jpg", ViewWriteHarness.Jpeg(6));
        var (otherPhotoId, _) = await _h.AddAsync("photo2.png", ViewWriteHarness.Png(7));
        var (documentId, document) = await _h.AddAsync("notes.docx", OfficeFiles.Make("docx", "Original Author"));
        var photoBefore = ViewWriteHarness.Sha256(photo);

        var results = await _h.RunAsync(photoId, otherPhotoId, documentId);

        Assert.True(results[0].ReadAgainFirst);
        Assert.Equal(ViewItemFileWriter.WaitingForExifToolReason, results[0].Outcome.Reason);
        Assert.True(results[1].ReadAgainFirst);
        Assert.False(results[2].ReadAgainFirst);
        Assert.Equal(WriteBackOutcomeKind.Verified, results[2].Outcome.Kind);
        Assert.Equal(ViewWriteHarness.OwnerName, OfficeProfileName.Read(document));
        Assert.Equal(photoBefore, ViewWriteHarness.Sha256(photo));
        Assert.Equal(1, runner.Opens);
    }

    // ----- Library's own writes and outside changes (D8) -------------------------------------------------------------------

    [Fact]
    public async Task ALibraryWrite_IsNotReadAsANewOrChangedItem_ByTheIndexerOrAFullScan()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("agenda.docx", OfficeFiles.Make("docx", "Original Author"));
        Assert.Single(await _h.RunAsync(itemId));
        var queuedBefore = _h.Queue.ViewItems.Count;
        var itemsBefore = _h.Fixture.ItemCount();

        var again = await _h.Fixture.Service.IndexPathAsync(_h.Fixture.PersonalLibraryId, path, _h.Fixture.OwnerProfileId);
        var scan = await _h.Fixture.Service.ScanAsync(_h.Fixture.PersonalLibraryId);

        Assert.Equal(itemId, again!.ItemId);
        Assert.False(again.ItemAdded);
        Assert.Equal(0, again.FilesAdded);
        Assert.Equal(0, again.SourcesAdded);
        Assert.Equal(0, scan!.ItemsAdded);
        Assert.Equal(0, scan.FilesAdded);
        Assert.Equal(0, scan.Errors);
        Assert.Equal(itemsBefore, _h.Fixture.ItemCount());
        Assert.Equal(queuedBefore, _h.Queue.ViewItems.Count);
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
    }

    [Fact]
    public async Task AFileChangedOutsideLibrary_IsReadAgainFirst_ThenWrittenOnTheNextRun()
    {
        _h.UseRealExifToolOrNone();
        var (itemId, path) = await _h.AddAsync("draft.docx", OfficeFiles.Make("docx", "Original Author"));
        var edited = OfficeFiles.Make("docx", "Someone Else");
        File.WriteAllBytes(path, edited);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        var first = Assert.Single(await _h.RunAsync(itemId));

        Assert.True(first.ReadAgainFirst);
        Assert.Equal(ViewItemFileWriter.FileChangedReason, first.Outcome.Reason);
        Assert.Null(OfficeProfileName.Read(path));
        Assert.Equal(ViewWriteHarness.Sha256(path), _h.RecordedHash(itemId));
        Assert.Equal(itemId, Assert.Single(_h.AllItemIds()));

        var second = Assert.Single(await _h.RunAsync(itemId));

        Assert.False(second.ReadAgainFirst);
        Assert.Equal(WriteBackOutcomeKind.Verified, second.Outcome.Kind);
        Assert.Equal(ViewWriteHarness.OwnerName, OfficeProfileName.Read(path));
        Assert.Equal("Someone Else", OfficeFiles.Author(path));
    }

    [Fact]
    public async Task WhenAnotherFileAlreadyHasTheNewFingerprint_ThePathJoinsThatRecord_AndTheUnusedOldRecordIsDeleted()
    {
        var (firstId, _) = await _h.AddAsync("one.docx", OfficeFiles.Make("docx", "One"));
        var (secondId, _) = await _h.AddAsync("two.docx", OfficeFiles.Make("docx", "Two"));
        var target = await _h.Files.GetTargetAsync(firstId);
        var otherHash = _h.RecordedHash(secondId);
        var oldHash = _h.RecordedHash(firstId);
        var newTime = DateTimeOffset.UtcNow.AddMinutes(-5);

        var recorded = await _h.Files.RecordOwnWriteAsync(
            firstId, target!.FileId!.Value, target.LibraryId, target.FilePath!, otherHash, 123, newTime);

        Assert.True(recorded);
        Assert.Equal(otherHash, _h.RecordedHash(firstId));
        Assert.Equal(otherHash, _h.RecordedHash(secondId));
        Assert.Equal(0, _h.FileRecordsWithHash(oldHash));
        Assert.Equal(1, _h.FileRecordsWithHash(otherHash));
        Assert.Equal(newTime.ToUnixTimeSeconds(), (await _h.Files.GetTargetAsync(firstId))!.ModifiedAt!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APhotoAndItsSharedCopyWrittenInOneRun_BothEndOnRecordsThatMatchTheirFiles(bool sharedFirst)
    {
        var runner = new MarkerExifToolRunner();
        _h.UseRunner(runner);
        var (personalId, personalPath) = await _h.AddAsync("pair.jpg", ViewWriteHarness.Jpeg(41));
        var originalHash = _h.RecordedHash(personalId);
        await _h.Transfers.ExecuteAsync(personalId, _h.Fixture.OwnerProfileId, "timeline", null, copyOnly: true);
        var sharedId = _h.SharedItemOf(personalId);
        var sharedPath = (await _h.Files.GetTargetAsync(sharedId))!.FilePath!;

        var results = sharedFirst ? await _h.RunAsync(sharedId, personalId) : await _h.RunAsync(personalId, sharedId);

        Assert.All(results, result => Assert.Equal(WriteBackOutcomeKind.Verified, result.Outcome.Kind));
        Assert.True(MarkerExifToolRunner.Carries(personalPath, ViewWriteHarness.OwnerName));
        Assert.True(MarkerExifToolRunner.Carries(sharedPath, ViewWriteHarness.OwnerName));
        Assert.Equal(ViewWriteHarness.Sha256(personalPath), _h.RecordedHash(personalId));
        Assert.Equal(ViewWriteHarness.Sha256(sharedPath), _h.RecordedHash(sharedId));
        Assert.Equal(new FileInfo(personalPath).Length, _h.RecordedSize(personalId));
        Assert.Equal(new FileInfo(sharedPath).Length, _h.RecordedSize(sharedId));
        Assert.Equal(0, _h.FileRecordsWithHash(originalHash));
    }

    [Theory]
    [InlineData("/data/View/Profiles/shy/Timeline/.tuvima-write-0a1b2c.jpg", true)]
    [InlineData("C:\\View\\Profiles\\shy\\Docs\\.tuvima-write-0a1b2c.docx", true)]
    [InlineData("/data/View/Profiles/shy/Timeline/beach.jpg", false)]
    [InlineData("/data/View/Profiles/shy/Timeline/tuvima-write-notes.jpg", false)]
    public void TheHiddenTemporaryCopy_IsNeverIndexed(string path, bool ignored)
    {
        Assert.Equal(ignored, ViewStorageService.IsInsideStagingFolder(path));
    }

    // ----- Queueing ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ANewItemInAManagedFolder_IsQueuedOnce_AndARescanDoesNotQueueItAgain()
    {
        var (itemId, _) = await _h.AddAsync("new.jpg", ViewWriteHarness.Jpeg(8));

        await _h.Fixture.Service.ScanAsync(_h.Fixture.PersonalLibraryId);

        var queued = Assert.Single(_h.Queue.ViewItems);
        Assert.Equal(itemId, queued.ItemId);
        Assert.Equal(MediaFileWriteTriggers.ViewAddedBy, queued.Trigger);
    }

    [Fact]
    public async Task AnUpload_IsQueuedForTheProfileThatUploadedIt()
    {
        await using var content = new MemoryStream(ViewWriteHarness.Jpeg(9));

        var result = await _h.Fixture.Service.UploadAsync(_h.Fixture.OwnerProfileId, "phone.jpg", content);

        Assert.Equal((result.ItemId, MediaFileWriteTriggers.ViewAddedBy), Assert.Single(_h.Queue.ViewItems));
    }

    [Fact]
    public async Task ANewItemInAFolderLibraryMayOnlyRead_IsNotQueued()
    {
        var queue = new RecordingFileWriteQueue();
        using var linked = new ViewWriteHarness(addedByAdministrator: true, queue: queue);
        linked.Fixture.WritePersonal("beach.jpg", ViewWriteHarness.Jpeg(1));

        var scan = await linked.Fixture.Service.ScanAsync(linked.Fixture.PersonalLibraryId);

        Assert.Equal(1, scan!.ItemsAdded);
        Assert.Empty(queue.ViewItems);
    }

    // ----- The folder's count, and deleting an item -------------------------------------------------------------------------

    [Fact]
    public async Task AFolderCountsTheItemsKeptInLibraryOnly()
    {
        var kept = new List<Guid>();
        foreach (var name in new[] { "a.mkv", "b.mkv", "c.mkv" })
        {
            kept.Add((await _h.AddAsync(name, Encoding.ASCII.GetBytes("video " + name))).ItemId);
        }

        var (written, _) = await _h.AddAsync("d.docx", OfficeFiles.Make("docx", "Original Author"));
        await _h.EnqueueForRealAsync([.. kept, written], "unsupported", except: written);

        var space = await _h.Fixture.Storage.EnsurePersonalSpaceAsync(_h.Fixture.OwnerProfileId);
        var folder = Assert.Single(await _h.Fixture.Spaces.GetSourcesAsync(space.Id), source => source.SourceType == ViewSourceType.BrowserUpload);

        Assert.Equal(3, folder.KeptInLibraryCount);
    }

    [Fact]
    public async Task DeletingAnItem_DropsItsEntryOnTheWriteList()
    {
        var (itemId, _) = await _h.AddAsync("gone.docx", OfficeFiles.Make("docx", "Original Author"));
        await _h.EnqueueForRealAsync([itemId], "pending", except: null);
        Assert.Equal(1, _h.IntentRows());

        using (var connection = _h.Fixture.Database.CreateConnection())
        {
            connection.Execute("DELETE FROM local_items WHERE id = @itemId;", new { itemId });
        }

        Assert.Equal(0, _h.IntentRows());
    }
}

/// <summary>Builds the View writer over a fixture, and the sample files and checks the tests use.</summary>
internal sealed class ViewWriteHarness : IDisposable
{
    public const string OwnerName = "View owner";

    private readonly string _toolRoot = Path.Combine(Path.GetTempPath(), "tuvima-view-writer-tests", Guid.NewGuid().ToString("N"));
    private IExifToolRunner _runner = new MissingExifToolRunner();
    private ViewItemFileWriter? _writer;

    public ViewWriteHarness(bool addedByAdministrator = false, RecordingFileWriteQueue? queue = null)
    {
        Directory.CreateDirectory(_toolRoot);
        Queue = queue ?? new RecordingFileWriteQueue();
        Fixture = new ViewLibraryServiceTests.ViewFixture(addedByAdministrator: addedByAdministrator, fileWrites: Queue);
        Files = new ViewItemFileWriteRepository(Fixture.Database);
    }

    public ViewLibraryServiceTests.ViewFixture Fixture { get; }
    public ListLogger Logger { get; } = new();
    public RecordingFileWriteQueue Queue { get; }
    public ViewItemFileWriteRepository Files { get; }

    public ViewItemFileWriter Writer => _writer ??= NewWriter();

    /// <summary>Where the Engine's data folder is for this harness (the list of kept copies lives under it).</summary>
    public string DataRoot => Path.Combine(_toolRoot, "data");

    /// <summary>The file that lists the kept copies the sweep must never clear.</summary>
    public string StrandedRecordPath => Path.Combine(DataRoot, "file-writes", "stranded-copies.json");

    /// <summary>A new writer over the same files and data folder, as after an Engine restart.</summary>
    public ViewItemFileWriter NewWriter() => new(
        Files,
        new ProfileRepository(Fixture.Database),
        Fixture.Spaces,
        Fixture.Storage,
        new SourceMutationPolicyGate(),
        _runner,
        Fixture.Service,
        Fixture.Configuration,
        new TuvimaDataPaths(DataRoot),
        Logger);

    public void Dispose()
    {
        Fixture.Dispose();
        if (Directory.Exists(_toolRoot))
        {
            Directory.Delete(_toolRoot, recursive: true);
        }
    }

    public void UseRunner(IExifToolRunner runner)
    {
        _runner = runner;
        _writer = null;
    }

    /// <summary>The real ExifTool, through the same locator the Engine uses.</summary>
    public ExifToolRunner UseRealExifTool()
    {
        var locator = new ExifToolLocator(
            new TuvimaDataPaths(DataRoot),
            new MediaEngine.Storage.ConfigurationDirectoryLoader(Path.Combine(_toolRoot, "config")),
            NullLogger<ExifToolLocator>.Instance,
            name => name == ExifToolLocator.PathVariable ? RealExifTool.Find() : Environment.GetEnvironmentVariable(name));
        var runner = new ExifToolRunner(locator, NullLogger<ExifToolRunner>.Instance);
        UseRunner(runner);
        return runner;
    }

    /// <summary>The real ExifTool when it is installed; otherwise one that is never ready. For tests where ExifTool plays no part.</summary>
    public void UseRealExifToolOrNone()
    {
        if (RealExifTool.Find() is not null)
        {
            UseRealExifTool();
        }
        else
        {
            UseRunner(new MissingExifToolRunner());
        }
    }

    /// <summary>Puts a file in the profile's Managed folder and indexes it, as an upload or a scan would.</summary>
    public async Task<(Guid ItemId, string Path)> AddAsync(string name, byte[] bytes)
    {
        var space = await Fixture.Storage.EnsurePersonalSpaceAsync(Fixture.OwnerProfileId);
        var source = await Fixture.Storage.EnsureManagedSourceAsync(
            space, "Browser uploads", ViewSourceType.BrowserUpload, "builtin:browser-uploads");
        var folder = Path.Combine(Fixture.Storage.GetSourcePath(space, source), "Timeline");
        Directory.CreateDirectory(folder);
        var path = Path.GetFullPath(Path.Combine(folder, name));
        await File.WriteAllBytesAsync(path, bytes);
        var indexed = await Fixture.Service.IndexPathAsync(space.LibraryId, path, Fixture.OwnerProfileId)
            ?? throw new InvalidOperationException("The sample could not be indexed.");
        return (indexed.ItemId, path);
    }

    public IReadOnlyList<MediaFileWriteIntent> Intents(IEnumerable<Guid> itemIds) => itemIds
        .Select(id => new MediaFileWriteIntent { Id = Guid.NewGuid(), TargetKind = MediaFileWriteIntentRepository.ViewItemKind, LocalItemId = id })
        .ToList();

    /// <summary>One run, as the scheduler makes it: start, one batch, finish.</summary>
    public Task<IReadOnlyList<FileWriteResult>> RunAsync(params Guid[] itemIds) => RunWithAsync(Writer, itemIds);

    /// <summary>One run through a given writer (for a writer made after a simulated restart).</summary>
    public async Task<IReadOnlyList<FileWriteResult>> RunWithAsync(ViewItemFileWriter writer, params Guid[] itemIds)
    {
        await writer.BeginRunAsync(CancellationToken.None);
        try
        {
            return await writer.WriteBatchAsync(Intents(itemIds), CancellationToken.None);
        }
        finally
        {
            await writer.EndRunAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Leaves the only copy of a file under its hidden name: the swap takes the original away and blocks its place, and every
    /// recovered name is taken. Returns the neighbour item (to start a later run), the original's path, and the stranded copy.
    /// </summary>
    public async Task<(Guid NeighbourId, string OriginalPath, string Copy)> StrandACopyAsync(string name)
    {
        UseRunner(new MarkerExifToolRunner());
        var (itemId, path) = await AddAsync(name, Jpeg(40));
        var (neighbourId, _) = await AddAsync("neighbour-of-" + name, Jpeg(41));
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var number = 1; number <= 100; number++)
        {
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, stem + ".tuvima-recovered" + (number == 1 ? "" : "-" + number) + ".jpg"), [9]);
        }

        Writer.FileSwap = new DeletingThenBlockingSwap();
        var result = Assert.Single(await RunAsync(itemId));
        Assert.Equal(WriteBackOutcomeKind.Failed, result.Outcome.Kind);
        var copy = Assert.Single(TemporaryCopiesBeside(path));
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddDays(-3));
        File.SetCreationTimeUtc(copy, DateTime.UtcNow.AddDays(-3));
        Directory.Delete(path);
        Writer.FileSwap = new MoveViewFileSwap();
        return (neighbourId, path, copy);
    }

    public string RecordedHash(Guid itemId)
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.ExecuteScalar<string>("""
            SELECT lf.content_hash FROM local_item_files lif JOIN local_files lf ON lf.id = lif.file_id
             WHERE lif.item_id = @itemId AND lif.role = 'primary';
            """, new { itemId })!;
    }

    public long RecordedSize(Guid itemId)
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.ExecuteScalar<long>("""
            SELECT lf.byte_size FROM local_item_files lif JOIN local_files lf ON lf.id = lif.file_id
             WHERE lif.item_id = @itemId AND lif.role = 'primary';
            """, new { itemId });
    }

    public int FileRecordsWithHash(string hash)
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM local_files WHERE content_hash = @hash COLLATE NOCASE;", new { hash });
    }

    public IReadOnlyList<Guid> AllItemIds()
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.Query<Guid>("SELECT id FROM local_items;").ToList();
    }

    public void RenameOwner(string name)
    {
        using var connection = Fixture.Database.CreateConnection();
        connection.Execute("UPDATE profiles SET display_name = @name WHERE id = @id;", new { name, id = Fixture.OwnerProfileId });
    }

    public void ClearAdder(Guid itemId)
    {
        using var connection = Fixture.Database.CreateConnection();
        connection.Execute("UPDATE local_items SET added_by_profile_id = NULL WHERE id = @itemId;", new { itemId });
    }

    /// <summary>
    /// Records the item's file at a second path (indexed earlier than the real one, so the real one stays the item's path), in another
    /// library unless one is named. Returns the library it was put in.
    /// </summary>
    public Guid AddSecondPlaceFor(Guid itemId, string secondPath, Guid? library = null)
    {
        var otherLibrary = library ?? Guid.NewGuid();
        using var connection = Fixture.Database.CreateConnection();
        connection.Execute("""
            INSERT INTO local_file_sources (id, file_id, library_id, source_id, device_id, file_path, modified_at, indexed_at)
            SELECT @id, lif.file_id, @otherLibrary,
                   (SELECT s.source_id FROM local_file_sources s WHERE s.file_id = lif.file_id AND s.library_id = @otherLibrary LIMIT 1),
                   NULL, @secondPath, @then, @then
              FROM local_item_files lif JOIN local_items li ON li.id = lif.item_id
             WHERE lif.item_id = @itemId AND lif.role = 'primary';
            """, new { id = Guid.NewGuid(), itemId, secondPath, otherLibrary, then = DateTimeOffset.UtcNow.AddDays(-1) });
        return otherLibrary;
    }

    /// <summary>The fingerprint of the file record a library's path points at.</summary>
    public string HashRecordedForPlace(Guid library, string path)
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.ExecuteScalar<string>("""
            SELECT lf.content_hash FROM local_file_sources lfs JOIN local_files lf ON lf.id = lfs.file_id
             WHERE lfs.library_id = @library AND lfs.file_path = @path;
            """, new { library, path })!;
    }

    private ViewSharedTransferService? _transfers;

    public ViewSharedTransferService Transfers =>
        _transfers ??= new ViewSharedTransferService(Fixture.Database, Fixture.Repository, Fixture.Storage, Fixture.Spaces, new SourceMutationPolicyGate(), Queue);

    public Guid SharedItemOf(Guid originItemId)
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.QuerySingle<Guid>("SELECT item_id FROM view_shared_assets WHERE origin_item_id = @originItemId;", new { originItemId });
    }

    public void SetTransferState(Guid originItemId, string state)
    {
        using var connection = Fixture.Database.CreateConnection();
        connection.Execute("UPDATE view_shared_transfers SET state = @state WHERE item_id = @originItemId;", new { state, originItemId });
    }

    public void SaveWriteBack(Action<WriteBackConfiguration> change)
    {
        var settings = Fixture.Configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration();
        change(settings);
        Fixture.Configuration.SaveConfig(string.Empty, "writeback", settings);
    }

    /// <summary>Puts items on the real write list and sets their state, as a finished run would.</summary>
    public async Task EnqueueForRealAsync(IReadOnlyList<Guid> itemIds, string status, Guid? except)
    {
        var intents = new MediaFileWriteIntentRepository(Fixture.Database);
        foreach (var id in itemIds)
        {
            await intents.EnqueueViewItemAsync(id, MediaFileWriteTriggers.ViewAddedBy);
            if (id == except)
            {
                continue;
            }

            using var connection = Fixture.Database.CreateConnection();
            connection.Execute(
                "UPDATE media_file_write_intents SET status = @status WHERE local_item_id = @id;", new { status, id });
        }
    }

    public int IntentRows()
    {
        using var connection = Fixture.Database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM media_file_write_intents WHERE target_kind = 'view_item';");
    }

    public IReadOnlyList<string> TemporaryCopiesBeside(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, ViewStorageService.WriteCopyPrefix + "*");

    public async Task<string?> ReadTagAsync(string path, string tag)
    {
        await using var session = await _runner.OpenSessionAsync(CancellationToken.None);
        var tags = await session!.ReadAsync(path, [tag], CancellationToken.None);
        return tags.TryGetValue(tag, out var value) ? value : null;
    }

    /// <summary>A fingerprint of the picture or video data alone (not the metadata), from ExifTool, or ffmpeg where ExifTool has none.</summary>
    public string MediaFingerprint(string path)
    {
        var tags = new[] { "ImageDataHash", "MediaDataHash" };
        var session = _runner.OpenSessionAsync(CancellationToken.None).GetAwaiter().GetResult();
        var read = session!.ReadAsync(path, tags, CancellationToken.None).GetAwaiter().GetResult();
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var tag in tags)
        {
            if (read.TryGetValue(tag, out var value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return FfmpegMd5(path);
    }

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static byte[] Jpeg(int seed) => Encode(SKEncodedImageFormat.Jpeg, seed);

    public static byte[] Png(int seed) => Encode(SKEncodedImageFormat.Png, seed);

    private static byte[] Encode(SKEncodedImageFormat format, int seed)
    {
        using var bitmap = new SKBitmap(32, 32);
        bitmap.Erase(new SKColor((byte)(seed * 5 % 256), (byte)(255 - seed * 3 % 256), (byte)(seed * 7 % 256)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A real, small sample of the format: Skia for the common pictures, ffmpeg for the rest.</summary>
    public byte[] MakeMedia(string extension)
    {
        switch (extension)
        {
            case "jpg":
            case "jpeg":
                return Jpeg(11);
            case "png":
                return Png(12);
            case "webp":
                return Encode(SKEncodedImageFormat.Webp, 13);
        }

        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg is needed for this sample.");
        var output = Path.Combine(_toolRoot, "sample-" + Guid.NewGuid().ToString("N") + "." + extension);
        var video = extension is "mp4" or "mov" or "m4v";
        var arguments = new List<string> { "-loglevel", "error", "-y", "-f", "lavfi", "-i", video ? "color=c=blue:s=64x64:d=1" : "color=c=red:s=32x32:d=1" };
        if (video)
        {
            arguments.AddRange(["-pix_fmt", "yuv420p"]);
        }
        else
        {
            arguments.AddRange(["-frames:v", "1"]);
        }

        if (extension == "avif")
        {
            arguments.AddRange(["-c:v", "libaom-av1"]);
        }

        if (extension == "m4v")
        {
            arguments.AddRange(["-f", "mp4"]);
        }

        arguments.Add(output);
        Run(ffmpeg, arguments);
        return File.ReadAllBytes(output);
    }

    private string FfmpegMd5(string path)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg is needed for this check.");
        return Run(ffmpeg, ["-loglevel", "error", "-i", path, "-map", "0:v:0", "-f", "md5", "-"]).Trim();
    }

    private static string Run(string program, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"{Path.GetFileName(program)} failed: {errors}");
    }

    public static string? FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), name))
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>Small PDFs built by hand, with a correct cross-reference table so they open and can be appended to.</summary>
internal static class MinimalPdf
{
    public static byte[] Plain() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");

    public static byte[] Signed() => Build(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /SigFlags 3 >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] /V 5 0 R /P 3 0 R >>",
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 100 200 300] /Contents <00> >>");

    public static byte[] Encrypted() => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /Filter /Standard /V 1 /R 2 /O (0123456789012345678901234567890) /U (0123456789012345678901234567890) /P -4 >>");

    private static byte[] Build(params string[] objects)
    {
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xref = text.Length;
        text.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        var encrypt = objects.Any(o => o.Contains("/Filter /Standard", StringComparison.Ordinal)) ? $" /Encrypt {objects.Length} 0 R /ID [<00112233445566778899aabbccddeeff><00112233445566778899aabbccddeeff>]" : string.Empty;
        text.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R").Append(encrypt).Append(" >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}

/// <summary>Word, Excel and PowerPoint files made with Open XML, and the broken ones the tests need.</summary>
internal static class OfficeFiles
{
    public static byte[] Make(string extension, string author)
    {
        var path = Path.Combine(Path.GetTempPath(), "tuvima-office-" + Guid.NewGuid().ToString("N") + "." + extension);
        try
        {
            switch (extension)
            {
                case "docx":
                    using (var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
                    {
                        document.AddMainDocumentPart().Document = new Word.Document(
                            new Word.Body(new Word.Paragraph(new Word.Run(new Word.Text("Hello")))));
                        document.PackageProperties.Creator = author;
                    }

                    break;
                case "xlsx":
                    using (var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
                    {
                        var workbook = document.AddWorkbookPart();
                        workbook.Workbook = new Spreadsheet.Workbook();
                        var sheet = workbook.AddNewPart<WorksheetPart>();
                        sheet.Worksheet = new Spreadsheet.Worksheet(new Spreadsheet.SheetData());
                        workbook.Workbook.AppendChild(new Spreadsheet.Sheets(new Spreadsheet.Sheet
                        {
                            Id = workbook.GetIdOfPart(sheet),
                            SheetId = 1,
                            Name = "Sheet1",
                        }));
                        document.PackageProperties.Creator = author;
                    }

                    break;
                default:
                    using (var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
                    {
                        document.AddPresentationPart().Presentation = new DocumentFormat.OpenXml.Presentation.Presentation();
                        document.PackageProperties.Creator = author;
                    }

                    break;
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The author recorded in the file, read straight from the package.</summary>
    public static string? Author(string path)
    {
        using var package = System.IO.Packaging.Package.Open(path, FileMode.Open, FileAccess.Read);
        return package.PackageProperties.Creator;
    }

    public static bool Opens(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx":
                using (var document = WordprocessingDocument.Open(path, false))
                {
                    return document.MainDocumentPart?.Document?.Body?.InnerText == "Hello";
                }

            case ".xlsx":
                using (var document = SpreadsheetDocument.Open(path, false))
                {
                    return document.WorkbookPart?.Workbook is not null;
                }

            default:
                using (var document = PresentationDocument.Open(path, false))
                {
                    return document.PresentationPart?.Presentation is not null;
                }
        }
    }

    /// <summary>An encrypted Office file is not a zip package but an older compound-file container.</summary>
    public static byte[] EncryptedContainer()
    {
        var bytes = new byte[4096];
        byte[] header = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>A package with the signature parts Office adds when a document is digitally signed.</summary>
    public static byte[] WithSignature(byte[] document)
    {
        using var stream = new MemoryStream();
        stream.Write(document);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("_xmlsignatures/origin.sigs").Open();
            entry.Write("signature"u8);
        }

        return stream.ToArray();
    }

    /// <summary>A package whose relationships name a digital-signature origin, without any signature folder.</summary>
    public static byte[] WithSignatureRelationship(byte[] document) => ChangePart(document, "_rels/.rels", xml => xml.Replace(
        "</Relationships>",
        "<Relationship Id=\"rIdSig\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/digital-signature/origin\" Target=\"_xmlsignatures/origin.sigs\"/></Relationships>",
        StringComparison.Ordinal));

    /// <summary>A Word file whose main part says it is Strict Open XML.</summary>
    public static byte[] AsStrict(byte[] document) => ChangePart(document, "word/document.xml", xml => xml.Replace(
        "<w:document ", "<w:document w:conformance=\"strict\" ", StringComparison.Ordinal));

    private static byte[] ChangePart(byte[] document, string partName, Func<string, string> change)
    {
        using var stream = new MemoryStream();
        stream.Write(document);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = archive.GetEntry(partName) ?? throw new InvalidOperationException(partName + " is missing from the sample.");
            string xml;
            using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                xml = reader.ReadToEnd();
            }

            var changed = change(xml);
            Assert.NotEqual(xml, changed);
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(partName).Open(), new UTF8Encoding(false));
            writer.Write(changed);
        }

        return stream.ToArray();
    }

    /// <summary>A zip with a damaged content-types part: it looks like a package and fails when opened.</summary>
    public static byte[] PackageThatIsNotADocument()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("[Content_Types].xml").Open();
            entry.Write("<<< this is not xml"u8);
        }

        return stream.ToArray();
    }
}

public sealed class ExifToolWriteTimeoutTests
{
    private static readonly TimeSpan Usual = TimeSpan.FromSeconds(60);

    [Theory]
    [InlineData(0L, 60)]
    [InlineData(-5L, 60)]
    [InlineData(1024L * 1024 * 1024, 90)]
    [InlineData(10L * 1024 * 1024 * 1024, 360)]
    [InlineData(100L * 1024 * 1024 * 1024, 1800)]
    [InlineData(long.MaxValue, 1800)]
    public void TheTimeForAWrite_GrowsWithTheFileSize_ButNeverPastThirtyMinutes(long bytes, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ExifToolWriteOptions.TimeoutFor(bytes, Usual));
    }

    [Fact]
    public void ACopyOptionWithoutASize_KeepsTheSessionsOwnLimit_AndOneWithASizeCarriesItsOwn()
    {
        Assert.Null(ExifToolWriteOptions.ToCopy("/tmp/x.jpg").Timeout);
        Assert.Equal(TimeSpan.FromSeconds(90), ExifToolWriteOptions.ToCopy("/tmp/x.jpg", 1024L * 1024 * 1024).Timeout);
    }
}
