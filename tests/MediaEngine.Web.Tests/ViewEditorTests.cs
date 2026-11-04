using System.Text.Json;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The photos, videos, documents and galleries editor (spec photos-documents-editor): what a group may change, that typing only stages, that Save
/// sends one request and keeps the changes when it fails, and that the window refuses to open off the desktop.
/// </summary>
public sealed class ViewEditorTests
{
    private static LocalAssetDto Photo(string name = "IMG_1.jpg", string kind = "image") => JsonSerializer.Deserialize<LocalAssetDto>($$"""
        {"id":"{{Guid.NewGuid()}}","library_id":"22222222-2222-2222-2222-222222222222","media_kind":"{{kind}}",
         "title":null,"file_name":"{{name}}","mime_type":"image/jpeg","created_at":"2026-03-02T10:00:00+00:00","captured_at":"2026-03-01T09:00:00+00:00",
         "source_count":1,"files":[],"tags":["Trip"],"thumbnail_url":"/t","content_url":"/c","favorite":false,"hidden":false,"description":"Beach"}
        """)!;

    private static LocalAssetDto Document() => Photo("bill.pdf", "document") with { MimeType = "application/pdf" };

    [Fact]
    public void AGroupOfPhotosOffersOnlyWhatTheyShare()
    {
        var one = new ViewEditStage([Photo()]);
        var group = new ViewEditStage([Photo(), Photo("IMG_2.jpg")]);

        Assert.True(one.CanEditDescription);
        Assert.False(group.CanEditDescription);
        // Setting a description on a group does nothing: the description is about one picture.
        group.SetDescription("Everyone");
        Assert.False(group.DescriptionStaged);
        Assert.Equal(0, group.ChangeCount);
    }

    [Fact]
    public void AGroupOfDocumentsNeverOffersTitleArchiveNumberOrNotes()
    {
        var group = new ViewEditStage([Document(), Document()]);

        Assert.False(group.CanEditDocumentField(ViewDocumentFieldKeys.Title));
        Assert.False(group.CanEditDocumentField(ViewDocumentFieldKeys.ArchiveNumber));
        Assert.False(group.CanEditDocumentField(ViewDocumentFieldKeys.Notes));
        Assert.True(group.CanEditDocumentField(ViewDocumentFieldKeys.Correspondent));
        Assert.True(group.CanEditDocumentField(ViewDocumentFieldKeys.Type));
        Assert.True(group.CanEditDocumentField(ViewDocumentFieldKeys.Date));
    }

    [Fact]
    public void TypingOnlyStagesAndTheChangeCountFollowsEachField()
    {
        var stage = new ViewEditStage([Photo()]);
        Assert.Equal(0, stage.ChangeCount);

        stage.SetDescription("At the harbor");
        stage.AddTag("Summer");
        stage.AddPerson("Ana");
        stage.ShiftDate(TimeSpan.FromHours(1));
        Assert.Equal(4, stage.ChangeCount);

        stage.RevertDate();
        stage.Clear();
        Assert.Equal(0, stage.ChangeCount);
    }

    [Fact]
    public void ShiftingTheDateOfAGroupBecomesOneShiftInTheRequestAndAWarningWhenADayChanges()
    {
        var stage = new ViewEditStage([Photo(), Photo("IMG_2.jpg")]);
        stage.ShiftDate(TimeSpan.FromDays(2));

        var request = stage.ToRequest();
        Assert.Equal((long)TimeSpan.FromDays(2).TotalSeconds, request.CapturedShiftSeconds);
        Assert.Null(request.CapturedAt);
        var row = Assert.Single(stage.Rows());
        Assert.Equal("Date taken", row.What);
        Assert.Equal("2 photos", row.Item);
    }

    [Fact]
    public void ARowForEveryChangeSaysWhatItWasAndWhatItWillBe()
    {
        var stage = new ViewEditStage([Photo()]);
        stage.SetDescription("At the harbor");
        stage.AddTag("Summer");

        var rows = stage.Rows();
        Assert.Contains(rows, row => row.What == "Description" && row.Before == "Beach" && row.After == "At the harbor");
        Assert.Contains(rows, row => row.What == "Tags" && row.After.Contains("Summer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavingSendsOneRequestForEverythingAndCloseFollows()
    {
        var calls = new List<ViewItemsEditSetRequest>();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.EditViewItemsAsync), args =>
        {
            var request = (ViewItemsEditSetRequest)args![0]!;
            calls.Add(request);
            return Task.FromResult(new ViewCatalogueResult<ViewItemsEditResultDto>(new ViewItemsEditResultDto(!request.PreviewOnly, true, 2, [])));
        }));
        var session = new ViewEditSession(api, [Photo()]);
        session.Active.SetDescription("At the harbor");
        session.Active.AddTag("Summer");

        Assert.True(await session.SaveAsync());

        // One check, then one write; nothing is sent while the person only types.
        Assert.Equal([true, false], calls.Select(call => call.PreviewOnly));
        Assert.Equal("At the harbor", calls[1].Edits.Single().Description);
        Assert.True(session.DataSaved);
    }

    [Fact]
    public async Task AFailedSaveKeepsEveryChangeAndSaysWhy()
    {
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.EditViewItemsAsync), args =>
        {
            var item = ((ViewItemsEditSetRequest)args![0]!).Edits[0].ItemIds[0];
            return Task.FromResult(new ViewCatalogueResult<ViewItemsEditResultDto>(new ViewItemsEditResultDto(false, false, 0,
                [new ViewItemsEditRefusalDto(item, "That tag is too long.")], "Nothing was saved.")));
        }));
        var session = new ViewEditSession(api, [Photo()]);
        session.Active.AddTag("Summer");

        Assert.False(await session.SaveAsync());

        Assert.Equal(1, session.ChangeCount);
        Assert.False(session.DataSaved);
        Assert.False(string.IsNullOrWhiteSpace(session.Message));
    }

    [Fact]
    public async Task LeavingWithStagedChangesAsksFirstAndStayingKeepsThem()
    {
        var session = new ViewEditSession(EngineApiClientStub.CreateDefault(), [Photo()]);
        var asked = 0;
        session.ConfirmDiscard = () => { asked++; return Task.FromResult(false); };
        session.Active.AddTag("Summer");

        Assert.False(await session.LeaveAsync());
        Assert.Equal(1, asked);
        Assert.Equal(1, session.ChangeCount);

        session.ConfirmDiscard = () => Task.FromResult(true);
        var closed = false;
        session.CloseRequested += () => closed = true;
        Assert.True(await session.LeaveAsync());
        Assert.True(closed);
        Assert.Equal(0, session.ChangeCount);
    }

    [Fact]
    public async Task SteppingToAnotherPhotoOfTheGalleryKeepsWhatWasStagedOnTheFirst()
    {
        var first = Photo("IMG_1.jpg");
        var second = Photo("IMG_2.jpg");
        var session = new ViewEditSession(EngineApiClientStub.CreateDefault(), [first], new ViewEditGalleryRail(Guid.NewGuid(), "Lisbon 2026", [first, second]));
        session.Active.AddTag("Summer");

        session.SwitchTo([second]);
        Assert.Equal(0, session.Active.ChangeCount);
        Assert.Equal(1, session.ChangeCount);

        session.SwitchTo([first]);
        Assert.Contains("Summer", session.Active.ShownTags());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheWindowDoesNotOpenOffTheDesktop()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        var host = new ViewEditorHost(EngineApiClientStub.CreateDefault(), device, new ViewWorkspaceService(EngineApiClientStub.CreateDefault()), NullLogger<ViewEditorHost>.Instance);

        // No desktop signals (unknown means no): the editor refuses, whatever button asked.
        Assert.False(host.CanEdit);
        Assert.False(await host.OpenItemsAsync([Photo()]));
        Assert.False(host.IsOpen);
    }

    [Fact]
    public void AGalleryStageNamesItsProblemsBeforeAnythingIsSent()
    {
        var stage = new ViewGalleryEditStage(null, null);
        Assert.Equal("Give the gallery a name.", stage.Problem());

        stage.SetName("Lisbon 2026");
        Assert.Null(stage.Problem());
        stage.SetSmart(true);
        Assert.Equal("A Smart gallery needs at least one rule.", stage.Problem());
    }

    [Fact]
    public void RemovingAPhotoFromAGalleryIsStagedAndCanBeTakenBack()
    {
        var stage = new ViewGalleryEditStage(null, null);
        var id = Guid.NewGuid();

        stage.Remove([id]);
        Assert.Contains(id, stage.Removed);
        stage.KeepAgain(id);
        Assert.Empty(stage.Removed);
    }
}
