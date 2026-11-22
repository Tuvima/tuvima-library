using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A Series is editable only for what is the owner's own, and a Universe takes overrides only (universal editor v5 D7, screen H, acceptance A10,
/// amended by v8 D5 and D6): Edit on a Series shows its name, description and ordered members locked with a notice that says why, offers its
/// pictures, tags and sort title, and has Details, Artwork and History; its header carries the match badge and two dropdowns (kind of title,
/// then title); a member edits as any item does; a Universe's provider facts have no unlock while its description override does. The Engine is
/// a hand-written stub.
/// </summary>
public sealed class SeriesEditorTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("5e000000-0000-0000-0000-000000000001");
    private static readonly Guid BookOne = Guid.Parse("5e000000-0000-0000-0000-0000000000b1");
    private static readonly Guid BookTwo = Guid.Parse("5e000000-0000-0000-0000-0000000000b2");
    private static readonly Guid SubjectId = Guid.Parse("5e000000-0000-0000-0000-0000000000a1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private EditorSubjectDto _subject = Grouping(EditorSubjectKinds.Series);
    private readonly List<(string Type, string? Query)> _titleReads = [];
    private Func<string, EditorSeriesTitlesDto> _titles = type => new EditorSeriesTitlesDto { MediaType = type };
    private string _mediaType = "Books";
    private bool _explicitSupported;

    public SeriesEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorSubjectAsync), _ => Task.FromResult<EditorSubjectDto?>(_subject));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetItemExplicitAsync), args => Task.FromResult<MediaEngine.Contracts.Items.ItemExplicitResponse?>(
                new MediaEngine.Contracts.Items.ItemExplicitResponse { EntityId = (Guid)args![0]!, Supported = _explicitSupported }));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorKindConfigAsync), args => Task.FromResult<EditorHeaderConfigDto?>(
                EditorHeaderTestData.Config((string)args![0]!, "details", "artwork", "history")));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorSeriesTitlesAsync), args =>
            {
                _titleReads.Add(((string)args![1]!, (string?)args[2]));
                return Task.FromResult<EditorSeriesTitlesDto?>(_titles((string)args[1]!));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), _ => Task.FromResult<EditorHeaderLevelDto?>(SeriesHeader().Level));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args => Task.FromResult<MediaEditorContextDto?>(
                (Guid)args![0]! == SeriesId ? BooksContext(SeriesId, "Dune Novels", "series", SeriesHeader()) : BooksContext((Guid)args[0]!, "Dune", "book", BookHeader((Guid)args[0]!))));
        }));
        Render<AppPopoverHost>();
    }

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow, ItemFieldEditSession Fields);

    private async Task<Editor> StartSubjectAsync(EditorSubjectDto subject)
    {
        _subject = subject;
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadSubjectAsync(SubjectId);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(SubjectId, subject.Title, subject.Kind)], () => Task.FromResult(false));
        Assert.True(await mode.EnterAsync());
        return new Editor(mode, session, flow, fields);
    }

    private async Task<Editor> StartSeriesOfBooksAsync(string mediaType = "Books")
    {
        _mediaType = mediaType;
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(SeriesId, mediaType, "series", null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(api), () => [new EditTrailItem(SeriesId, "Dune Novels", "Series")], () => Task.FromResult(false))
        {
            OpenItem = async item =>
            {
                await fields.LoadAsync(item.EntityId, "Books", "book", null);
                return fields.Loaded && fields.EntityId == item.EntityId;
            },
        };
        Assert.True(await mode.EnterAsync());
        return new Editor(mode, session, flow, fields);
    }

    // The window as the pop-over draws it: the header's nav row, the tabs, the Details tool and the footer.
    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderWindow(Editor editor, bool withHeader = false, bool phone = false)
    {
        editor.Flow.SelectToolAsync(EditTool.Details).GetAwaiter().GetResult();
        return Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, editor.Mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), editor.Mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), editor.Flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.ShowHistory), true);
                if (withHeader)
                {
                    builder.AddComponentParameter(4, nameof(EditorPanelHost.ItemHeader), (RenderFragment)(header =>
                    {
                        header.OpenComponent<EditorItemHeader>(0);
                        header.AddComponentParameter(1, nameof(EditorItemHeader.Flow), editor.Flow);
                        header.AddComponentParameter(2, nameof(EditorItemHeader.Fields), editor.Fields);
                        header.AddComponentParameter(3, nameof(EditorItemHeader.Family), new EditorFamilyState(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance));
                        header.AddComponentParameter(4, nameof(EditorItemHeader.Phone), phone);
                        header.CloseComponent();
                    }));
                }

                builder.AddComponentParameter(5, nameof(EditorPanelHost.ChildContent), (RenderFragment<EditTool>)(tool => inner =>
                {
                    if (tool == EditTool.Details)
                    {
                        inner.OpenComponent<EditorDetailsPanel>(0);
                        inner.AddComponentParameter(1, nameof(EditorDetailsPanel.Mode), editor.Mode);
                        inner.AddComponentParameter(2, nameof(EditorDetailsPanel.Session), editor.Session);
                        inner.AddComponentParameter(3, nameof(EditorDetailsPanel.Flow), editor.Flow);
                        inner.CloseComponent();
                    }
                }));
                builder.CloseComponent();
            }));
    }

    private static IEnumerable<string> Tabs(IRenderedComponent<CascadingValue<EditingModeContext>> window) =>
        window.FindAll(".tl-editor-window__tabs [role='tab']").Select(tab => tab.TextContent.Trim());

    // -- A Series of the Library ----------------------------------------------------------------

    [Fact]
    public async Task EditOnASeries_ShowsItsNameAndDescriptionLocked_WithNoWayToUnlockThem_AndTheNoticeWhy()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));
        var window = RenderWindow(editor);

        window.WaitForElement(".tl-editor-details");

        Assert.Single(window.FindAll("[data-series-note]"));
        Assert.Contains("stay locked", window.Find("[data-series-note]").TextContent, StringComparison.Ordinal);
        Assert.Single(window.FindAll(".tl-editor-field--locked[data-field-key='title']"));
        Assert.Single(window.FindAll(".tl-editor-field--locked[data-field-key='description']"));
        Assert.Empty(window.FindAll(".tl-editor-field[data-field-key='title'] input"));
        Assert.Empty(window.FindAll(".tl-editor-field[data-field-key='description'] textarea"));
        Assert.Empty(window.FindAll("[data-lock-state]"));
        Assert.Empty(window.FindAll("button.tl-editor-field__reset"));
    }

    [Fact]
    public async Task EditOnASeries_OffersItsSortTitleAndTags_AsTheOwnersOwnFields_WithNoLock()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));
        var window = RenderWindow(editor);

        window.WaitForElement(".tl-editor-details");

        Assert.Single(window.FindAll(".tl-editor-field[data-field-key='sort_title'] input"));
        Assert.Single(window.FindAll(".tl-editor-field[data-field-key='custom_tags']"));
        Assert.Equal("false", window.Find(".tl-editor-field[data-field-key='sort_title']").GetAttribute("data-locked"));
        Assert.Equal("false", window.Find(".tl-editor-field[data-field-key='custom_tags']").GetAttribute("data-locked"));
    }

    [Fact]
    public async Task EditOnASeries_OffersNoSortName_BecauseTheEngineWritesOnlySortTitleAndTags()
    {
        var subject = Grouping(EditorSubjectKinds.Series);
        subject.Fields.Add(new EditorSubjectFieldDto { Key = "sort_name", Label = "Sort name" });
        var editor = await StartSubjectAsync(subject);
        var window = RenderWindow(editor);

        window.WaitForElement(".tl-editor-details");

        Assert.Empty(window.FindAll(".tl-editor-field[data-field-key='sort_name'] input"));
        Assert.Single(window.FindAll(".tl-editor-field[data-field-key='sort_title'] input"));
        Assert.Single(window.FindAll(".tl-editor-field[data-field-key='custom_tags']"));
    }

    [Fact]
    public async Task EditOnASeriesOfVideos_OffersNoExplicitMark_BecauseTheEngineRefusesItOnASeries()
    {
        _explicitSupported = true;
        var editor = await StartSeriesOfBooksAsync(mediaType: "Videos");
        var window = RenderWindow(editor, withHeader: true);

        window.WaitForElement("[data-series-members]");

        Assert.True(editor.Fields.IsSeries);
        Assert.Empty(window.FindAll("[data-field-key='explicit']"));
    }

    [Fact]
    public async Task TypingASeriesSortTitle_StagesIt_ForSaveChangesToReview_AndSavesNothing()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));
        var window = RenderWindow(editor);
        window.WaitForElement(".tl-editor-details");

        window.Find(".tl-editor-field[data-field-key='sort_title'] input").Input("Dune Novels, The");

        window.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(SubjectId, "sort_title", out var staged));
            Assert.Equal("Dune Novels, The", staged);
            Assert.False(editor.Session.Pending.TryGetField(SubjectId, "title", out _));
        });
    }

    [Fact]
    public async Task EditOnASeries_OffersDetailsArtworkAndHistory_NeverMatch()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));

        var window = RenderWindow(editor);

        Assert.Equal(["Details", "Artwork", "History"], Tabs(window));
        Assert.True(editor.Fields.CanEditArtwork);
        Assert.Equal(["CoverArt", "Background", "Logo"], editor.Fields.ArtworkSlots);
    }

    [Fact]
    public async Task EditOnASeries_KeepsSaveChangesAndDiscard_BecauseItsOwnFieldsAndPicturesCanBeSaved()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series));

        var window = RenderWindow(editor);

        Assert.NotEmpty(window.FindAll("button.tl-editor-window__save"));
        Assert.Empty(window.FindAll("footer[data-read-only]"));
    }

    // -- The Series' header: the badge and the two dropdowns (v8 D1 and D6) ---------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ASeriesHeader_ShowsTheBadgeFromItsWikidataLinkAndMembers(bool retail, bool canonical)
    {
        var series = Grouping(EditorSubjectKinds.Series, new EditorSubjectMatchBadgeDto { RetailMatched = retail, ProviderName = "TMDB", CanonicalMatched = canonical });
        var editor = await StartSubjectAsync(series);

        var window = RenderWindow(editor, withHeader: true);
        window.WaitForElement("[data-testid='header-match-badge']");

        Assert.Equal(retail ? "true" : "false", window.Find("[data-testid='header-badge-retail']").GetAttribute("data-matched"));
        Assert.Equal(canonical ? "true" : "false", window.Find("[data-testid='header-badge-canonical']").GetAttribute("data-matched"));
    }

    [Fact]
    public async Task ASeriesHeader_ListsOnlyTheKindsTheSeriesHas_InTheFirstDropdown()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series, kinds: [("Books", "Books", 3), ("Movies", "Movies", 2)]));
        var window = RenderWindow(editor, withHeader: true);

        window.WaitForElement("#tl-snav-kind").Click();

        Assert.Equal(["Books", "Movies"], window.FindAll("[data-kind-key]").Select(option => option.GetAttribute("data-kind-key")));
        Assert.Contains("Books", window.Find("#tl-snav-kind").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeriesHeader_ListsEveryTitleOfTheChosenKind_WithATitleNotOwnedShownButNotOpenable()
    {
        _titles = type => new EditorSeriesTitlesDto
        {
            MediaType = type,
            Total = 3,
            ShowingMissing = true,
            Items =
            [
                new EditorSeriesTitleDto { Id = BookOne, Title = "Dune", Number = "1" },
                new EditorSeriesTitleDto { Id = BookTwo, Title = "Dune Messiah", Number = "2" },
                new EditorSeriesTitleDto { Title = "Children of Dune", Number = "3", Owned = false },
            ],
        };
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series, kinds: [("Books", "Books", 2)]));
        var window = RenderWindow(editor, withHeader: true);

        window.WaitForElement("#tl-snav-title").Click();
        window.WaitForElement("[data-testid='series-nav-titles'] li");

        Assert.Equal(("Books", (string?)string.Empty), Assert.Single(_titleReads));
        Assert.Equal(
            ["1 · Dune", "2 · Dune Messiah", "3 · Children of Dune Not in your library"],
            window.FindAll("[data-testid='series-nav-titles'] li").Select(line => System.Text.RegularExpressions.Regex.Replace(line.TextContent, @"\s+", " ").Trim()).Where(text => text.Length > 0));
        var missing = Assert.Single(window.FindAll("[data-missing='true']"));
        Assert.Equal("true", missing.GetAttribute("aria-disabled"));
        Assert.Empty(missing.QuerySelectorAll("button"));
    }

    [Fact]
    public async Task ASeriesHeader_WithoutShowMissing_ListsOnlyOwnedTitles_AsTheEngineSaysThem()
    {
        _titles = type => new EditorSeriesTitlesDto
        {
            MediaType = type,
            Total = 1,
            Items = [new EditorSeriesTitleDto { Id = BookOne, Title = "Dune", Number = "1" }],
        };
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series, kinds: [("Books", "Books", 1)]));
        var window = RenderWindow(editor, withHeader: true);

        window.WaitForElement("#tl-snav-title").Click();
        window.WaitForElement("[data-testid='series-nav-titles'] li");

        Assert.Empty(window.FindAll("[data-missing='true']"));
        Assert.Single(window.FindAll("[data-title-id]"));
    }

    [Fact]
    public async Task PickingATitleInTheSeriesHeader_OpensThatTitlesEditor()
    {
        _titles = type => new EditorSeriesTitlesDto
        {
            MediaType = type,
            Total = 1,
            Items = [new EditorSeriesTitleDto { Id = BookTwo, Title = "Dune Messiah", Number = "2" }],
        };
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series, kinds: [("Books", "Books", 1)]));
        var opened = new List<Guid>();
        editor.Flow.OpenItem = item =>
        {
            opened.Add(item.EntityId);
            return Task.FromResult(true);
        };
        var window = RenderWindow(editor, withHeader: true);
        window.WaitForElement("#tl-snav-title").Click();

        window.WaitForElement($"[data-title-id='{BookTwo:N}']").Click();

        window.WaitForAssertion(() => Assert.Equal([BookTwo], opened));
    }

    [Fact]
    public async Task AChosenKind_ReadsItsOwnTitles_AndASearchAsksTheEngineForThatText()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series, kinds: [("Books", "Books", 1), ("Movies", "Movies", 1)]));
        var window = RenderWindow(editor, withHeader: true);
        window.WaitForElement("#tl-snav-kind").Click();

        window.Find("[data-kind-key='Movies']").Click();

        window.WaitForAssertion(() => Assert.Contains(("Movies", (string?)string.Empty), _titleReads));
    }

    [Fact]
    public async Task ASeriesHeader_OnAPhone_PutsTheTwoDropdownsInTheNavigationSheet()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Series, kinds: [("Books", "Books", 1)]));
        var window = RenderWindow(editor, withHeader: true, phone: true);

        window.WaitForElement("[data-testid='phone-series-tap']").Click();

        window.WaitForAssertion(() =>
        {
            Assert.NotNull(window.Find("[data-testid='phone-series-sheet']"));
            Assert.Single(window.FindAll("#tl-snav-kind"));
            Assert.Single(window.FindAll("#tl-snav-title"));
        });
    }

    [Theory]
    [InlineData("Books", "EditorSeries_ReadingOrder")]
    [InlineData("Comics", "EditorSeries_ReadingOrder")]
    [InlineData("Movies", "EditorSeries_WatchOrder")]
    [InlineData("TV", "EditorSeries_WatchOrder")]
    [InlineData("Audiobooks", "EditorSeries_ListeningOrder")]
    [InlineData("Music", "EditorSeries_ListeningOrder")]
    public void TheOrderHeading_IsNamedForHowTheMembersAreTakenIn(string kind, string key) =>
        Assert.Equal(key, EditorSeriesRules.OrderHeadingKey([kind]));

    [Fact]
    public void TheOrderHeading_ForASeriesThatMixesKinds_IsJustInOrder()
    {
        Assert.Equal("EditorSeries_MixedOrder", EditorSeriesRules.OrderHeadingKey(["Books", "Movies"]));
        Assert.Equal("EditorSeries_ReadingOrder", EditorSeriesRules.OrderHeadingKey(["Books", "Comics"]));
    }

    [Theory]
    [InlineData("EditorSeries_ReadingOrder", "Reading order")]
    [InlineData("EditorSeries_WatchOrder", "Watch order")]
    [InlineData("EditorSeries_ListeningOrder", "Listening order")]
    [InlineData("EditorSeries_MixedOrder", "In order")]
    public void TheOrderWords_AreTheOnesTheProductOwnerChose(string key, string words)
    {
        var localizer = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<MediaEngine.Web.Resources.SharedStrings>>();

        Assert.Equal(words, localizer[key].Value);
    }

    // -- A Series of books, reached from the header -----------------------------------------------

    [Fact]
    public async Task EditOnASeriesOfBooks_ListsItsBooksInReadingOrder_Locked()
    {
        var editor = await StartSeriesOfBooksAsync();
        var window = RenderWindow(editor, withHeader: true);

        window.WaitForElement("[data-series-members]");

        Assert.True(editor.Fields.IsSeries);
        Assert.Equal(["Dune", "Dune Messiah"], window.FindAll("[data-series-members] .tl-order__title").Select(line => line.TextContent.Trim()));
        Assert.Equal(["1", "2"], window.FindAll("[data-series-members] .tl-order__num").Select(line => line.TextContent.Trim()));
        Assert.Contains("Reading order", window.Find("[data-series-members]").GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Equal(["Details", "History"], Tabs(window));
    }

    [Fact]
    public async Task PickingABookInTheSeriesDropdown_OpensTheBooksEditor_WhichIsEditable()
    {
        var editor = await StartSeriesOfBooksAsync();
        var window = RenderWindow(editor, withHeader: true);
        window.WaitForElement("#tl-hnav-item").Click();

        window.Find($"#tl-hnav-option-{BookOne:N}").Click();

        window.WaitForAssertion(() =>
        {
            Assert.Equal(BookOne, editor.Fields.EntityId);
            Assert.False(editor.Fields.IsSeries);
            Assert.True(editor.Fields.CanEditFields);
            Assert.Empty(window.FindAll("[data-series-note]"));
            Assert.NotEmpty(window.FindAll("button.tl-editor-window__save"));
        });
    }

    // -- A Universe: mostly read-only -------------------------------------------------------------

    [Fact]
    public async Task AUniversesNameAndDescription_StartLocked_AndEachUnlocksAsAnOverride()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Universe));
        var window = RenderWindow(editor);
        window.WaitForElement(".tl-editor-details");

        Assert.Equal("locked", window.LockStateOf("title"));
        Assert.Equal("locked", window.LockStateOf("description"));
        Assert.Empty(window.FindAll("[data-series-note]"));

        window.UnlockField("title");
        window.Find(".tl-editor-field[data-field-key='title'] input").Input("Arrakis");

        window.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(SubjectId, "title", out var staged));
            Assert.Equal("Arrakis", staged);
        });
    }

    [Fact]
    public async Task ALibrarySeries_ListsItsMembersInOrder_AndPickingOneOpensThatMembersEditor()
    {
        var series = Grouping(EditorSubjectKinds.Series);
        series.Members.Add(new EditorSubjectMemberDto { Id = BookOne, Title = "Dune", Number = 1 });
        series.Members.Add(new EditorSubjectMemberDto { Id = BookTwo, Title = "Dune Messiah", Number = 2 });
        var editor = await StartSubjectAsync(series);
        var opened = new List<Guid>();
        editor.Flow.OpenItem = item =>
        {
            opened.Add(item.EntityId);
            return Task.FromResult(true);
        };
        var window = RenderWindow(editor);
        window.WaitForElement("[data-series-members]");

        Assert.Equal(["Dune", "Dune Messiah"], window.FindAll("[data-series-members] .tl-order__title").Select(line => line.TextContent.Trim()));
        Assert.Equal(["1", "2"], window.FindAll("[data-series-members] .tl-order__num").Select(line => line.TextContent.Trim()));
        window.FindAll("[data-series-members] li button")[1].Click();

        window.WaitForAssertion(() => Assert.Equal([BookTwo], opened));
    }

    // -- The order block: the rules, then the result (group pages and tiles D5, mock screen C) -----

    private static EditorSubjectMemberDto Member(string title, int? number, string? placedBy, string? year = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Number = number,
        MediaType = "Books",
        Year = year,
        PlacedBy = placedBy,
    };

    private async Task<IRenderedComponent<CascadingValue<EditingModeContext>>> RenderOrderAsync(params EditorSubjectMemberDto[] members)
    {
        var series = Grouping(EditorSubjectKinds.Series);
        series.Members.AddRange(members);
        var editor = await StartSubjectAsync(series);
        var window = RenderWindow(editor);
        window.WaitForElement("[data-series-members]");
        return window;
    }

    [Fact]
    public async Task TheOrderBlock_ShowsTheRulesInTheOrderTheyAreTried_EachWithHowManyTitlesItPlaced()
    {
        var window = await RenderOrderAsync(
            Member("Leviathan Wakes", 1, EditorSeriesOrderRuleKeys.Number, "2011"),
            Member("Caliban's War", 2, EditorSeriesOrderRuleKeys.Number, "2012"),
            Member("The Churn", null, EditorSeriesOrderRuleKeys.Date, "2014"),
            Member("Notes", null, EditorSeriesOrderRuleKeys.Title));

        var rules = window.FindAll("[data-order-rules] li");

        Assert.Equal(["Series number", "Publication date", "Title"], rules.Select(rule => rule.QuerySelector(".tl-order__rule-name")!.TextContent.Trim()));
        Assert.Equal(["2 of 4 placed", "1 of 4 placed", "1 of 4 placed"], rules.Select(rule => rule.QuerySelector(".tl-order__rule-count")!.TextContent.Trim()));
        Assert.Equal(["1", "2", "3"], rules.Select(rule => rule.QuerySelector(".tl-order__rule-step")!.TextContent.Trim()));
        Assert.Equal(["Leviathan Wakes", "Caliban's War", "The Churn", "Notes"], window.FindAll(".tl-order__title").Select(title => title.TextContent.Trim()));
        Assert.Equal("Book · 2011", window.FindAll(".tl-order__detail")[0].TextContent.Trim());
        Assert.Empty(window.FindAll("[data-order-unknown]"));
    }

    [Fact]
    public async Task ATitlePlacedByALaterRule_ShowsAnAmberChip_AndTheOthersShowTheFirstRulesNeutrally()
    {
        var window = await RenderOrderAsync(
            Member("Abaddon's Gate", 3, EditorSeriesOrderRuleKeys.Number, "2013"),
            Member("The Churn", null, EditorSeriesOrderRuleKeys.Date, "2014"));

        var chips = window.FindAll(".tl-order__chip");

        Assert.Equal(["Series number", "Publication date"], chips.Select(chip => chip.TextContent.Trim()));
        Assert.DoesNotContain("tl-order__chip--later", chips[0].ClassList);
        Assert.Contains("tl-order__chip--later", chips[1].ClassList);
        Assert.Equal("true", chips[1].GetAttribute("data-later"));
    }

    [Fact]
    public async Task WhenOneRulePlacedEveryTitle_TheOrderBlockShowsOnePill_AndEveryChipNamesIt()
    {
        var window = await RenderOrderAsync(
            Member("Dune", 1, EditorSeriesOrderRuleKeys.Number),
            Member("Dune Messiah", 2, EditorSeriesOrderRuleKeys.Number));

        Assert.Single(window.FindAll("[data-order-rules] li"));
        Assert.Contains("2 of 2 placed", window.Find("[data-order-rules] li").TextContent, StringComparison.Ordinal);
        Assert.Equal(["Series number", "Series number"], window.FindAll(".tl-order__chip").Select(chip => chip.TextContent.Trim()));
        Assert.Empty(window.FindAll(".tl-order__chip--later"));
    }

    [Fact]
    public async Task WhenNoOrderIsKnown_TheRulesAreaSaysSo_AndTheTitlesAreListedWithoutChips()
    {
        var window = await RenderOrderAsync(
            Member("Alpha", null, EditorSeriesOrderRuleKeys.Title),
            Member("Beta", null, EditorSeriesOrderRuleKeys.Title));

        Assert.Equal("No order known. Titles are listed A to Z.", window.Find("[data-order-unknown]").TextContent.Trim());
        Assert.Empty(window.FindAll("[data-order-rules]"));
        Assert.Empty(window.FindAll(".tl-order__chip"));
        Assert.Equal(["Alpha", "Beta"], window.FindAll(".tl-order__title").Select(title => title.TextContent.Trim()));
    }

    [Fact]
    public async Task WhenTheEngineNamesNoRule_TheOrderBlockShowsOnlyTheResultList()
    {
        var window = await RenderOrderAsync(Member("Dune", 1, null), Member("Dune Messiah", 2, null));

        Assert.Empty(window.FindAll("[data-order-rules]"));
        Assert.Empty(window.FindAll("[data-order-unknown]"));
        Assert.Empty(window.FindAll(".tl-order__chip"));
        Assert.Equal(2, window.FindAll(".tl-order__row").Count);
    }

    [Fact]
    public void OnAPhone_TheOrderBlockMarksItselfSoTheRuleChipDropsUnderTheTitle()
    {
        var members = new[] { new EditorSeriesMember(BookOne, "Dune", "1 · Dune", "1", "Books", "1965", EditorSeriesOrderRuleKeys.Number) };

        var block = Render<EditorSeriesOrderBlock>(parameters => parameters
            .Add(component => component.Members, members)
            .Add(component => component.Heading, "Reading order")
            .Add(component => component.Phone, true));

        Assert.Equal("true", block.Find("[data-series-members]").GetAttribute("data-phone"));
        Assert.Equal("Series number", block.Find(".tl-order__chip").TextContent.Trim());
    }

    [Fact]
    public async Task AUniversesFooter_KeepsSaveChanges_BecauseItsOverridesCanBeSaved()
    {
        var editor = await StartSubjectAsync(Grouping(EditorSubjectKinds.Universe));

        var window = RenderWindow(editor);

        Assert.NotEmpty(window.FindAll("button.tl-editor-window__save"));
        Assert.Empty(window.FindAll("footer[data-read-only]"));
    }

    // -- Data -------------------------------------------------------------------------------------

    private static EditorSubjectDto Grouping(
        string kind, EditorSubjectMatchBadgeDto? badge = null, (string Key, string Label, int Count)[]? kinds = null) => new()
        {
            Id = SubjectId,
            Kind = kind,
            Title = "Dune",
            ArtworkEntityKind = "Collection",
            ArtworkSlots = kind == EditorSubjectKinds.Series ? ["CoverArt", "Background", "Logo"] : ["CoverArt", "Background"],
            Fields =
        [
            new EditorSubjectFieldDto { Key = "title", Label = "Name", Value = "Dune", CanonicalValue = "Dune", SourceLabel = "Provider" },
            new EditorSubjectFieldDto { Key = "description", Label = "Description", InputKind = EditorSubjectInputKinds.TextArea, Value = "A desert world.", CanonicalValue = "A desert world.", SourceLabel = "Provider" },
            .. kind == EditorSubjectKinds.Series
                ?
                [
                    new EditorSubjectFieldDto { Key = "sort_title", Label = "Sort title" },
                    new EditorSubjectFieldDto { Key = "custom_tags", Label = "Tags", InputKind = EditorSubjectInputKinds.Chips },
                ]
                : Array.Empty<EditorSubjectFieldDto>(),
        ],
            MatchBadge = badge,
            MediaTypes = [.. (kinds ?? []).Select(entry => new EditorSeriesMediaTypeDto { Key = entry.Key, Label = entry.Label, OwnedCount = entry.Count })],
        };

    private static EditorNavItemDto Book(Guid id, int number, string title) => new() { Id = id, Kind = "book", Number = number, Title = title };

    private static EditorHeaderDto SeriesHeader() => new()
    {
        Kind = EditorKindNames.Series,
        Root = new EditorHeaderRootDto { Id = SeriesId, Title = "Dune Novels", MediaType = "Books" },
        CurrentId = SeriesId,
        Label = "Series",
        LabelIsKind = true,
        ShowHome = true,
        HomeId = SeriesId,
        HomeIsCurrent = true,
        ShowItemDropdown = true,
        Level = new EditorHeaderLevelDto
        {
            LevelId = SeriesId,
            Total = 2,
            PageSize = 50,
            Items = [Book(BookOne, 1, "Dune"), Book(BookTwo, 2, "Dune Messiah")],
            SupportingFiles = new EditorHeaderSupportingFilesDto(),
        },
        Config = EditorHeaderTestData.Config(EditorKindNames.Series, "details", "history"),
    };

    private static EditorHeaderDto BookHeader(Guid id) => new()
    {
        Kind = EditorKindNames.Book,
        Root = new EditorHeaderRootDto { Id = SeriesId, Title = "Dune Novels", MediaType = "Books" },
        CurrentId = id,
        Label = "Dune Novels",
        ShowHome = true,
        HomeId = SeriesId,
        ShowItemDropdown = true,
        Level = SeriesHeader().Level,
        Config = EditorHeaderTestData.Config(EditorKindNames.Book, "details", "artwork", "match", "history"),
    };

    private MediaEditorContextDto BooksContext(Guid id, string title, string scope, EditorHeaderDto header) => new()
    {
        LaunchEntityId = id,
        MediaType = _mediaType,
        InitialScope = scope,
        AvailableTabs = ["details", "artwork", "links", "history"],
        Header = header,
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = scope,
                Label = scope,
                FieldEntityId = id,
                DisplayTitle = title,
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                {
                    CanonicalFields = [new MediaEditorScopedFieldValueDto { Key = "title", Value = title, ProviderName = "openlibrary" }],
                    DisplayOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    AllowedOverrideKeys = ["title", "description"],
                },
            },
        ],
    };
}
