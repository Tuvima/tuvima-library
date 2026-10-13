using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class DocumentOcrTests
{
    private static readonly DocumentOcrWorkerTiming FastTiming = new(
        PlaybackPollInterval: TimeSpan.FromMilliseconds(20),
        IdleDelay: TimeSpan.FromMilliseconds(10),
        UnavailableDelay: TimeSpan.FromMilliseconds(10),
        LowEffortPause: TimeSpan.Zero);

    // ── Which pages are read ────────────────────────────────────────────────

    [Fact]
    public void PageSelection_ReadsOnlyPagesWithoutAUsableTextLayer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-ocr-pages-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, OcrFixtures.Pdf(
                "This page carries a real text layer with plenty of characters",
                string.Empty,
                "tiny",
                "Another full page of embedded text that must not be read again"));

            var report = PdfPageSelection.ReadTextLayers(path, maxPages: 100, CancellationToken.None);

            Assert.False(report.Encrypted);
            Assert.Equal(4, report.PageCount);
            Assert.Equal([2, 3], PdfPageSelection.PagesNeedingOcr(report.Pages, minimumCharacters: 25));
            Assert.Equal([2], PdfPageSelection.PagesNeedingOcr(report.Pages, minimumCharacters: 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  \r\n\t ", 0)]
    [InlineData(" a b\tc\n", 3)]
    public void PageSelection_CountsOnlyNonWhitespaceCharacters(string? text, int expected)
    {
        Assert.Equal(expected, PdfPageSelection.CountNonWhitespace(text));
    }

    [Fact]
    public void Renderer_DrawsAPageAndNeverExceedsTheLongestSideCap()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-ocr-render-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, OcrFixtures.Pdf(string.Empty));

            using var bitmap = PdfPageRenderer.RenderPage(path, pageIndex: 0, dpi: 300, maxSidePixels: 3500);
            using var small = PdfPageRenderer.RenderPage(path, pageIndex: 0, dpi: 300, maxSidePixels: 1000);

            Assert.InRange(Math.Max(bitmap.Width, bitmap.Height), 2500, 3500);
            Assert.True(Math.Max(small.Width, small.Height) <= 1000);
            Assert.True(bitmap.Height > bitmap.Width, "A4 portrait stays portrait.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Queueing ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TextJob_QueuesRecognitionOnlyForPdfsWithUnreadPages()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("scan.pdf", OcrFixtures.Pdf(string.Empty, string.Empty));
        fixture.WritePersonal("typed.pdf", OcrFixtures.Pdf("This typed page has well over twenty five characters of text"));

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();

        var queue = await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20);
        var queued = Assert.Single(queue);
        Assert.Equal("scan.pdf", Path.GetFileName(queued.SourcePath));
        Assert.Equal(MediaOperationType.ViewDocumentOcr, queued.OperationType);
    }

    [Fact]
    public async Task Photos_AreQueuedStraightAwayAndOnlyOnce()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("receipt.png", OcrFixtures.Png());
        fixture.WritePersonal("raw.cr2", [1, 2, 3]);

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        Assert.Equal("receipt.png", Path.GetFileName(Assert.Single(await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20)).SourcePath));
    }

    [Fact]
    public async Task NothingIsQueued_WhenTheHelperIsMissingOrSwitchedOff()
    {
        var ocr = new FakeOcrService { Capability = OcrCapability.Unavailable("Text recognition is not installed.") };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("receipt.png", OcrFixtures.Png());
        fixture.WritePersonal("scan.pdf", OcrFixtures.Pdf(string.Empty));

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        Assert.Empty(await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20));

        ocr.Capability = new OcrCapability(true, "tesseract", "5.4.0", "tessdata", ["eng"], true, null);
        fixture.Configuration.SaveOcr(new OcrSettings { Enabled = false });
        Assert.False(await fixture.OcrQueue!.TryQueueAsync(Guid.NewGuid(), "hash", "x.png", CancellationToken.None));
    }

    [Fact]
    public async Task QueueingTheSameFileTwiceMakesOneJobButAnEditedFileMakesAnother()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        var itemId = Guid.NewGuid();

        await fixture.OcrQueue!.TryQueueAsync(itemId, "hash-one", "scan.pdf", CancellationToken.None);
        await fixture.OcrQueue.TryQueueAsync(itemId, "hash-one", "scan.pdf", CancellationToken.None);
        Assert.Single(await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20));

        await fixture.OcrQueue.TryQueueAsync(itemId, "hash-two", "scan.pdf", CancellationToken.None);
        Assert.Equal(2, (await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20)).Count);
    }

    // ── Reading ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScannedPdf_IsReadPageByPage_StoredAsOcr_AndFoundBySearch()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (call, _) => Task.FromResult(new OcrRecognition($"zeppelin hangar rental page{call}", 88, 4)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("lease.pdf", OcrFixtures.Pdf(string.Empty, string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        Assert.Equal(LocalAssetTextSources.None, fixture.TextSource(item.Id));
        Assert.Equal(0, fixture.Search("zeppelin"));

        await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, ocr.Calls);
        Assert.Equal(LocalAssetTextSources.Ocr, fixture.TextSource(item.Id));
        Assert.Equal(1, fixture.Search("zeppelin hangar"));
        var metadata = fixture.Metadata(item.Id)!;
        Assert.Contains("\"ocr_average_confidence\":88", metadata, StringComparison.Ordinal);
        Assert.Contains("\"ocr_pages\":2", metadata, StringComparison.Ordinal);
        Assert.Empty(Directory.Exists(ocr.TemporaryRoot) ? Directory.GetDirectories(ocr.TemporaryRoot) : []);
    }

    [Fact]
    public async Task PdfWithSomeTextLayer_KeepsThoseWordsAndIsMarkedMixed()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (_, _) => Task.FromResult(new OcrRecognition("handwritten marginalia gazebo", 70, 3)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("mixed.pdf", OcrFixtures.Pdf("Embedded paragraph about lighthouse keepers and more", string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        Assert.Equal(LocalAssetTextSources.Embedded, fixture.TextSource(item.Id));

        await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, ocr.Calls);
        Assert.Equal(LocalAssetTextSources.Mixed, fixture.TextSource(item.Id));
        Assert.Equal(1, fixture.Search("lighthouse"));
        Assert.Equal(1, fixture.Search("gazebo"));
    }

    [Fact]
    public async Task Photo_IsReadAndStoredAsOcr()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (_, _) => Task.FromResult(new OcrRecognition("marmalade invoice number 4471", 93.2, 5)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("invoice.png", OcrFixtures.Png());
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None);

        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        Assert.Equal(LocalAssetTextSources.Ocr, fixture.TextSource(item.Id));
        Assert.Equal(1, fixture.Search("marmalade invoice"));
        Assert.Contains("93.2", fixture.Metadata(item.Id)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TinyImages_AreSkippedWithoutReading()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("icon.png", OcrFixtures.Png(64, 64));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);

        await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task FailedReRead_KeepsThePreviousGoodText()
    {
        var ocr = new FakeOcrService { OnRecognize = (_, _) => throw new InvalidOperationException("engine crashed") };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("scan.pdf", OcrFixtures.Pdf(string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        await fixture.Repository.SetDocumentTextAsync(
            item.Id, new LocalAssetDocumentTextWrite("previously recognised quartermaster ledger", LocalAssetTextSources.Ocr));

        var outcome = await new DocumentOcrService(
            fixture.Repository, fixture.Configuration, ocr, NullLogger<DocumentOcrService>.Instance)
            .ReadItemAsync(item.Id, null, CancellationToken.None);

        Assert.Equal(DocumentOcrStatus.Failed, outcome.Status);
        Assert.Equal(1, fixture.Search("quartermaster"));
        Assert.Equal(LocalAssetTextSources.Ocr, fixture.TextSource(item.Id));
    }

    [Fact]
    public async Task OneFailingFile_DoesNotStopTheRestOfTheQueue()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (call, _) => call == 1
                ? throw new InvalidOperationException("first file is broken")
                : Task.FromResult(new OcrRecognition("sundial calibration notes", 80, 3)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("a.png", OcrFixtures.Png());
        fixture.WritePersonal("b.png", OcrFixtures.Png(640, 480));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var worker = Worker(fixture, ocr, new ManualPlaybackProbe());

        Assert.Equal(DocumentOcrOperationWorker.WorkerStep.Worked, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(DocumentOcrOperationWorker.WorkerStep.Worked, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(DocumentOcrOperationWorker.WorkerStep.Idle, await worker.RunOnceAsync(CancellationToken.None));

        Assert.Equal(1, fixture.Search("sundial"));
        var statuses = (await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20)).Select(op => op.Status).ToList();
        Assert.Contains(MediaOperationStatus.Succeeded, statuses);
        Assert.Contains(MediaOperationStatus.FailedTerminal, statuses);
    }

    [Fact]
    public async Task SlowFile_IsStoppedAtTheTimeLimit_AndThePagesReadSoFarAreKeptAsPartial()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = async (call, token) =>
            {
                if (call == 1)
                {
                    return new OcrRecognition("first page about astrolabe repair", 90, 5);
                }

                await Task.Delay(Timeout.Infinite, token);
                return new OcrRecognition(string.Empty, null, 0);
            },
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.Configuration.SaveOcr(new OcrSettings { PerFileTimeoutSeconds = 1 });
        fixture.WritePersonal("long.pdf", OcrFixtures.Pdf(string.Empty, string.Empty, string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();

        var started = DateTime.UtcNow;
        await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        Assert.Equal(1, fixture.Search("astrolabe"));
        Assert.Contains("\"document_text_truncated\":true", fixture.Metadata(item.Id)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagesBeyondTheCap_AreNotReadAndTheFileIsMarkedPartial()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.Configuration.SaveOcr(new OcrSettings { MaxPagesPerFile = 2 });
        fixture.WritePersonal("big.pdf", OcrFixtures.Pdf(string.Empty, string.Empty, string.Empty, string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();

        await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, ocr.Calls);
        var item = Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 5)).Items);
        Assert.Contains("\"document_text_truncated\":true", fixture.Metadata(item.Id)!, StringComparison.Ordinal);
    }

    // ── The worker ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Worker_WaitsWhileSomeoneIsPlayingMedia_ThenResumes()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("receipt.png", OcrFixtures.Png());
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var probe = new ManualPlaybackProbe { Playing = true };
        var worker = Worker(fixture, ocr, probe);

        var run = worker.RunOnceAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.False(run.IsCompleted, "The worker must hold back while media is playing.");
        Assert.Equal(0, ocr.Calls);
        Assert.True(probe.Checks >= 3, "The worker keeps polling the probe.");

        probe.Playing = false;
        Assert.Equal(DocumentOcrOperationWorker.WorkerStep.Worked, await run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, ocr.Calls);
    }

    [Fact]
    public async Task Worker_LeavesQueuedFilesAlone_WhenTheHelperIsNotAvailable()
    {
        var ocr = new FakeOcrService();
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("receipt.png", OcrFixtures.Png());
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        ocr.Capability = OcrCapability.Unavailable("Text recognition is not installed.");

        Assert.Equal(DocumentOcrOperationWorker.WorkerStep.Unavailable,
            await Worker(fixture, ocr, new ManualPlaybackProbe()).RunOnceAsync(CancellationToken.None));

        Assert.Equal(MediaOperationStatus.Queued, Assert.Single(await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 5)).Status);
    }

    [Fact]
    public async Task RereadAll_QueuesPdfsAndPhotosWithoutTextAgain()
    {
        // The first pass finds nothing readable, so both files are still "unread" when the person asks again.
        var ocr = new FakeOcrService { OnRecognize = (_, _) => Task.FromResult(new OcrRecognition(string.Empty, null, 0)) };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr);
        fixture.WritePersonal("a.png", OcrFixtures.Png());
        fixture.WritePersonal("b.pdf", OcrFixtures.Pdf(string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        var worker = Worker(fixture, ocr, new ManualPlaybackProbe());
        while (await worker.RunOnceAsync(CancellationToken.None) == DocumentOcrOperationWorker.WorkerStep.Worked)
        {
        }

        Assert.True(await fixture.OcrQueue!.StartRereadAsync(CancellationToken.None));
        for (var wait = 0; wait < 100 && fixture.OcrQueue.RereadProgress.Running; wait++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(2, fixture.OcrQueue.RereadProgress.Queued);
        var waiting = (await fixture.Operations.GetQueueAsync(DocumentOcrQueue.QueueName, 20))
            .Count(operation => operation.Status == MediaOperationStatus.Queued);
        Assert.Equal(2, waiting);
    }

    // ── Privacy ─────────────────────────────────────────────────────────────

    [Fact]
    public void RecognisedText_NeverReachesALogCall()
    {
        var folder = FindOcrSourceFolder();
        var offences = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
        {
            var source = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(
                source, @"(?:_logger|logger)\.Log\w+\((?<args>[^;]*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var arguments = System.Text.RegularExpressions.Regex.Replace(call.Groups["args"].Value, "\"(?:[^\"\\\\]|\\\\.)*\"", "\"\"");
                if (System.Text.RegularExpressions.Regex.IsMatch(
                        arguments, @"\b(?:StandardOutput|StandardError|[Tt]ext|[Rr]ecognition|[Rr]eport|tsv)\b(?!\.Length)"))
                {
                    offences.Add($"{Path.GetFileName(file)}: {call.Value.Split('\n')[0].Trim()}");
                }
            }
        }

        Assert.True(offences.Count == 0, "Recognised text or helper output reaches a log call: " + string.Join("; ", offences));
    }

    private static string FindOcrSourceFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "MediaEngine.Api", "Services", "LocalAssets", "Ocr");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the text recognition source folder.");
    }

    private static DocumentOcrOperationWorker Worker(
        ViewLibraryServiceTests.ViewFixture fixture, FakeOcrService ocr, ManualPlaybackProbe probe) => new(
            fixture.Operations,
            new DocumentOcrService(fixture.Repository, fixture.Configuration, ocr, NullLogger<DocumentOcrService>.Instance),
            fixture.OcrQueue!,
            probe,
            fixture.Configuration,
            NullLogger<DocumentOcrOperationWorker>.Instance,
            FastTiming);
}
