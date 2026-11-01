using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;
using MediaEngine.Web.Components.Import;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The shared Import (Collections and lists spec D17): one dialog that recognises a bookmark pack, a pack file and a Goodreads CSV and
// opens the right preview for each, and a list step that turns a dropped list into the draft the New collection flow creates from.
public sealed class ImportPanelTests : AsyncBunitContext
{
    private readonly Dictionary<string, ImportPreviewDto> _answers = new(StringComparer.Ordinal);
    private readonly List<ImportApplyRequest> _applied = [];
    private readonly List<ListImportPreviewRequest> _listPreviews = [];

    public ImportPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.RecogniseImportAsync), args =>
            {
                var request = (ImportRecogniseRequest)args![0]!;
                return Task.FromResult<ImportPreviewDto?>(_answers.GetValueOrDefault(request.FileName));
            });
            stub.SetHandler(nameof(IEngineApiClient.ApplyImportAsync), args =>
            {
                _applied.Add((ImportApplyRequest)args![0]!);
                return Task.FromResult<ImportApplyResultDto?>(new ImportApplyResultDto(true, "Added 2 settings to Example."));
            });
            stub.SetHandler(nameof(IEngineApiClient.PreviewCollectionListAsync), args =>
            {
                var request = (ListImportPreviewRequest)args![0]!;
                _listPreviews.Add(request);
                return Task.FromResult<ListImportPreviewDto?>(GoodreadsList(request.Shelf));
            });
        }));
    }

    private static ImportPreviewDto BookmarkPack() => new()
    {
        Kind = ImportKinds.BookmarkPack,
        KindLabel = "Bookmark pack",
        Recognised = true,
        Heading = "Best scenes",
        Subheading = "2 bookmarks for Heat",
        Facts = [new ImportFactDto("Bookmarks", "2"), new ImportFactDto("For", "Heat")],
    };

    private static ImportPreviewDto PackFile() => new()
    {
        Kind = ImportKinds.PackFile,
        KindLabel = "Collection pack",
        Recognised = true,
        Heading = "Film classics",
        Subheading = "A ready-made set of collections",
        Message = "Add it from New collection, Start from a pack.",
    };

    private static ListImportPreviewDto GoodreadsList(string? shelf = null) => new()
    {
        SourceKind = "goodreads",
        SourceLabel = "Goodreads export",
        SourceName = "goodreads.csv",
        SuggestedName = shelf is null ? "Goodreads" : $"Goodreads: {shelf}",
        EntryCount = 4,
        OwnedCount = 3,
        UnmatchedCount = 1,
        Shelves = [new ListImportShelfDto("to-read", 2, 2), new ListImportShelfDto("read", 2, 1)],
        Entries = [new CollectionListEntryDto { Position = 4, Title = "Snow Crash", Year = 1992, Status = ListImportChoices.StatusUnmatched }],
        Owned = [new ListImportOwnedItemDto(Guid.NewGuid(), "Dune", "Books", null)],
        MatchedWorkIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()],
    };

    private ImportPreviewDto GoodreadsPreview() => new()
    {
        Kind = ImportKinds.CollectionList,
        KindLabel = "Collection list",
        Recognised = true,
        Heading = "Goodreads",
        Subheading = "Goodreads export, 4 titles",
        Facts = [new ImportFactDto("You own", "3 of 4")],
        List = GoodreadsList(),
    };

    private static void Choose(IRenderedComponent<ImportPanel> cut, string fileName) =>
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("content", fileName));

    [Fact]
    public void OnePanel_OpensTheRightPreviewForABookmarkPack_APackFile_AndAGoodreadsCsv()
    {
        _answers["scenes.zip"] = BookmarkPack();
        _answers["classics.json"] = PackFile();
        _answers["goodreads.csv"] = GoodreadsPreview();
        var cut = Render<ImportPanel>();

        Choose(cut, "scenes.zip");
        cut.WaitForAssertion(() => Assert.Equal(ImportKinds.BookmarkPack, cut.Find("[data-import-frame]").GetAttribute("data-import-kind")));
        Assert.Contains("2 bookmarks for Heat", cut.Markup);

        Choose(cut, "classics.json");
        cut.WaitForAssertion(() => Assert.Equal(ImportKinds.PackFile, cut.Find("[data-import-frame]").GetAttribute("data-import-kind")));
        Assert.Contains("Add it from New collection, Start from a pack.", cut.Markup);

        Choose(cut, "goodreads.csv");
        cut.WaitForAssertion(() => Assert.Equal(ImportKinds.CollectionList, cut.Find("[data-import-frame]").GetAttribute("data-import-kind")));
        Assert.Contains("3 of 4", cut.Markup);
    }

    [Fact]
    public void ThePanel_SaysFilesStayOnThisMachine()
    {
        var cut = Render<ImportPanel>();

        Assert.Equal(ImportPrivacy.FilesStayOnThisMachine, cut.Find("[data-import-privacy]").TextContent.Trim());
    }

    [Fact]
    public void AFileThatIsNotUnderstood_ShowsTheEngineSentence_AndRaisesNothing()
    {
        _answers["notes.bin"] = new ImportPreviewDto
        {
            Kind = ImportKinds.Unknown,
            Recognised = false,
            Message = "Couldn't read this file. Accepted: CSV from IMDb, Letterboxd, Trakt, Goodreads or StoryGraph, or a text file with one title per line.",
        };
        ImportRecognition? raised = null;
        var cut = Render<ImportPanel>(parameters => parameters.Add(panel => panel.OnRecognised, EventCallback.Factory.Create<ImportRecognition>(this, recognition => raised = recognition)));

        Choose(cut, "notes.bin");

        cut.WaitForAssertion(() => Assert.Contains("Couldn't read this file.", cut.Find("[data-import-error]").TextContent));
        Assert.Null(raised);
        Assert.Empty(cut.FindAll("[data-import-frame]"));
    }

    [Fact]
    public void AKindThisPlaceCannotUse_StillGetsItsPreview_AndSaysWhereItGoes()
    {
        _answers["scenes.zip"] = BookmarkPack();
        ImportRecognition? raised = null;
        var cut = Render<ImportPanel>(parameters => parameters
            .Add(panel => panel.AllowedKinds, [ImportKinds.CollectionList])
            .Add(panel => panel.OnRecognised, EventCallback.Factory.Create<ImportRecognition>(this, recognition => raised = recognition)));

        Choose(cut, "scenes.zip");

        cut.WaitForAssertion(() => Assert.Contains("a bookmark pack", cut.Find("[data-import-elsewhere]").TextContent));
        Assert.Contains("Best scenes", cut.Markup);
        Assert.Null(raised);
    }

    [Fact]
    public void AKindThisPlaceCanUse_RaisesTheFileWithItsContent()
    {
        _answers["goodreads.csv"] = GoodreadsPreview();
        ImportRecognition? raised = null;
        var cut = Render<ImportPanel>(parameters => parameters
            .Add(panel => panel.AllowedKinds, [ImportKinds.CollectionList])
            .Add(panel => panel.OnRecognised, EventCallback.Factory.Create<ImportRecognition>(this, recognition => raised = recognition)));

        Choose(cut, "goodreads.csv");

        cut.WaitForAssertion(() => Assert.NotNull(raised));
        Assert.Equal("goodreads.csv", raised!.FileName);
        Assert.Equal(Convert.ToBase64String("content"u8.ToArray()), raised.ContentBase64);
    }

    [Fact]
    public async Task PluginSettings_AreAppliedFromTheDialog_AndTheResultIsShown()
    {
        _answers["settings.json"] = new ImportPreviewDto
        {
            Kind = ImportKinds.PluginSettings,
            KindLabel = "Plugin settings",
            Recognised = true,
            Heading = "Example",
            CanApply = true,
            ApplyLabel = "Add plugin settings",
        };
        var cut = Render<ImportPanel>();

        Choose(cut, "settings.json");
        cut.WaitForAssertion(() => Assert.Contains("Add plugin settings", cut.Markup));
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Add plugin settings").ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("Added 2 settings to Example.", cut.Find("[data-import-applied]").TextContent));
        Assert.Equal(ImportKinds.PluginSettings, Assert.Single(_applied).Kind);
    }

    [Fact]
    public void ThePasteBox_IsOfferedOnlyWhereAListCanBePasted()
    {
        Assert.Empty(Render<ImportPanel>().FindAll("textarea"));
        Assert.NotEmpty(Render<ImportPanel>(parameters => parameters.Add(panel => panel.ShowPaste, true)).FindAll("textarea"));
    }

    [Fact]
    public void TheListStep_ShowsWhatYouOwn_TheShelves_AndTheTitlesItCouldNotMatch()
    {
        _answers["goodreads.csv"] = GoodreadsPreview();
        var draft = new ListImportDraft();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, draft));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("content", "goodreads.csv"));

        cut.WaitForAssertion(() => Assert.Contains("You own 3 of 4", cut.Find("[data-list-owned]").TextContent));
        Assert.Contains("to-read", cut.Find("[data-list-shelves]").TextContent);
        Assert.Contains("read", cut.Find("[data-list-shelves]").TextContent);
        Assert.Contains("Snow Crash", cut.Find("[data-list-unmatched]").TextContent);
        Assert.True(draft.HasList);
    }

    [Fact]
    public async Task TickingTwoShelves_MeansTwoCollections_AndCountsOnlyThoseShelves()
    {
        _answers["goodreads.csv"] = GoodreadsPreview();
        var draft = new ListImportDraft();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, draft));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("content", "goodreads.csv"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-list-shelves]")));

        foreach (var row in cut.Find("[data-list-shelves]").QuerySelectorAll("input[type=checkbox]"))
        {
            await cut.InvokeAsync(() => row.Change(true));
        }

        cut.WaitForAssertion(() => Assert.Equal(2, draft.CollectionCount));
        Assert.Equal(3, draft.OwnedCount);
        Assert.Equal(4, draft.EntryCount);
    }

    [Fact]
    public async Task IgnoringAnUnmatchedTitle_IsHeldInTheDraft_AndReadAgainByTheEngine()
    {
        _answers["goodreads.csv"] = GoodreadsPreview();
        var draft = new ListImportDraft();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, draft));
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("content", "goodreads.csv"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-list-fix-row]")));

        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Ignore").ClickAsync();

        cut.WaitForAssertion(() => Assert.Single(_listPreviews));
        var fix = Assert.Single(draft.Overrides);
        Assert.Equal(4, fix.Position);
        Assert.True(fix.Ignore);
        Assert.Equal(fix.Position, Assert.Single(_listPreviews[0].Overrides).Position);
    }

    private static ImportPreviewDto TmdbAddress() => new()
    {
        Kind = ImportKinds.CollectionList,
        KindLabel = "List address",
        Recognised = true,
        Heading = "Sci-fi picks",
        Subheading = "TMDb list, 3 titles",
        PrivacyNote = ImportPrivacy.AddressGoesTo("TMDb"),
        List = new ListImportPreviewDto
        {
            SourceKind = "tmdb_list",
            SourceLabel = "TMDb list",
            SourceName = "https://www.themoviedb.org/list/8136",
            SourceAddress = "https://www.themoviedb.org/list/8136",
            IsWebSource = true,
            SuggestedName = "Sci-fi picks",
            EntryCount = 3,
            OwnedCount = 2,
            UnmatchedCount = 1,
            Owned = [new ListImportOwnedItemDto(Guid.NewGuid(), "The Matrix", "Movies", null)],
            MatchedWorkIds = [Guid.NewGuid(), Guid.NewGuid()],
        },
    };

    [Fact]
    public async Task APastedWebAddress_ShowsThePreview_OffersRefreshDefaultingToWeekly_AndNamesTheSite()
    {
        _answers["pasted-list.txt"] = TmdbAddress();
        var draft = new ListImportDraft();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, draft));

        cut.Find("textarea").Input("https://www.themoviedb.org/list/8136");
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Read this text").ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("You own 2 of 3", cut.Find("[data-list-owned]").TextContent));
        Assert.Contains("https://www.themoviedb.org/list/8136", cut.Markup);
        Assert.Equal(ListImportChoices.RefreshWeekly, draft.Refresh);
        Assert.True(draft.IsWeb);
        Assert.Contains("Weekly", cut.Find("[data-list-choices]").TextContent);
        Assert.Contains("Never", cut.Find("[data-list-choices]").TextContent);
        Assert.Equal("Only this address and the list's identifiers go to TMDb.", cut.Find("[data-import-privacy]").TextContent.Trim());
    }

    [Fact]
    public void AFileList_HasNoRefreshChoice_BecauseThereIsNowhereToLookAgain()
    {
        _answers["goodreads.csv"] = GoodreadsPreview();
        var draft = new ListImportDraft();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, draft));

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("content", "goodreads.csv"));

        cut.WaitForAssertion(() => Assert.True(draft.HasList));
        Assert.False(draft.IsWeb);
        Assert.Equal(ListImportChoices.RefreshNever, draft.Refresh);
        Assert.DoesNotContain("Weekly", cut.Find("[data-list-choices]").TextContent);
    }

    [Fact]
    public async Task ASwitchedOffSource_ShowsTheEngineSentence_WithALinkToThePluginsPage_AndFilesStillWork()
    {
        _answers["pasted-list.txt"] = new ImportPreviewDto
        {
            Kind = ImportKinds.Unknown,
            Recognised = false,
            Message = "TMDb lists are switched off. Turn the source on in Settings → Plugins.",
            MessageLinkHref = "/settings/plugins",
            MessageLinkLabel = "Open Plugins",
        };
        _answers["goodreads.csv"] = GoodreadsPreview();
        var cut = Render<ImportPanel>(parameters => parameters.Add(panel => panel.ShowPaste, true));

        cut.Find("textarea").Input("https://www.themoviedb.org/list/8136");
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Read this text").ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("TMDb lists are switched off.", cut.Find("[data-import-error]").TextContent));
        var link = cut.Find("[data-import-error-link]");
        Assert.Equal("/settings/plugins", link.GetAttribute("href"));
        Assert.Equal("Open Plugins", link.TextContent.Trim());

        Choose(cut, "goodreads.csv");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-import-error]")));
        Assert.Equal(ImportKinds.CollectionList, cut.Find("[data-import-frame]").GetAttribute("data-import-kind"));
    }

    [Fact]
    public async Task AnAddressOutsideThisApp_IsNeverMadeIntoALink()
    {
        _answers["pasted-list.txt"] = new ImportPreviewDto
        {
            Kind = ImportKinds.Unknown,
            Recognised = false,
            Message = "Not usable.",
            MessageLinkHref = "https://evil.example/",
            MessageLinkLabel = "Go",
        };
        var cut = Render<ImportPanel>(parameters => parameters.Add(panel => panel.ShowPaste, true));

        cut.Find("textarea").Input("https://www.themoviedb.org/list/8136");
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Read this text").ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("Not usable.", cut.Find("[data-import-error]").TextContent));
        Assert.Empty(cut.FindAll("[data-import-error-link]"));
    }
}
