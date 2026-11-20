using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Spec view-people-search WP5: words inside photos are read only for a person who switched it on, and then found by search.</summary>
public sealed class PhotoWordsTests
{
    private static readonly DocumentOcrWorkerTiming FastTiming = new(
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.Zero);

    [Fact]
    public async Task SwitchedOff_NoPhotoIsScanned_AndSwitchedOn_TheExistingScreenshotIsFoundByItsWords()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (_, _) => Task.FromResult(new OcrRecognition("marmalade invoice number 4471", 93.2, 5)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr, contentKind: ViewContentKinds.Photos);
        var profiles = new ViewProfileRepository(fixture.Database);
        var service = new ViewLibraryService(
            fixture.Repository, fixture.Configuration, fixture.Spaces, fixture.Storage, NullLogger<ViewLibraryService>.Instance,
            operations: fixture.Operations, ocrQueue: fixture.OcrQueue, profiles: profiles);
        fixture.WritePersonal("screenshot.png", OcrFixtures.Png());
        fixture.WritePersonal("raw.cr2", [1, 2, 3]);

        // Off by default: indexing the photo queues nothing, so nothing is read and the words cannot be found.
        Assert.False((await profiles.GetPreferencesAsync(fixture.OwnerProfileId)).ReadWordsInPhotos);
        await service.ScanAsync(fixture.PersonalLibraryId);
        Assert.Empty(await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20));
        Assert.Equal(0, fixture.Search("marmalade"));

        // Switching it on processes the photos already there, and only photos of that person's own library.
        Assert.True(await profiles.SaveReadWordsInPhotosAsync(fixture.OwnerProfileId, true));
        Assert.Equal(0, await fixture.OcrQueue!.QueuePhotoWordsAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(1, await fixture.OcrQueue.QueuePhotoWordsAsync(fixture.PersonalLibraryId, CancellationToken.None));
        var worker = new DocumentOcrOperationWorker(
            fixture.Operations,
            new DocumentOcrService(fixture.Repository, fixture.Configuration, ocr, OcrEngineRoom.For(ocr, fixture.Configuration, null), NullLogger<DocumentOcrService>.Instance),
            fixture.OcrQueue, new ManualPlaybackProbe(), fixture.Configuration, NullLogger<DocumentOcrOperationWorker>.Instance, FastTiming);
        while (await worker.RunOnceAsync(CancellationToken.None) == DocumentOcrOperationWorker.WorkerStep.Worked)
        {
        }

        Assert.Equal(1, fixture.Search("marmalade"));

        // A photo added later is queued straight away while the switch stays on.
        fixture.WritePersonal("receipt.png", OcrFixtures.Png(640, 420));
        await service.ScanAsync(fixture.PersonalLibraryId);
        Assert.Contains(await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20),
            operation => Path.GetFileName(operation.SourcePath) == "receipt.png");
    }
}
