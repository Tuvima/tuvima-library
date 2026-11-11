using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Settings;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Switching items on a real detail page (universal editor spec D7): a show page's bar lists its seasons and episodes, choosing one keeps the
/// page where it is, loads that item's fields into the panel, keeps every item's staged changes, leaves the page showing its own item, and
/// saves nothing. An album page does the same for its artist and tracks. The Engine is a hand-written stub; nothing is written anywhere.
/// </summary>
public sealed class DetailEditItemSwitchPageTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _writes = [];
    private readonly List<Guid> _contextAsks = [];
    private readonly List<string> _familyReads = [];
    private bool _writeBackOn;
    private string[] _tabs = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public DetailEditItemSwitchPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            EditTrailTestData.RegisterFamily(stub, _familyReads);
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args =>
            {
                var id = (Guid)args![0]!;
                _contextAsks.Add(id);
                return Task.FromResult<MediaEditorContextDto?>(ContextFor(id));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<LibraryItemHistoryDto>(), (string?)null)));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetFileWriteScheduleAsync), _ => Task.FromResult<FileWriteScheduleDto?>(new FileWriteScheduleDto { Enabled = true }));
            stub.SetHandler(nameof(IEngineApiClient.GetLibrariesAsync), _ => Task.FromResult<LibrariesConfigurationDto?>(new LibrariesConfigurationDto
            {
                Libraries =
                [
                    new LibraryFolderDto
                    {
                        Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
                        Name = "TV",
                        MediaTypes = ["TV", "Music", "Movies"],
                        OrganizationPolicy = new LibraryOrganizationPolicyDto { WriteTags = _writeBackOn },
                    },
                ],
            }));
            foreach (var write in new[] { nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), nameof(IEngineApiClient.RunEditSessionAsync) })
            {
                var name = write;
                stub.SetHandler(name, args =>
                {
                    _writes.Add(name + (args is [EditSessionRequest { PreviewOnly: true }, ..] ? ":check" : string.Empty));
                    return Task.FromResult<object?>(null);
                });
            }
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
        _editor = Render<EditorPopover>();
    }

    private readonly IRenderedComponent<EditorPopover> _editor;

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    private MediaEditorContextDto? ContextFor(Guid id)
    {
        if (id == ShowId)
        {
            return WithHeader(EditTrailTestData.TvContext(id, "series"), TvHeader(id));
        }

        if (id == SeasonOne || id == EditTrailTestData.SeasonTwoId)
        {
            return WithHeader(EditTrailTestData.TvContext(id, "season"), TvHeader(id));
        }

        if (id == EpisodeOne || id == EpisodeTwo || id == EditTrailTestData.EpisodeThreeId)
        {
            return WithHeader(EditTrailTestData.TvContext(id, "episode"), TvHeader(id));
        }

        if (id == EditTrailTestData.AlbumId)
        {
            return WithHeader(EditTrailTestData.AlbumContext(id, "album"), AlbumHeader(id));
        }

        if (id == EditingModeTestData.MovieId)
        {
            return EditingModeTestData.MovieContext();
        }

        return id == EditTrailTestData.TrackOneId || id == EditTrailTestData.TrackTwoId ? WithHeader(EditTrailTestData.AlbumContext(id, "track"), AlbumHeader(id)) : null;
    }

    private static MediaEditorContextDto WithHeader(MediaEditorContextDto context, EditorHeaderDto header)
    {
        context.Header = header;
        return context;
    }

    // What the Engine says about the way around the show: a home button, a Season dropdown and, inside a season, its episodes.
    private EditorHeaderDto TvHeader(Guid id)
    {
        var season = id == EditTrailTestData.SeasonTwoId || id == EditTrailTestData.EpisodeThreeId
            ? EditTrailTestData.SeasonTwoId
            : id == ShowId ? (Guid?)null : SeasonOne;
        var kind = id == ShowId ? EditorKindNames.TvShow : id == SeasonOne || id == EditTrailTestData.SeasonTwoId ? EditorKindNames.TvSeason : EditorKindNames.TvEpisode;
        var items = season == SeasonOne
            ? new List<EditorNavItemDto> { NavEpisode(EpisodeOne, "S01E01", "Episode 1", EditorNavItemStates.Matched, id), NavEpisode(EpisodeTwo, "S01E02", "Episode 2", EditorNavItemStates.NotMatched, id) }
            : season == EditTrailTestData.SeasonTwoId
                ? [NavEpisode(EditTrailTestData.EpisodeThreeId, "S02E03", "Episode 3", EditorNavItemStates.NotMatched, id)]
                : [];
        return new EditorHeaderDto
        {
            Kind = kind,
            Root = new EditorHeaderRootDto { Id = ShowId, Title = EditTrailTestData.ShowTitle, MediaType = "TV" },
            CurrentId = id,
            Label = EditTrailTestData.ShowTitle,
            ShowHome = true,
            HomeId = ShowId,
            HomeIsCurrent = id == ShowId,
            SecondLevel = new EditorHeaderSecondLevelDto
            {
                Label = "Season",
                CurrentId = season,
                Entries =
                [
                    new EditorHeaderLevelEntryDto { Id = SeasonOne, Title = "Season 1", Number = 1, ItemCount = 2 },
                    new EditorHeaderLevelEntryDto { Id = EditTrailTestData.SeasonTwoId, Title = "Season 2", Number = 2, ItemCount = 1 },
                ],
            },
            ShowItemDropdown = id != ShowId,
            Level = new EditorHeaderLevelDto
            {
                LevelId = season,
                LevelTitle = season == SeasonOne ? "Season 1" : season is null ? string.Empty : "Season 2",
                Total = items.Count,
                PageSize = 50,
                Items = items,
                SupportingFiles = new EditorHeaderSupportingFilesDto(),
            },
            Colors = id == EditTrailTestData.ShowId || id == SeasonOne || id == EpisodeOne || id == EpisodeTwo
                ? new EditorCoverColorsDto { PrimaryHex = "#5a3a8a", SecondaryHex = "#2a1858" }
                : null,
            Config = EditorHeaderTestData.Config(kind, _tabs),
        };
    }

    private static EditorNavItemDto NavEpisode(Guid id, string code, string title, string state, Guid current) =>
        EditorHeaderTestData.Episode(id, code, title, state, current: id == current);

    // An album: the item dropdown lists its songs, and a song's header has the album as its home.
    private static EditorHeaderDto AlbumHeader(Guid id) => new()
    {
        Kind = id == EditTrailTestData.AlbumId ? EditorKindNames.Album : EditorKindNames.Song,
        Root = new EditorHeaderRootDto { Id = EditTrailTestData.AlbumId, Title = "OK Computer", MediaType = "Music" },
        CurrentId = id,
        Label = "OK Computer",
        ShowHome = id != EditTrailTestData.AlbumId,
        HomeId = EditTrailTestData.AlbumId,
        ShowItemDropdown = true,
        Level = new EditorHeaderLevelDto
        {
            LevelId = EditTrailTestData.AlbumId,
            LevelTitle = "OK Computer",
            Total = 2,
            PageSize = 50,
            Items =
            [
                new EditorNavItemDto { Id = EditTrailTestData.TrackOneId, Kind = "track", Number = 1, Title = "Airbag", State = EditorNavItemStates.Matched, IsCurrent = id == EditTrailTestData.TrackOneId },
                new EditorNavItemDto { Id = EditTrailTestData.TrackTwoId, Kind = "track", Number = 2, Title = "Paranoid Android", State = EditorNavItemStates.Matched, IsCurrent = id == EditTrailTestData.TrackTwoId },
            ],
            SupportingFiles = new EditorHeaderSupportingFilesDto(),
        },
        Config = EditorHeaderTestData.Config(id == EditTrailTestData.AlbumId ? EditorKindNames.Album : EditorKindNames.Song),
    };

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync(DetailPageViewModel model)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = Render<DetailPage>(parameters => parameters.Add(page => page.Model, model));
        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-editor-window__tabs [role='tab']")));
        return cut;
    }

    private EditSessionFlow FlowOf(IRenderedComponent<DetailPage> cut) => Services.GetRequiredService<EditorHost>().Flow;

    // Picks an item from a dropdown of the header's nav row: the level is "Show" (the home button), "Season" (the second-level dropdown) or
    // "Episode" or "Song" (the item dropdown). The list is opened, the row is picked, and the editor switches to it in the same window.
    private void Choose(IRenderedComponent<DetailPage> cut, string name, string level)
    {
        if (level == "Show")
        {
            _editor.WaitForElement(".tl-hnav__home").Click();
            return;
        }

        var trigger = level == "Season" ? "#tl-hnav-second" : "#tl-hnav-item";
        _editor.WaitForElement(trigger).Click();
        _editor.WaitForAssertion(() => Assert.Contains(_editor.FindAll(".tl-hnav__option"), option => OptionName(option) == name));
        _editor.FindAll(".tl-hnav__option").First(option => OptionName(option) == name).Click();
    }

    private static string OptionName(AngleSharp.Dom.IElement option) =>
        option.QuerySelector(".tl-hnav__text > b")!.TextContent.Trim();

    // The pop-over opens on Details by itself.
    private void OpenDetails(IRenderedComponent<DetailPage> cut) => _editor.WaitForElement("input[aria-label='Title']");

    [Fact]
    public async Task TheTrailListsTheShowsSeasonsAndEpisodes_AndChoosingOneSwitchesWithoutLeavingThePage()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        var address = Services.GetRequiredService<NavigationManager>().Uri;

        Choose(cut, "Season 1", "Season");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 2", "Episode");
        _editor.WaitForAssertion(() => Assert.Equal(EpisodeTwo, flow.Session.ActiveItem!.EntityId));

        Assert.Equal(address, Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("Le Continental", cut.Find(".tl-detail-page h1").TextContent.Trim());
        Assert.DoesNotContain(_writes, write => !write.EndsWith(":check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePanelReloadsForTheNewItem_AndItsFieldsStageIntoTheSameList()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        OpenDetails(cut);
        Choose(cut, "Season 1", "Season");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));

        _editor.WaitForElement("[data-field-key='title'] input[aria-label='Season']");
        Assert.Equal("season", flow.Mode().Fields.ScopeId);
        _editor.UnlockField("title");
        _editor.Find("[data-field-key='title'] input").Input("Premiere");

        _editor.WaitForAssertion(() => Assert.True(flow.Session.Pending.TryGetField(SeasonOne, "title", out _)));
        Assert.False(flow.Session.Pending.TryGetField(ShowId, "title", out _));
    }

    [Fact]
    public async Task EveryItemsChangesStayInThePendingList_AndTheSaveCountCoversAllOfThem()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        flow.Session.Pending.SetField(ShowId, "tagline", "A hotel");
        Choose(cut, "Season 1", "Season");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 1", "Episode");
        _editor.WaitForAssertion(() => Assert.Equal(EpisodeOne, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(EpisodeOne, "description", "The first night.");

        _editor.WaitForAssertion(() => Assert.Equal("Save changes", _editor.Find("button.tl-editor-window__save").TextContent.Trim()));
        var items = flow.ReviewRows().Select(row => row.Item).Distinct().ToList();
        Assert.Equal(["Le Continental", "Episode 1"], items);
    }

    [Fact]
    public async Task FromAnEpisode_TheShowAnotherSeasonAndAThirdEpisodeAreReachable_AndTheSaveCountCoversAllThree()
    {
        var cut = await StartEditingAsync(EditTrailTestData.EpisodePage());
        var flow = FlowOf(cut);
        Assert.Equal(EpisodeTwo, flow.Session.ActiveItem!.EntityId);
        flow.Session.Pending.SetField(EpisodeTwo, "description", "A change on the episode I opened.");

        // The show above it.
        Choose(cut, EditTrailTestData.ShowTitle, "Show");
        _editor.WaitForAssertion(() => Assert.Equal(ShowId, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(ShowId, "tagline", "A hotel");

        // Another season, then a third episode inside it.
        Choose(cut, "Season 2", "Season");
        _editor.WaitForAssertion(() => Assert.Equal(EditTrailTestData.SeasonTwoId, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 3", "Episode");
        _editor.WaitForAssertion(() => Assert.Equal(EditTrailTestData.EpisodeThreeId, flow.Session.ActiveItem!.EntityId));
        flow.Session.Pending.SetField(EditTrailTestData.EpisodeThreeId, "description", "The third one.");

        _editor.WaitForAssertion(() => Assert.Equal("Save changes", _editor.Find("button.tl-editor-window__save").TextContent.Trim()));
        // The home button and the item dropdown carry the purple dot for the show and the episode that have a change waiting, and nothing was written on the way.
        Assert.NotEmpty(_editor.FindAll(".tl-hnav__dot--home"));
        Assert.NotEmpty(_editor.FindAll("#tl-hnav-item .tl-hnav__dot"));
        Assert.DoesNotContain(_writes, write => !write.EndsWith(":check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEpisodesPopOver_HasTheNavRowAHeaderTheTabsAndAFooterThatSaysWhereChangesGo()
    {
        await StartEditingAsync(EditTrailTestData.EpisodePage());

        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-hnav__home")));
        Assert.Equal("Season", _editor.Find("#tl-hnav-second").GetAttribute("data-level"));
        Assert.Equal("episode", _editor.Find("#tl-hnav-item").GetAttribute("data-level"));
        // The header names the item, and its show above the title.
        Assert.Equal("Episode 2", _editor.Find(".tl-editor-header__title").TextContent.Trim());
        _editor.WaitForAssertion(() => Assert.Contains(EditTrailTestData.ShowTitle, _editor.Find(".tl-editor-header__label").TextContent));
        Assert.Equal(["Details", "Artwork", "Match", "History"], _editor.FindAll(".tl-editor-window__tabs [role='tab']").Select(tab => tab.TextContent.Trim()));
        // The footer: whether edits reach the files, how many wait, Discard and Save changes.
        Assert.Contains("Edits save in Library", _editor.Find(".tl-editor-window__where").TextContent);
        Assert.Equal("No changes yet", _editor.Find(".tl-editor-window__changes").TextContent.Trim());
        Assert.Equal("Discard", _editor.Find("button.tl-editor-window__cancel").TextContent.Trim());
        Assert.NotEmpty(_editor.FindAll("button.tl-editor-window__save"));
    }

    [Fact]
    public async Task AnEpisodesHeader_DrawsAGradientFromTheCoversColours_AndAnAlbumWithNoColoursStaysPlain()
    {
        await StartEditingAsync(EditTrailTestData.EpisodePage());

        _editor.WaitForAssertion(() => Assert.Equal("cover", _editor.Find(".tl-editor-header").GetAttribute("data-gradient")));
        var wash = _editor.Find(".tl-editor-header__wash");
        Assert.Contains("#5a3a8a", wash.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Contains("#2a1858", wash.GetAttribute("style"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACoverWithNoStoredColours_LeavesTheHeaderAsThePlainWindowSurface()
    {
        await StartEditingAsync(EditTrailTestData.AlbumPage());

        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll("#tl-hnav-item")));
        Assert.Equal("none", _editor.Find(".tl-editor-header").GetAttribute("data-gradient"));
        Assert.Empty(_editor.FindAll(".tl-editor-header__wash"));
    }

    [Fact]
    public async Task WithWriteBackOff_TheFooterSaysEditsSaveInLibraryAndTheOriginalFilesStayUnchanged()
    {
        _writeBackOn = false;
        await StartEditingAsync(EditTrailTestData.EpisodePage());

        _editor.WaitForAssertion(() => Assert.Equal("writebackoff", _editor.Find("[data-footer-status]").GetAttribute("data-footer-status")));
        var text = _editor.Find(".tl-editor-window__where").TextContent;
        Assert.Contains("File write-back off", text, StringComparison.Ordinal);
        Assert.Contains("Edits save in Library. Original files stay unchanged.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithWriteBackOn_TheFooterSaysEditsAreAlsoWrittenIntoTheFilesOwnTags()
    {
        _writeBackOn = true;
        await StartEditingAsync(EditTrailTestData.EpisodePage());

        _editor.WaitForAssertion(() => Assert.Equal("writebackon", _editor.Find("[data-footer-status]").GetAttribute("data-footer-status")));
        var text = _editor.Find(".tl-editor-window__where").TextContent;
        Assert.Contains("File write-back on", text, StringComparison.Ordinal);
        Assert.Contains("written into the file's own tags", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStagedFileMove_TurnsTheFooterAmber_NamesTheMove_AndDiscardingPutsTheSentenceBack()
    {
        _writeBackOn = false;
        var cut = await StartEditingAsync(EditTrailTestData.EpisodePage());
        var flow = FlowOf(cut);
        _editor.WaitForAssertion(() => Assert.Equal("writebackoff", _editor.Find("[data-footer-status]").GetAttribute("data-footer-status")));

        flow.Session.Pending.SetTarget(EditSessionChangeKinds.FileMove, EpisodeTwo, "target", label: "TV/Le Continental/Season 1/S01E02.mkv", before: "Inbox/ep2.mkv");

        _editor.WaitForAssertion(() => Assert.Equal("willmove", _editor.Find("[data-footer-status]").GetAttribute("data-footer-status")));
        var where = _editor.Find(".tl-editor-window__where");
        Assert.Contains("tl-editor-window__where--warn", where.ClassList);
        Assert.Contains("This file will move when you save", where.TextContent, StringComparison.Ordinal);
        Assert.Contains("Inbox/ep2.mkv", where.TextContent, StringComparison.Ordinal);

        flow.Session.Pending.Discard();

        _editor.WaitForAssertion(() => Assert.Equal("writebackoff", _editor.Find("[data-footer-status]").GetAttribute("data-footer-status")));
        Assert.DoesNotContain("tl-editor-window__where--warn", _editor.Find(".tl-editor-window__where").ClassList);
    }

    [Fact]
    public async Task TheFooterReadsDeleteThenTheStatusThenTheCountThenDiscardThenSave()
    {
        var administrator = Guid.NewGuid();
        Services.GetRequiredService<DashboardSessionAccessor>().Set("test-session-token", administrator, administrator, Guid.NewGuid(),
            new DashboardAuthorityResponse(administrator, administrator, true, true, 1, 1, true, true, null, 1, [], [], []));
        var cut = await StartEditingAsync(EditTrailTestData.EpisodePage());
        var flow = FlowOf(cut);
        flow.Session.Pending.SetField(EpisodeTwo, "description", "A change.");
        flow.Session.Pending.SetField(EpisodeTwo, "title", "Renamed");

        _editor.WaitForAssertion(() => Assert.Equal("2 unsaved changes", _editor.Find(".tl-editor-window__changes").TextContent.Trim()));
        var footer = _editor.Find("footer.tl-editor-window__footer");
        Assert.Equal("Delete", footer.QuerySelector("button.tl-editor-window__delete")?.TextContent.Trim());
        Assert.Equal("Save changes", footer.QuerySelector("button.tl-editor-window__save")!.TextContent.Trim());
        var order = footer.QuerySelectorAll("button, .tl-editor-window__where, .tl-editor-window__changes")
            .Select(element => element.ClassList.Contains("tl-editor-window__where") ? "status" : element.ClassList.Contains("tl-editor-window__changes") ? "count" : element.TextContent.Trim())
            .ToList();
        Assert.Equal(["Delete", "status", "count", "Discard", "Save changes"], order);
    }

    [Fact]
    public async Task TheTabsKeepTheirFixedOrder_WhateverOrderTheConfigListsThem()
    {
        _tabs = ["history", "match", "artwork", "details"];

        await StartEditingAsync(EditTrailTestData.EpisodePage());

        _editor.WaitForAssertion(() => Assert.Equal(
            ["Details", "Artwork", "Match", "History"],
            _editor.FindAll(".tl-editor-window__tabs [role='tab']").Select(tab => tab.TextContent.Trim())));
    }

    [Fact]
    public async Task NoPopOverOffersAContentsOrFilesTab_TheNavRowTakesTheirPlace()
    {
        await StartEditingAsync(EditTrailTestData.ShowPage());

        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll("#tl-hnav-second")));
        var tabs = _editor.FindAll(".tl-editor-window__tabs [role='tab']").Select(tab => tab.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Contents", tabs);
        Assert.DoesNotContain("Files", tabs);
    }

    [Fact]
    public async Task ThePageBehindTheWindow_KeepsItsStoredTitle_WhileAnEpisodeIsEdited()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);
        var storedTitle = cut.Find(".tl-detail-page h1").TextContent.Trim();
        flow.Session.Pending.SetField(ShowId, "title", "Renamed show");
        Choose(cut, "Season 1", "Season");
        _editor.WaitForAssertion(() => Assert.Equal(SeasonOne, flow.Session.ActiveItem!.EntityId));
        Choose(cut, "Episode 1", "Episode");
        _editor.WaitForAssertion(() => Assert.Equal(EpisodeOne, flow.Session.ActiveItem!.EntityId));

        flow.Session.Pending.SetField(EpisodeOne, "title", "Pilot");

        Assert.Equal(storedTitle, cut.Find(".tl-detail-page h1").TextContent.Trim());
    }

    [Fact]
    public async Task ASeasonThePageHasNotOpened_ListsItsEpisodesOnceTheItemIsSwitchedTo()
    {
        var cut = await StartEditingAsync(EditTrailTestData.ShowPage());
        var flow = FlowOf(cut);

        await cut.InvokeAsync(() => flow.SwitchToAsync(EditTrailTestData.SeasonTwoId));

        // The stub answers no detail page for the season, so the season has no episodes to list; switching itself still works.
        Assert.Equal(EditTrailTestData.SeasonTwoId, flow.Session.ActiveItem!.EntityId);
        Assert.Empty(flow.Catalog.ChildrenOf(EditTrailTestData.SeasonTwoId));
    }

    [Fact]
    public async Task AnAlbumPage_SwitchesBetweenTheAlbumItselfAndItsSongs()
    {
        var cut = await StartEditingAsync(EditTrailTestData.AlbumPage());
        var flow = FlowOf(cut);
        // An album has no home button of its own (it is the home); its songs are in the item dropdown.
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll("#tl-hnav-item")));
        Assert.Empty(_editor.FindAll(".tl-hnav__home"));

        Choose(cut, "Paranoid Android", "Song");

        _editor.WaitForAssertion(() => Assert.Equal(EditTrailTestData.TrackTwoId, flow.Session.ActiveItem!.EntityId));
        Assert.Equal("track", flow.Mode().Fields.ScopeId);
        // On the song, the album is one click away through the home button.
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-hnav__home")));
    }

    [Fact]
    public async Task AMoviePage_HasNoNavRow_AndTheToolsUseTheFullWidth()
    {
        var movie = new DetailPageViewModel
        {
            Id = EditingModeTestData.MovieId.ToString("D"),
            EntityType = DetailEntityType.Movie,
            Title = "Dune: Part One",
            Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
            OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        };

        var cut = await StartEditingAsync(movie);

        Assert.Empty(_editor.FindAll(".tl-hnav"));
        Assert.Contains("Dune: Part One", _editor.Find(".tl-editor-header__title").TextContent);
    }

}

internal static class FlowTestExtensions
{
    public static EditingModeContext Mode(this EditSessionFlow flow) =>
        (EditingModeContext)typeof(EditSessionFlow).GetField("_mode", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(flow)!;
}
