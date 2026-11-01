using System.Text;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Import;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Collections page and a collection's own page for lists and sets (Collections and lists spec WP8, 4g): the source line and badge on
/// each curated tile, a set as one tile with its own page, "Remove set" and Export in its "..." menu, "You own X of Y" in a list's hero,
/// the "Not in your library" section with its empty action slot, and a pack file added from Settings, Import.
/// </summary>
public sealed class CollectionListsAndSetsPageTests : AsyncBunitContext
{
    private static readonly Guid SetId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid ChildId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<Guid> _removed = [];
    private readonly List<Guid> _exported = [];
    private readonly List<CollectionPackCreateRequest> _packRequests = [];
    private bool _canEditSet = true;

    public CollectionListsAndSetsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionSummaryAsync), _ =>
                Task.FromResult<CollectionManagementCatalogViewModel?>(new CollectionManagementCatalogViewModel
                {
                    Id = SetId,
                    Name = "Decades",
                    CollectionType = "Set",
                    FillKind = CollectionFillKinds.Set,
                    CanEdit = _canEditSet,
                }));
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionCatalogAsync), _ =>
                Task.FromResult(new List<CollectionManagementCatalogViewModel>
                {
                    new()
                    {
                        Id = ChildId, Name = "1990s", CollectionType = "Custom", FillKind = CollectionFillKinds.Pack,
                        SetId = SetId, SetName = "Decades", ItemCount = 4, MovieCount = 4,
                    },
                    new() { Id = Guid.NewGuid(), Name = "Unrelated", CollectionType = "Custom", FillKind = CollectionFillKinds.Rules },
                }));
            stub.SetHandler(nameof(IEngineApiClient.RemoveCollectionSetAsync), args =>
            {
                _removed.Add((Guid)args![0]!);
                return Task.FromResult<CollectionSetRemovedResponse?>(new CollectionSetRemovedResponse { RemovedCollections = 3 });
            });
            stub.SetHandler(nameof(IEngineApiClient.ExportCollectionSetAsync), args =>
            {
                _exported.Add((Guid)args![0]!);
                return Task.FromResult<CollectionPackExportResponse?>(new CollectionPackExportResponse
                {
                    File = new CollectionPackFileDto { Name = "Decades", Collections = [new CollectionPackFileEntryDto { Name = "1990s" }] },
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionPacksAsync), args =>
            {
                _packRequests.Add((CollectionPackCreateRequest)args![0]!);
                return Task.FromResult<CollectionPackCreateResponse?>(new CollectionPackCreateResponse
                {
                    Sets = [new CollectionPackCreatedDto { Id = SetId, Name = "Decades" }],
                    Collections = [new CollectionPackCreatedDto { Id = ChildId, Name = "1990s", SetId = SetId }],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.RecogniseImportAsync), _ =>
                Task.FromResult<ImportPreviewDto?>(new ImportPreviewDto
                {
                    Kind = ImportKinds.PackFile,
                    KindLabel = "Collection pack",
                    Recognised = true,
                    Heading = "Decades",
                    Subheading = "A set of collections",
                    Message = "Add it here in Settings, Import, or from New collection, Start from a pack.",
                }));
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    private Task UseDeviceAsync(string formFactor, bool fine = true, bool hover = true) =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: fine, Hover: hover));

    private void SignIn(bool kids = false)
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var grants = new[]
        {
            new AccountProfileGrantDto(
                account, profile, "Profile", null, true, true, false,
                new ProfileAdminProtectionResponse(false, "UntilProfileSwitch", 30, 1), 1, DateTimeOffset.UnixEpoch, kids),
        };
        _session.Set("test-session-token", account, profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(account, profile, true, true, 1, 1, false, false, null, 1, grants, [], []));
    }

    // ── Tiles ────────────────────────────────────────────────────────────────

    [Fact]
    public void AnImportedListTile_SaysList_AndCarriesItsRefreshAndTheUnmatchedBadge()
    {
        var refreshed = new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        var tile = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Top films",
            CollectionType = "Custom",
            FillKind = CollectionFillKinds.List,
            ListSource = new CollectionCatalogListSourceDto { SourceLabel = "Letterboxd export", LastRefreshedAt = refreshed, EntryCount = 250, OwnedCount = 83, UnmatchedCount = 4 },
        });

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, tile));

        Assert.Equal(MediaTileGroupKind.List, tile.GroupKind);
        Assert.Equal("List", cut.Find(".media-group-tile__kind").TextContent.Trim());
        Assert.Equal("From a list, refreshed 14 Oct", cut.Find("[data-group-source]").TextContent.Trim());
        Assert.Equal("Couldn't match 4", cut.Find("[data-unmatched-badge]").TextContent.Trim());
    }

    [Fact]
    public void AnImportedListWithNothingUnmatched_ShowsNoBadge_AndNoRefreshWordsBeforeItsFirstRefresh()
    {
        var tile = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Top films",
            CollectionType = "Custom",
            FillKind = CollectionFillKinds.List,
            ListSource = new CollectionCatalogListSourceDto { SourceLabel = "Letterboxd export", EntryCount = 3, OwnedCount = 3 },
        });

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, tile));

        Assert.Equal("From a list", cut.Find("[data-group-source]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-unmatched-badge]"));
    }

    [Theory]
    [InlineData(CollectionFillKinds.HandPicked, "Hand-picked")]
    [InlineData(CollectionFillKinds.Rules, "Rules")]
    [InlineData(CollectionFillKinds.Pack, "Pack: Decades")]
    [InlineData(CollectionFillKinds.Set, "Set of 7 collections")]
    public void EveryCuratedTile_NamesHowItFillsUp(string fillKind, string expected)
    {
        var tile = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Something",
            CollectionType = fillKind == CollectionFillKinds.Set ? "Set" : "Custom",
            FillKind = fillKind,
            SetName = "Decades",
            ChildCollectionCount = 7,
        });

        var cut = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, tile));

        Assert.Equal(expected, cut.Find("[data-group-source]").TextContent.Trim());
    }

    [Fact]
    public void ASetTile_SaysSet_AndASeriesTileDrawsNoSourceLine()
    {
        var set = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Decades",
            CollectionType = "Set",
            FillKind = CollectionFillKinds.Set,
            ChildCollectionCount = 1,
        });
        var series = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel
        {
            Id = Guid.NewGuid(),
            Name = "Dune Novels",
            CollectionType = "Series",
        });

        var setTile = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, set));
        var seriesTile = Render<MediaGroupTile>(parameters => parameters.Add(component => component.Item, series));

        Assert.Equal("Set", setTile.Find(".media-group-tile__kind").TextContent.Trim());
        Assert.Equal("Set of 1 collection", setTile.Find("[data-group-source]").TextContent.Trim());
        Assert.Null(series.GroupSource);
        Assert.Empty(seriesTile.FindAll("[data-group-source]"));
    }

    [Fact]
    public void TheCuratedSection_ListsSetsAndHidesTheCollectionsInsideASetItShows()
    {
        var set = new CollectionManagementCatalogViewModel { Id = SetId, Name = "Decades", CollectionType = "Set", FillKind = CollectionFillKinds.Set };
        var inside = new CollectionManagementCatalogViewModel { Id = ChildId, Name = "1990s", CollectionType = "Custom", FillKind = CollectionFillKinds.Pack, SetId = SetId };
        var orphan = new CollectionManagementCatalogViewModel { Id = Guid.NewGuid(), Name = "2000s", CollectionType = "Custom", FillKind = CollectionFillKinds.Pack, SetId = Guid.NewGuid() };
        var plain = new CollectionManagementCatalogViewModel { Id = Guid.NewGuid(), Name = "Mine", CollectionType = "Custom", FillKind = CollectionFillKinds.HandPicked };
        var series = new CollectionManagementCatalogViewModel { Id = Guid.NewGuid(), Name = "Dune Novels", CollectionType = "Series" };

        var curated = CollectionsPage.CuratedWithoutSetMembers([set, inside, orphan, plain, series]);

        // A pack collection whose set the person cannot see stays on the page rather than disappearing.
        Assert.Equal(["Decades", "2000s", "Mine"], curated.Select(collection => collection.Name));
    }

    // ── A set's page ─────────────────────────────────────────────────────────

    private static DetailPageViewModel SetPage() => new()
    {
        Id = SetId.ToString("D"),
        EntityType = DetailEntityType.Collection,
        Title = "Decades",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        CollectionInfo = new DetailCollectionInfoViewModel { FillKind = CollectionFillKinds.Set, IsSet = true },
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit this collection", Icon = "edit" }],
    };

    private IRenderedComponent<DetailPage> RenderPage(DetailPageViewModel model) =>
        Render<DetailPage>(parameters => parameters.Add(page => page.Model, model));

    private static IReadOnlyList<AngleSharp.Dom.IElement> OpenPageMoreMenu(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return cut.FindAll("[role='menuitem']");
    }

    [Fact]
    public async Task ASetsPage_ListsItsCollectionsAsGroupTiles_AndNoOthers()
    {
        await UseDeviceAsync("desktop");
        SignIn();

        var cut = RenderPage(SetPage());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-set-children] .media-group-tile")));
        Assert.Contains("1990s", cut.Find("[data-set-children] .media-group-tile__name").TextContent);
        Assert.Equal("Pack: Decades", cut.Find("[data-set-children] [data-group-source]").TextContent.Trim());
        Assert.DoesNotContain("Unrelated", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASetsMoreMenu_OffersExportAndARedRemoveSetLast_ToAPersonWhoMayEditIt()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        var cut = RenderPage(SetPage());
        WaitForSetMenu(cut);

        var items = OpenPageMoreMenu(cut);

        Assert.Contains(items, item => item.TextContent.Trim() == "Export as a pack file");
        var last = items[^1];
        Assert.Equal("Remove set…", last.TextContent.Trim());
        Assert.Contains("app-tone--error", last.ClassList);
    }

    // The set's own entry (does this person may edit it) loads after the page first draws.
    private static void WaitForSetMenu(IRenderedComponent<DetailPage> cut) =>
        cut.WaitForState(() => cut.FindComponent<DetailHero>().Instance.ExtraActions is { Count: > 0 });

    private static void WaitForSetPage(IRenderedComponent<DetailPage> cut) =>
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-set-children] .media-group-tile")));

    private static IReadOnlyList<string> OpenLabels(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        return [.. cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())];
    }

    [Fact]
    public async Task ASetsMoreMenu_OffersNeitherEntryWhenThePersonMayNotEditTheSet()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        _canEditSet = false;
        var cut = RenderPage(SetPage());
        WaitForSetPage(cut);

        var labels = OpenLabels(cut);

        Assert.DoesNotContain("Remove set…", labels);
        Assert.DoesNotContain("Export as a pack file", labels);
    }

    [Theory]
    [InlineData("phone", true, false)]
    [InlineData("tablet", true, true)]
    public async Task ASetsMoreMenu_OffersNeitherEntryOnAPhoneOrTablet(string formFactor, bool fine, bool hover)
    {
        await UseDeviceAsync(formFactor, fine, hover);
        SignIn();
        var cut = RenderPage(SetPage());
        WaitForSetPage(cut);

        // Nothing is left for the "..." to hold on a phone or tablet, so the page draws no menu at all.
        Assert.Empty(cut.FindAll("button[aria-label='More actions']"));
        Assert.DoesNotContain("Remove set…", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Export as a pack file", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingASet_AsksFirst_ThenRemovesTheSetThroughTheEngine()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        var host = Render<AppDialogHost>();
        var cut = RenderPage(SetPage());
        WaitForSetMenu(cut);
        OpenPageMoreMenu(cut);

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Remove set…").Click();

        host.WaitForElement(".app-confirm");
        Assert.Empty(_removed);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Remove set").Click();
        cut.WaitForAssertion(() => Assert.Equal([SetId], _removed));
    }

    [Fact]
    public async Task CancellingTheRemoveSetQuestion_RemovesNothing()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        var host = Render<AppDialogHost>();
        var cut = RenderPage(SetPage());
        WaitForSetMenu(cut);
        OpenPageMoreMenu(cut);
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Remove set…").Click();
        host.WaitForElement(".app-confirm");

        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        Assert.Empty(_removed);
    }

    [Fact]
    public async Task ExportingASet_AsksTheEngineForItsPackFileAndSavesItToDisk()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        var cut = RenderPage(SetPage());
        WaitForSetMenu(cut);
        OpenPageMoreMenu(cut);

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Export as a pack file").Click();

        cut.WaitForAssertion(() => Assert.Equal([SetId], _exported));
        var save = JSInterop.VerifyInvoke("tuvimaDownloads.fromStream");
        Assert.Equal("decades.tuvima-pack.json", save.Arguments[0]);
    }

    [Fact]
    public void ThePackFileName_IsASafeVersionOfTheSetsName_AndTheFileCarriesTheEnginesFormat()
    {
        Assert.Equal("award-winners-best-picture.tuvima-pack.json", CollectionSetPackFile.FileNameFor("Award winners: Best Picture!"));
        Assert.Equal("collection-pack.tuvima-pack.json", CollectionSetPackFile.FileNameFor("   "));

        using var document = JsonDocument.Parse(CollectionSetPackFile.Write(new CollectionPackFileDto { Name = "Decades" }));

        Assert.Equal(CollectionPackFileDto.FormatName, document.RootElement.GetProperty("format").GetString());
        Assert.Equal("Decades", document.RootElement.GetProperty("name").GetString());
    }

    // ── A list's page ────────────────────────────────────────────────────────

    private static DetailPageViewModel ListPage(DetailCollectionListViewModel? list) => new()
    {
        Id = ChildId.ToString("D"),
        EntityType = DetailEntityType.Collection,
        Title = "Top films",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        Metadata = list is null ? [] : [new MetadataPill { Label = $"You own {list.OwnedCount} of {list.EntryCount}", Kind = "list_owned" }],
        CollectionInfo = new DetailCollectionInfoViewModel { FillKind = CollectionFillKinds.List, List = list },
    };

    private static DetailCollectionListViewModel TwoOfFour() => new()
    {
        SourceLabel = "Letterboxd export",
        EntryCount = 4,
        OwnedCount = 2,
        UnmatchedCount = 2,
        Missing =
        [
            new DetailMissingTitleViewModel { Position = 3, Title = "Missing One", Year = 1999 },
            new DetailMissingTitleViewModel { Position = 4, Title = "Missing Two" },
        ],
    };

    [Fact]
    public async Task AListsHero_SaysYouOwnXOfY()
    {
        await UseDeviceAsync("desktop");
        SignIn();

        var cut = RenderPage(ListPage(TwoOfFour()));

        Assert.Contains("You own 2 of 4", cut.Find(".tl-detail-metadata-stack").TextContent);
    }

    [Fact]
    public async Task AListsPage_HasANotInYourLibrarySection_WithGreyedTilesAndAnEmptyActionSlot()
    {
        await UseDeviceAsync("desktop");
        SignIn();

        var cut = RenderPage(ListPage(TwoOfFour()));

        Assert.Equal("Not in your library (2)", cut.Find("[data-not-in-library] h2").TextContent.Trim());
        var tiles = cut.FindAll("[data-missing-title]");
        Assert.Equal(2, tiles.Count);
        Assert.Contains("Missing One", tiles[0].TextContent);
        Assert.Contains("1999", tiles[0].TextContent);
        // The slot exists on every tile and holds nothing: a later Seerr integration fills it.
        Assert.All(tiles, tile => Assert.Empty(tile.QuerySelector("[data-missing-title-action]")!.Children));
        Assert.All(tiles, tile => Assert.Equal(string.Empty, tile.QuerySelector("[data-missing-title-action]")!.TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-not-in-library] button, [data-not-in-library] a"));
    }

    [Fact]
    public async Task AListWithNothingMissing_HasNoNotInYourLibrarySection()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        var complete = new DetailCollectionListViewModel { SourceLabel = "Letterboxd export", EntryCount = 3, OwnedCount = 3 };

        var cut = RenderPage(ListPage(complete));

        Assert.Empty(cut.FindAll("[data-not-in-library]"));
    }

    [Fact]
    public async Task AKidsListPage_HasNeitherTheCountNorTheSection()
    {
        await UseDeviceAsync("desktop");
        SignIn(kids: true);

        var cut = RenderPage(ListPage(null));

        Assert.Empty(cut.FindAll("[data-not-in-library]"));
        Assert.DoesNotContain("You own", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMissingTitleActionSlot_ShowsWhatAnIntegrationPutsInIt()
    {
        var cut = Render<CollectionNotInLibrary>(parameters => parameters
            .Add(component => component.Missing, TwoOfFour().Missing)
            .Add(component => component.ActionSlot, entry => builder => builder.AddContent(0, $"Send {entry.Title}")));

        var slots = cut.FindAll("[data-missing-title-action]");
        Assert.Equal("Send Missing One", slots[0].TextContent.Trim());
    }

    [Fact]
    public void ALongMissingList_ShowsTheFirstPage_ThenEverythingOnRequest()
    {
        var many = Enumerable.Range(1, 130)
            .Select(index => new DetailMissingTitleViewModel { Position = index, Title = $"Title {index}" })
            .ToList();
        var cut = Render<CollectionNotInLibrary>(parameters => parameters.Add(component => component.Missing, many));

        Assert.Equal(48, cut.FindAll("[data-missing-title]").Count);
        Assert.Equal("Not in your library (130)", cut.Find("h2").TextContent.Trim());

        cut.Find("[data-missing-show-all]").Click();

        Assert.Equal(130, cut.FindAll("[data-missing-title]").Count);
        Assert.Empty(cut.FindAll("[data-missing-show-all]"));
    }

    // ── Settings, Import: a pack file ────────────────────────────────────────

    private async Task<IRenderedComponent<OverviewImportPage>> DropAPackAsync(string content)
    {
        var cut = Render<OverviewImportPage>();
        await cut.InvokeAsync(() => { });
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText(content, "decades.tuvima-pack.json"));
        return cut;
    }

    [Fact]
    public async Task APackFileDroppedOnImport_CanBeAddedThereThroughThePacksAction()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        const string text = """{"format":"tuvima-collection-pack","name":"Decades","collections":[{"name":"1990s"}]}""";
        var cut = await DropAPackAsync(text);

        cut.WaitForElement("[data-import-add-pack]").Click();

        cut.WaitForAssertion(() => Assert.Single(_packRequests));
        var request = _packRequests[0];
        Assert.Equal(text, request.FileContent);
        Assert.Empty(request.PackIds);
        Assert.Equal("Profile", request.OwnerKind);
        Assert.Equal("Private", request.Audience);
        cut.WaitForAssertion(() => Assert.Equal("Added 1 collections from this pack.", cut.Find("[data-import-pack-result]").TextContent.Trim()));
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task APackFileDroppedOnImport_CannotBeAddedOnAPhoneOrTablet(string formFactor)
    {
        await UseDeviceAsync(formFactor, fine: formFactor == "tablet", hover: formFactor == "tablet");
        SignIn();
        var cut = await DropAPackAsync("{}");

        cut.WaitForElement("[data-import-frame]");

        Assert.Empty(cut.FindAll("[data-import-add-pack]"));
    }

    [Fact]
    public async Task APackFileDroppedOnImport_CannotBeAddedByAKidsProfile()
    {
        await UseDeviceAsync("desktop");
        SignIn(kids: true);
        var cut = await DropAPackAsync("{}");

        cut.WaitForElement("[data-import-frame]");

        Assert.Empty(cut.FindAll("[data-import-add-pack]"));
        Assert.Empty(_packRequests);
    }

    [Fact]
    public async Task APackFileThatArrivesAsBase64_IsReadAsUtf8TextWithoutItsByteOrderMark()
    {
        await UseDeviceAsync("desktop");
        SignIn();
        const string text = """{"format":"tuvima-collection-pack","name":"Décennies"}""";
        var cut = Render<OverviewImportPage>();
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, "decades.tuvima-pack.json"));

        cut.WaitForElement("[data-import-add-pack]").Click();

        cut.WaitForAssertion(() => Assert.Equal(text, Assert.Single(_packRequests).FileContent));
    }
}
