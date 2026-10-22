using Dapper;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>From a file landing in a folder to a filed document: scan, read, recognise, apply rules, suggest.</summary>
public sealed class DocumentFilingPipelineTests
{
    private static readonly DocumentOcrWorkerTiming FastTiming = new(
        TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.Zero);

    private static ViewCatalogueViewer Viewer(ViewLibraryServiceTests.ViewFixture fixture) =>
        new(fixture.OwnerProfileId, [fixture.PersonalLibraryId]);

    private static DocumentOcrOperationWorker OcrWorker(ViewLibraryServiceTests.ViewFixture fixture, FakeOcrService ocr) => new(
        fixture.Operations,
        new DocumentOcrService(fixture.Repository, fixture.Configuration, ocr, OcrEngineRoom.For(ocr, fixture.Configuration),
            NullLogger<DocumentOcrService>.Instance, fixture.RulesQueue),
        fixture.OcrQueue!,
        new ManualPlaybackProbe(),
        fixture.Configuration,
        NullLogger<DocumentOcrOperationWorker>.Instance,
        FastTiming);

    private static async Task<DocumentRule> AddRuleAsync(
        ViewLibraryServiceTests.ViewFixture fixture, string kind, string value, bool files, params DocumentRuleAction[] actions) =>
        await fixture.RulesRepository!.CreateAsync(new DocumentRule(
            Guid.Empty, fixture.OwnerProfileId, fixture.PersonalSpaceId, "Test rule", true, "all", 0, files,
            [new DocumentRuleCondition(kind, value)], actions));

    private static LocalAssetDto OnlyItem(ViewLibraryServiceTests.ViewFixture fixture) =>
        Assert.Single(fixture.Repository.Query(new LocalAssetQuery(fixture.PersonalLibraryId, Limit: 10)).Items);

    [Fact]
    public async Task ANewDocumentLandsInTheInboxWithSuggestionsAndRulesOnItsNameApplyAtOnce()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(withFiling: true, contentKind: ViewContentKinds.Documents);
        var tag = (await fixture.Catalogue!.CreateEntryAsync(Viewer(fixture), ViewCatalogueKind.Tag, "Receipts", null)).Id;
        await AddRuleAsync(fixture, ViewDocumentRuleConditionKinds.FilenameContains, "receipt", false,
            new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag, null));
        fixture.WritePersonal("receipt-march.txt", "Corner shop receipt. Total 12.40 EUR on 2026-03-04");

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainFilingQueueAsync();

        var info = OnlyItem(fixture).DocumentInfo!;
        Assert.Equal(ViewDocumentFilingStates.Inbox, info.FilingState);
        Assert.Equal(["Receipts"], info.Tags!.Select(item => item.Name).ToArray());
        Assert.Equal(new DateOnly(2026, 3, 4), info.Suggestions!.DocumentDate);
        Assert.False(info.Suggestions.DateFromFile);
        Assert.Equal(1, fixture.FilingRepository!.CountInbox(Viewer(fixture)));
    }

    [Fact]
    public async Task ScannedTextFromTextRecognitionTriggersTheRulesAndAFilingRuleFilesTheDocument()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (call, _) => Task.FromResult(new OcrRecognition($"zeppelin hangar rental page{call} amount due 450.00 EUR", 90, 5)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr, withFiling: true, contentKind: ViewContentKinds.Documents);
        var viewer = Viewer(fixture);
        var tag = (await fixture.Catalogue!.CreateEntryAsync(viewer, ViewCatalogueKind.Tag, "Hangar", null)).Id;
        var correspondent = (await fixture.Catalogue.CreateEntryAsync(viewer, ViewCatalogueKind.Correspondent, "Skyport", null)).Id;
        await AddRuleAsync(fixture, ViewDocumentRuleConditionKinds.TextContains, "Zeppelin Hangar", true,
            new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag, null),
            new DocumentRuleAction(ViewDocumentRuleActionKinds.SetCorrespondent, correspondent, null));
        fixture.WritePersonal("lease.pdf", OcrFixtures.Pdf(string.Empty, string.Empty));

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        await fixture.DrainFilingQueueAsync();

        // No words yet: the words rule cannot match, so the document waits in the Inbox.
        var waiting = OnlyItem(fixture).DocumentInfo!;
        Assert.Equal(ViewDocumentFilingStates.Inbox, waiting.FilingState);
        Assert.Empty(waiting.Tags!);

        await OcrWorker(fixture, ocr).RunOnceAsync(CancellationToken.None);
        await fixture.DrainFilingQueueAsync();

        var filed = OnlyItem(fixture);
        Assert.Equal(ViewDocumentFilingStates.Filed, filed.DocumentInfo!.FilingState);
        Assert.Equal("Skyport", filed.DocumentInfo.Correspondent!.Name);
        Assert.Equal(["Hangar"], filed.DocumentInfo.Tags!.Select(item => item.Name).ToArray());
        Assert.Null(filed.DocumentInfo.Suggestions);
        Assert.Equal(0, fixture.FilingRepository!.CountInbox(viewer));
        Assert.Equal(1, fixture.Search("skyport"));
    }

    [Fact]
    public async Task WhenAnAmountFieldExistsRecognisedTextSuggestsTheAmountButNeverWritesItUntilAccepted()
    {
        var ocr = new FakeOcrService
        {
            OnRecognize = (_, _) => Task.FromResult(new OcrRecognition("Invoice date: 2026-02-10. Total due 99.90 EUR", 90, 5)),
        };
        using var fixture = new ViewLibraryServiceTests.ViewFixture(ocr, withFiling: true, contentKind: ViewContentKinds.Documents);
        var viewer = Viewer(fixture);
        var field = await fixture.Catalogue!.CreateFieldAsync(viewer, "Amount", ViewDocumentFieldKinds.Money, null);
        fixture.WritePersonal("scan.pdf", OcrFixtures.Pdf(string.Empty));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        await OcrWorker(fixture, ocr).RunOnceAsync(CancellationToken.None);

        await fixture.DrainFilingQueueAsync();

        var item = OnlyItem(fixture);
        var read = fixture.Repository.Find(item.Id)!.DocumentInfo!;
        Assert.Equal(99.90, read.Suggestions!.Amount);
        Assert.Equal("EUR", read.Suggestions.Currency);
        Assert.Equal(new DateOnly(2026, 2, 10), read.Suggestions.DocumentDate);
        Assert.Empty(read.FieldValues!);

        await fixture.FilingRepository!.FileFromInboxAsync(viewer, item.Id, new FileInboxWrite(true, null, null, null, null));
        var filed = fixture.Repository.Find(item.Id)!.DocumentInfo!;
        Assert.Equal(field.Id, Assert.Single(filed.FieldValues!).FieldId);
        Assert.Equal(new DateOnly(2026, 2, 10), filed.DocumentDate);
    }

    [Fact]
    public async Task EachTextChangeQueuesTheRulesOnceAndRunningThemAgainChangesNothing()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(withFiling: true, contentKind: ViewContentKinds.Documents);
        var itemId = Guid.NewGuid();

        await fixture.RulesQueue!.TryQueueAfterTextAsync(itemId, "same words", CancellationToken.None);
        await fixture.RulesQueue.TryQueueAfterTextAsync(itemId, "same words", CancellationToken.None);
        Assert.Single(await fixture.Operations.GetQueueAsync(DocumentRulesQueue.QueueName, 20));

        await fixture.RulesQueue.TryQueueAfterTextAsync(itemId, "recognised words replaced them", CancellationToken.None);
        var queued = await fixture.Operations.GetQueueAsync(DocumentRulesQueue.QueueName, 20);
        Assert.Equal(2, queued.Count);
        Assert.All(queued, operation => Assert.Equal(MediaOperationType.ViewDocumentApplyRules, operation.OperationType));
        Assert.Equal("none", DocumentRulesQueue.TextFingerprint(null));
        Assert.Equal(DocumentRulesQueue.TextFingerprint("x"), DocumentRulesQueue.TextFingerprint("x"));
    }

    [Fact]
    public async Task RunningTheSameRulesAgainIsHarmlessAndNeverOverwritesWhatThePersonSet()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(withFiling: true, contentKind: ViewContentKinds.Documents);
        var viewer = Viewer(fixture);
        var correspondent = (await fixture.Catalogue!.CreateEntryAsync(viewer, ViewCatalogueKind.Correspondent, "Rule Co", null)).Id;
        await AddRuleAsync(fixture, ViewDocumentRuleConditionKinds.FileKind, "pdf", false,
            new DocumentRuleAction(ViewDocumentRuleActionKinds.SetCorrespondent, correspondent, null));
        fixture.WritePersonal("a.pdf", OcrFixtures.Pdf("Some typed words long enough to count as text here"));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        await fixture.DrainTextQueueAsync();
        await fixture.DrainFilingQueueAsync();
        var item = OnlyItem(fixture);
        Assert.Equal("Rule Co", fixture.Repository.Find(item.Id)!.DocumentInfo!.Correspondent!.Name);

        // The person changes it; running every rule again (the "apply to existing" button) leaves it alone.
        await fixture.Catalogue.UpdateDocumentDetailsAsync(viewer, item.Id, new ViewDocumentDetailsWrite(
            null, null, "My Choice", null, null, null, null, [], [], null));
        var queued = await fixture.RulesQueue!.QueueLibraryAsync(fixture.PersonalLibraryId, "test-batch", CancellationToken.None);
        await fixture.DrainFilingQueueAsync();

        Assert.Equal(1, queued);
        Assert.Equal("My Choice", fixture.Repository.Find(item.Id)!.DocumentInfo!.Correspondent!.Name);
        Assert.Equal(1, await fixture.RulesQueue.QueueLibraryAsync(fixture.PersonalLibraryId, "test-batch", CancellationToken.None));
        Assert.Single(await fixture.Operations.GetQueueAsync(DocumentRulesQueue.QueueName, 50),
            operation => operation.IdempotencyKey.EndsWith("batch-test-batch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADocumentThatVanishedIsReportedMissingNotFailed()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(withFiling: true, contentKind: ViewContentKinds.Documents);
        await fixture.RulesQueue!.QueueAsync(Guid.NewGuid(), "x", CancellationToken.None);

        Assert.Equal(1, await fixture.FilingWorker!.RunBatchAsync(CancellationToken.None));

        var operation = Assert.Single(await fixture.Operations.GetQueueAsync(DocumentRulesQueue.QueueName, 20));
        Assert.Equal(MediaOperationStatus.MissingConfirmed, operation.Status);
    }
}
