using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Batch editing on the details page (details overlay spec D8, D9, D11): tick boxes only in Editing Mode on a desktop, the selection
/// bar, the "Edit together" section above the rows (No change, Mixed, Add / Replace / Remove, one Apply), the result line, "Save or
/// discard?", Move and Delete from the bar. The Engine is a hand-written stub; nothing is changed anywhere.
/// </summary>
public sealed class DetailBatchEditingTests : AsyncBunitContext
{
    private static readonly Guid EpisodeOne = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid EpisodeTwo = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid EpisodeThree = Guid.Parse("e0000000-0000-0000-0000-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<LibraryBatchEditRequest> _applies = [];
    private readonly List<LibraryBatchEditPreviewRequest> _previews = [];
    private readonly List<LibraryMovePreviewRequest> _movePreviews = [];
    private readonly List<LibraryMoveCommitRequest> _moveCommits = [];
    private Func<LibraryMovePreview> _movePreview = () => LibraryMoveTestData.AlbumPreview(
    [
        LibraryMoveTestData.Row(EpisodeOne, "Pilot"),
        LibraryMoveTestData.Row(EpisodeTwo, "Second", suggested: "t2"),
        LibraryMoveTestData.Row(EpisodeThree, "Third", suggested: "t3"),
    ]);
    private readonly List<IReadOnlyList<Guid>> _deletionPreviews = [];
    private readonly Queue<SaveOrDiscardChoice> _answers = new();
    private int _asked;
    private int _refreshed;
    private LibraryBatchEditPreview _preview = DefaultPreview();
    private Func<Task<LibraryBatchEditOutcome<LibraryBatchEditResult>>> _apply = () =>
        Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditResult>.Ok(new LibraryBatchEditResult { UpdatedCount = 3 }));

    public DetailBatchEditingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), args =>
            {
                _previews.Add((LibraryBatchEditPreviewRequest)args![0]!);
                return Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(_preview));
            });
            stub.SetHandler(nameof(IEngineApiClient.ApplyLibraryBatchEditAsync), args =>
            {
                _applies.Add((LibraryBatchEditRequest)args![0]!);
                return _apply();
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryMoveDestinationsAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMoveDestinationsResponse>.Ok(new LibraryMoveDestinationsResponse
                {
                    Destinations = [LibraryMoveTestData.Album()],
                })));
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryMoveAsync), args =>
            {
                _movePreviews.Add((LibraryMovePreviewRequest)args![0]!);
                return Task.FromResult(LibraryMoveOutcome<LibraryMovePreview>.Ok(_movePreview()));
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveLibraryItemsAsync), args =>
            {
                var request = (LibraryMoveCommitRequest)args![0]!;
                _moveCommits.Add(request);
                return Task.FromResult(LibraryMoveOutcome<LibraryMoveCommitResult>.Ok(new LibraryMoveCommitResult
                {
                    MovedCount = request.Rows.Count,
                    Rows = [.. request.Rows.Select(row => new LibraryMoveRowResult { ItemId = row.ItemId, Outcome = LibraryMoveOutcomes.Matched, ResultEntityId = row.ItemId })],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), args =>
            {
                var ids = (IReadOnlyList<Guid>)args![0]!;
                _deletionPreviews.Add(ids);
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [.. ids.Select(id => new LibraryItemDeletionItemDto { WorkId = id, Title = id.ToString(), MediaType = "TV", FileCount = 1 })],
                    Files = [],
                    CuratedCollections = [],
                    Fingerprint = "fp-batch",
                }));
            });
        }));
    }

    // -- Fixtures ------------------------------------------------------------------------------

    private static LibraryBatchEditPreview DefaultPreview() => new()
    {
        AffectedCount = 3,
        Fields =
        [
            new LibraryBatchFieldState
            {
                Key = "genre",
                Label = "Genres",
                Kind = LibraryBatchFieldKinds.List,
                Operations = [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove],
                Editable = true,
                IsMixed = true,
                DistinctValues =
                [
                    new LibraryBatchDistinctValue { Value = "Drama", Count = 2 },
                    new LibraryBatchDistinctValue { Value = "Comedy", Count = 1 },
                ],
                DistinctCount = 2,
            },
            new LibraryBatchFieldState
            {
                Key = "year",
                Label = "Year",
                Kind = LibraryBatchFieldKinds.Scalar,
                Operations = [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear],
                Editable = true,
                Value = "2020",
            },
            new LibraryBatchFieldState
            {
                Key = "tagline",
                Label = "Tagline",
                Kind = LibraryBatchFieldKinds.Scalar,
                Operations = [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear],
                Editable = false,
                LockedItems = [new LibraryBatchLockedItem { EntityId = EpisodeTwo, Reason = "The season supplies this." }],
            },
            new LibraryBatchFieldState
            {
                Key = "director",
                Label = "Directors",
                Kind = LibraryBatchFieldKinds.List,
                Operations = [],
                Editable = false,
                LockedItems = [new LibraryBatchLockedItem { EntityId = EpisodeOne, Reason = "Credits come from the catalogue and cannot be edited." }],
            },
        ],
    };

    private static DetailBatchRow Episode(Guid id, string title) => new(id, title, DetailEntityType.TvEpisode);

    private static DashboardSessionAccessor Administrator()
    {
        var profile = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("token", Guid.NewGuid(), profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(profile, profile, true, true, 1, 1, true, true, null, 1, [], [], []));
        return session;
    }

    private Task UseDevice(string formFactor) => formFactor switch
    {
        "tablet" => _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true)),
        "phone" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)),
        _ => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)),
    };

    private sealed record Page(
        EditingModeContext Mode,
        DetailBatchSelection Selection,
        IRenderedComponent<DetailBatchPanel> Panel);

    // A page with Editing Mode on and the three episodes ticked, as far as the bar and the section are concerned.
    private async Task<Page> StartAsync(bool tick = true, params DetailBatchRow[] rows)
    {
        await UseDevice("desktop");
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        var mode = new EditingModeContext(_device, session)
        {
            HasPermission = true,
            AskSaveOrDiscard = () =>
            {
                _asked++;
                return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : SaveOrDiscardChoice.Cancel);
            },
        };
        Assert.True(await mode.EnterAsync());
        var selection = new DetailBatchSelection
        {
            MediaType = "TV",
            RefreshAsync = () =>
            {
                _refreshed++;
                return Task.CompletedTask;
            },
        };
        if (tick)
        {
            foreach (var row in rows.Length > 0 ? rows : [Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second"), Episode(EpisodeThree, "Third")])
            {
                selection.Toggle(row);
            }
        }

        var panel = Render<DetailBatchPanel>(parameters => parameters.AddCascadingValue(mode).AddCascadingValue(selection));
        return new Page(mode, selection, panel);
    }

    // The selection bar's own buttons (a field in the section can have its own "Clear").
    private static IElement Button(IRenderedComponent<DetailBatchPanel> panel, string label) =>
        panel.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == label);

    private static bool HasButton(IRenderedComponent<DetailBatchPanel> panel, string label) =>
        panel.FindAll(".tl-batch__bar button").Any(button => button.TextContent.Trim() == label);

    // The bar's "..." menu: Move and Delete live in it, not on the bar.
    private static IReadOnlyList<IElement> OpenBarMenu(IRenderedComponent<DetailBatchPanel> panel)
    {
        panel.Find(".tl-batch__bar button[aria-label='More actions']").Click();
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll("[role='menuitem']")));
        return panel.FindAll("[role='menuitem']");
    }

    private static string ItemLabel(IElement item) => Regex.Replace(item.TextContent, "\\s+", " ").Trim();

    private static IElement MenuItem(IRenderedComponent<DetailBatchPanel> panel, string label) =>
        OpenBarMenu(panel).Single(item => ItemLabel(item).StartsWith(label, StringComparison.Ordinal));

    private static bool HasMenuItem(IRenderedComponent<DetailBatchPanel> panel, string label)
    {
        var menu = panel.FindAll(".tl-batch__bar button[aria-label='More actions']");
        if (menu.Count == 0)
        {
            return false;
        }

        return OpenBarMenu(panel).Any(item => ItemLabel(item).StartsWith(label, StringComparison.Ordinal));
    }

    private static void OpenSection(Page page)
    {
        Button(page.Panel, "Edit together").Click();
        page.Panel.WaitForElement(".tl-batch-field");
    }

    private static string Field(IRenderedComponent<DetailBatchPanel> panel, string key) =>
        panel.Find($"[data-field='{key}']").TextContent;

    private static void TypeScalar(IRenderedComponent<DetailBatchPanel> panel, string key, string text) =>
        panel.Find($"[data-field='{key}'] input").Input(text);

    private static void AddListValue(IRenderedComponent<DetailBatchPanel> panel, string key, string value)
    {
        panel.Find($"[data-field='{key}'] input[aria-label='Tag to add']").Input(value);
        panel.Find($"[data-field='{key}'] form").Submit();
    }

    private static void ClickApply(IRenderedComponent<DetailBatchPanel> panel) =>
        panel.FindAll(".tl-batch-section__footer button").Single(button => button.TextContent.Trim().StartsWith("Apply to", StringComparison.Ordinal)).Click();

    // -- Tick boxes ----------------------------------------------------------------------------

    private IRenderedComponent<DetailRowSelectBox> RenderBox(EditingModeContext mode, DetailBatchSelection selection, Guid id, string title, DetailEntityType kind) =>
        Render<DetailRowSelectBox>(parameters => parameters
            .AddCascadingValue(mode)
            .AddCascadingValue(selection)
            .Add(component => component.ItemId, id)
            .Add(component => component.Title, title)
            .Add(component => component.Kind, kind));

    [Fact]
    public async Task TickBox_InEditingModeOnADesktop_IsLabelledWithTheRowTitle()
    {
        var page = await StartAsync(tick: false);

        var box = RenderBox(page.Mode, page.Selection, EpisodeOne, "Pilot", DetailEntityType.TvEpisode);

        Assert.Equal("Select Pilot", box.Find("input[type=checkbox]").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TickBox_OutsideEditingMode_DoesNotRender()
    {
        var page = await StartAsync(tick: false);
        page.Mode.Leave();

        var box = RenderBox(page.Mode, page.Selection, EpisodeOne, "Pilot", DetailEntityType.TvEpisode);

        Assert.Empty(box.FindAll("input"));
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task TickBox_OnADeviceThatMayNotEdit_DoesNotRender(string formFactor)
    {
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        await UseDevice(formFactor);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };

        var box = RenderBox(mode, new DetailBatchSelection(), EpisodeOne, "Pilot", DetailEntityType.TvEpisode);

        Assert.False(await mode.EnterAsync());
        Assert.Empty(box.FindAll("input"));
    }

    [Fact]
    public async Task TickBox_ForASeasonRow_DoesNotRender()
    {
        var page = await StartAsync(tick: false);

        var box = RenderBox(page.Mode, page.Selection, Guid.NewGuid(), "Season 1", DetailEntityType.TvSeason);

        Assert.Empty(box.FindAll("input"));
    }

    [Fact]
    public async Task Ticking_ARow_AddsItToTheSelectionAndTheBarShowsTheCount()
    {
        var page = await StartAsync(tick: false);
        var first = RenderBox(page.Mode, page.Selection, EpisodeOne, "Pilot", DetailEntityType.TvEpisode);
        var second = RenderBox(page.Mode, page.Selection, EpisodeTwo, "Second", DetailEntityType.TvEpisode);
        Assert.Empty(page.Panel.FindAll(".tl-batch__bar"));

        first.Find("input").Change(true);
        second.Find("input").Change(true);

        page.Panel.WaitForAssertion(() => Assert.Equal("2 selected", page.Panel.Find(".tl-batch__count").TextContent.Trim()));
        Assert.Equal([EpisodeOne, EpisodeTwo], page.Selection.Ids);
    }

    [Fact]
    public async Task Clear_EmptiesTheSelectionAndHidesTheBar()
    {
        var page = await StartAsync();
        Assert.Equal("3 selected", page.Panel.Find(".tl-batch__count").TextContent.Trim());

        Button(page.Panel, "Clear").Click();

        page.Panel.WaitForAssertion(() => Assert.Empty(page.Panel.FindAll(".tl-batch__bar")));
        Assert.Equal(0, page.Selection.Count);
    }

    // -- The batch section ---------------------------------------------------------------------

    [Fact]
    public async Task EditTogether_OpensTheBatchSectionAboveTheRowsAndNotInTheHeroSlot()
    {
        var page = await StartAsync();

        OpenSection(page);

        Assert.Equal(EditingSection.Batch, page.Mode.OpenSectionKind);
        Assert.NotEmpty(page.Panel.FindAll("#tl-batch-section"));
        Assert.Empty(page.Panel.FindAll("#tl-editing-slot"));
        Assert.Equal(["3 episodes"], [page.Panel.Find(".tl-batch-section__scope").TextContent.Trim()]);
        var request = Assert.Single(_previews);
        Assert.Equal([EpisodeOne, EpisodeTwo, EpisodeThree], request.EntityIds);
    }

    [Fact]
    public async Task EditTogether_ClosesAHeroSectionAndAHeroSectionClosesIt()
    {
        var page = await StartAsync();
        Assert.True(page.Mode.OpenSection(EditingSection.Artwork));

        OpenSection(page);

        Assert.Equal(EditingSection.Batch, page.Mode.OpenSectionKind);

        Assert.True(await page.Mode.ShowSectionAsync(EditingSection.History));

        Assert.Equal(EditingSection.History, page.Mode.OpenSectionKind);
        page.Panel.WaitForAssertion(() => Assert.Empty(page.Panel.FindAll("#tl-batch-section")));
    }

    [Fact]
    public async Task Section_ShowsNoChangeForEveryFieldAndMixedWhereValuesDiffer()
    {
        var page = await StartAsync();

        OpenSection(page);

        Assert.Equal(DetailBatchText.NoChange, page.Panel.Find("[data-field='genre'] .tl-batch-field__state").TextContent.Trim());
        Assert.Equal(DetailBatchText.NoChange, page.Panel.Find("[data-field='year'] .tl-batch-field__state").TextContent.Trim());
        Assert.StartsWith("Mixed", page.Panel.Find("[data-field='genre'] .tl-batch-field__current").TextContent.Trim(), StringComparison.Ordinal);
        Assert.Contains("Drama (2), Comedy (1)", Field(page.Panel, "genre"), StringComparison.Ordinal);
        Assert.DoesNotContain("Mixed", page.Panel.Find("[data-field='year'] .tl-batch-field__current").TextContent, StringComparison.Ordinal);
        Assert.Contains("2020", page.Panel.Find("[data-field='year'] .tl-batch-field__current").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Section_OffersAddReplaceAndRemoveForAListFieldWithAddChosenFirst()
    {
        var page = await StartAsync();

        OpenSection(page);

        var options = page.Panel.FindAll("[data-field='genre'] .app-segmented-control__item");
        Assert.Equal(["Add", "Replace", "Remove"], options.Select(option => option.TextContent.Trim()));
        Assert.Equal("Add", Assert.Single(options, option => option.GetAttribute("aria-pressed") == "true").TextContent.Trim());
        Assert.Empty(page.Panel.FindAll("[data-field='year'] .app-segmented-control__item"));
    }

    [Fact]
    public async Task Section_MarksAFieldNoOneCanChangeAndAFieldSomeItemsCannotChangeWithTheirReasons()
    {
        var page = await StartAsync();

        OpenSection(page);

        var credits = page.Panel.Find("[data-field='director']");
        Assert.Contains("Credits come from the catalogue and cannot be edited.", credits.TextContent, StringComparison.Ordinal);
        Assert.Empty(credits.QuerySelectorAll("input"));
        var tagline = Field(page.Panel, "tagline");
        Assert.Contains("1 of 3 can't take this change", tagline, StringComparison.Ordinal);
        Assert.Contains("Second (The season supplies this)", tagline, StringComparison.Ordinal);
        Assert.NotEmpty(page.Panel.Find("[data-field='tagline']").QuerySelectorAll("input"));
    }

    [Fact]
    public async Task Section_OffersClearOnlyWhereTheFieldAllowsIt()
    {
        var page = await StartAsync();

        OpenSection(page);

        Assert.Contains(page.Panel.FindAll("[data-field='year'] button"), button => button.TextContent.Trim() == "Clear");
        Assert.DoesNotContain(page.Panel.FindAll("[data-field='genre'] button"), button => button.TextContent.Trim() == "Clear");
    }

    [Fact]
    public async Task Apply_IsOffUntilAFieldIsTouchedAndNamesTheKindOfRow()
    {
        var page = await StartAsync();

        OpenSection(page);

        var apply = page.Panel.Find(".tl-batch-section__footer button");
        Assert.Equal("Apply to 3 episodes", apply.TextContent.Trim());
        Assert.True(apply.HasAttribute("disabled"));

        TypeScalar(page.Panel, "year", "2021");

        Assert.False(page.Panel.Find(".tl-batch-section__footer button").HasAttribute("disabled"));
        Assert.Equal("Will change", page.Panel.Find("[data-field='year'] .tl-batch-field__state").TextContent.Trim());
    }

    [Theory]
    [InlineData(DetailEntityType.TvEpisode, 3, "Apply to 3 episodes")]
    [InlineData(DetailEntityType.Work, 2, "Apply to 2 tracks")]
    [InlineData(DetailEntityType.Book, 4, "Apply to 4 items")]
    [InlineData(DetailEntityType.TvEpisode, 1, "Apply to 1 episode")]
    public void ApplyLabel_UsesThePlainNounOfTheTickedRows(DetailEntityType kind, int count, string expected) =>
        Assert.Equal(expected, DetailBatchText.ApplyLabel(count, Enumerable.Repeat(kind, count)));

    // -- Apply ---------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_SendsExactlyTheTouchedFieldsWithTheirOperationsOnceForEveryTickedRow()
    {
        var page = await StartAsync();
        OpenSection(page);
        AddListValue(page.Panel, "genre", "Mystery");
        page.Panel.FindAll("[data-field='genre'] .app-segmented-control__item").Single(option => option.TextContent.Trim() == "Replace").Click();
        AddListValue(page.Panel, "genre", "Thriller");
        TypeScalar(page.Panel, "year", "2021");

        ClickApply(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Single(_applies));
        var request = _applies[0];
        Assert.Equal([EpisodeOne, EpisodeTwo, EpisodeThree], request.EntityIds);
        Assert.Equal(["genre", "year"], request.FieldChanges.Select(change => change.Key).Order());
        var genre = request.FieldChanges.Single(change => change.Key == "genre");
        Assert.Equal(LibraryBatchEditOps.Replace, genre.Op);
        Assert.Equal(["Mystery", "Thriller"], genre.Values);
        var year = request.FieldChanges.Single(change => change.Key == "year");
        Assert.Equal(LibraryBatchEditOps.Set, year.Op);
        Assert.Equal("2021", year.Value);
        Assert.DoesNotContain(request.FieldChanges, change => change.Key is "tagline" or "director");
    }

    [Fact]
    public async Task Apply_ClickedTwiceWhileItIsRunning_AppliesOnlyOnce()
    {
        var gate = new TaskCompletionSource<LibraryBatchEditOutcome<LibraryBatchEditResult>>();
        _apply = () => gate.Task;
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");

        ClickApply(page.Panel);
        page.Panel.WaitForAssertion(() => Assert.True(page.Mode.SectionSaving));
        foreach (var button in page.Panel.FindAll(".tl-batch-section__footer button"))
        {
            if (!button.HasAttribute("disabled"))
            {
                button.Click();
            }
        }

        gate.SetResult(LibraryBatchEditOutcome<LibraryBatchEditResult>.Ok(new LibraryBatchEditResult { UpdatedCount = 3 }));
        page.Panel.WaitForAssertion(() => Assert.False(page.Mode.SectionSaving));
        Assert.Single(_applies);
    }

    [Fact]
    public async Task Apply_AfterSuccess_ShowsTheResultLineKeepsTheSelectionAndRefreshesThePage()
    {
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");

        ClickApply(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Equal("3 updated", page.Panel.Find(".tl-batch__result-line").TextContent.Trim()));
        Assert.Equal(3, page.Selection.Count);
        Assert.Equal(1, _refreshed);
        Assert.False(page.Mode.SectionDirty);
        Assert.Equal(DetailBatchText.NoChange, page.Panel.Find("[data-field='year'] .tl-batch-field__state").TextContent.Trim());
        Assert.Equal(2, _previews.Count);
    }

    [Fact]
    public async Task Apply_WhenARowGoesToNeedsReview_SaysSoInTheResultLineAndNamesTheRowAndItsReason()
    {
        _apply = () => Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditResult>.Ok(new LibraryBatchEditResult
        {
            UpdatedCount = 2,
            SentToReviewCount = 1,
            Items =
            [
                new LibraryBatchItemResult { EntityId = EpisodeOne, Outcome = LibraryBatchItemOutcomes.Updated },
                new LibraryBatchItemResult { EntityId = EpisodeTwo, Outcome = LibraryBatchItemOutcomes.SentToReview, Reason = "The file is locked." },
                new LibraryBatchItemResult { EntityId = EpisodeThree, Outcome = LibraryBatchItemOutcomes.Updated },
            ],
        }));
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");

        ClickApply(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Equal("2 updated, 1 sent to Needs Review", page.Panel.Find(".tl-batch__result-line").TextContent.Trim()));
        Assert.Equal("Second: sent to Needs Review. The file is locked.", page.Panel.Find(".tl-batch__result-notes li").TextContent.Trim());
    }

    [Fact]
    public async Task Apply_WhenARowIsRefused_ShowsItsReasonInTheLineAndOnItsRow()
    {
        _apply = () => Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditResult>.Ok(new LibraryBatchEditResult
        {
            UpdatedCount = 1,
            UnchangedCount = 1,
            RefusedCount = 1,
            Items =
            [
                new LibraryBatchItemResult { EntityId = EpisodeThree, Outcome = LibraryBatchItemOutcomes.Refused, Reason = "A genre list cannot be emptied here." },
            ],
        }));
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");

        ClickApply(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Equal(
            "1 updated, 1 unchanged, 1 not changed: A genre list cannot be emptied here",
            page.Panel.Find(".tl-batch__result-line").TextContent.Trim()));
        Assert.Equal("Third: not changed. A genre list cannot be emptied here.", page.Panel.Find(".tl-batch__result-notes li").TextContent.Trim());
    }

    [Fact]
    public async Task Apply_WhenTheEngineDoesNotAnswer_KeepsTheInputAndSaysItCouldNotConfirmTheResultWithoutClaimingNothingChanged()
    {
        _apply = () => Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditResult>.Failed(LibraryBatchEditOutcomeStatus.Unavailable));
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");

        ClickApply(page.Panel);

        page.Panel.WaitForAssertion(() => Assert.Contains("Couldn't confirm the result. Reload to check.", page.Panel.Find(".tl-batch-section__message").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing was changed", page.Panel.Find(".tl-batch-section__message").TextContent, StringComparison.Ordinal);
        Assert.True(page.Mode.SectionDirty);
        Assert.Empty(page.Panel.FindAll(".tl-batch__result"));
    }

    [Fact]
    public void ResultLine_ForOnlyUpdatedRows_IsJustTheCount()
    {
        var view = DetailBatchText.Result(new LibraryBatchEditResult { UpdatedCount = 3 }, []);

        Assert.Equal("3 updated", view.Line);
        Assert.False(view.HasProblems);
    }

    // -- Save or discard -----------------------------------------------------------------------

    [Fact]
    public async Task Done_WithTouchedFields_AsksSaveOrDiscardAndSaveAppliesThenLeaves()
    {
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");
        _answers.Enqueue(SaveOrDiscardChoice.Save);

        var left = await page.Panel.InvokeAsync(page.Mode.DoneAsync);

        Assert.True(left);
        Assert.Equal(1, _asked);
        Assert.Single(_applies);
        Assert.False(page.Mode.IsOn);
    }

    [Fact]
    public async Task Done_WithTouchedFields_AndDiscard_DropsTheChangesWithoutApplying()
    {
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        var left = await page.Panel.InvokeAsync(page.Mode.DoneAsync);

        Assert.True(left);
        Assert.Empty(_applies);
        Assert.False(page.Mode.IsOn);
    }

    [Fact]
    public async Task Done_WithTouchedFields_AndCancel_StaysInEditingModeWithTheInputKept()
    {
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        var left = await page.Panel.InvokeAsync(page.Mode.DoneAsync);

        Assert.False(left);
        Assert.True(page.Mode.IsOn);
        Assert.Empty(_applies);
        Assert.Equal("2021", page.Panel.Find("[data-field='year'] input").GetAttribute("value"));
    }

    [Fact]
    public async Task Esc_WithTouchedFields_AsksSaveOrDiscardBeforeClosingTheSection()
    {
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        var used = await page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());

        Assert.True(used);
        Assert.Equal(1, _asked);
        Assert.Null(page.Mode.OpenSectionKind);
        Assert.True(page.Mode.IsOn);
        Assert.Empty(_applies);
    }

    [Fact]
    public async Task Done_WithNoTouchedFields_LeavesWithoutAsking()
    {
        var page = await StartAsync();
        OpenSection(page);

        var left = await page.Panel.InvokeAsync(page.Mode.DoneAsync);

        Assert.True(left);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task Clear_WithTouchedFields_AsksSaveOrDiscardAndCancelKeepsTheRowsTicked()
    {
        var page = await StartAsync();
        OpenSection(page);
        TypeScalar(page.Panel, "year", "2021");
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        Button(page.Panel, "Clear").Click();

        page.Panel.WaitForAssertion(() => Assert.Equal(1, _asked));
        Assert.Equal(3, page.Selection.Count);
        Assert.Equal(EditingSection.Batch, page.Mode.OpenSectionKind);
    }

    // -- Move ----------------------------------------------------------------------------------

    [Fact]
    public async Task Move_FromTheBar_OpensThePickerAndThenTheMatchStepWithEveryTickedRow()
    {
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second"), Episode(EpisodeThree, "Third"));

        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement($"[data-destination-id='{LibraryMoveTestData.AlbumId}']").Click();
        page.Panel.WaitForElement(".tl-match-step");

        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        var request = Assert.Single(_movePreviews);
        Assert.Equal([EpisodeOne, EpisodeTwo, EpisodeThree], request.ItemIds);
        Assert.Equal(LibraryMoveTestData.AlbumId, request.DestinationId);
        Assert.Contains("Pilot", page.Panel.Find(".tl-match-step").TextContent, StringComparison.Ordinal);
        Assert.Contains("Third", page.Panel.Find(".tl-match-step").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Move_ForRowsOfDifferentKinds_IsNotOffered()
    {
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), new DetailBatchRow(Guid.NewGuid(), "A song", DetailEntityType.Work));

        Assert.False(HasMenuItem(page.Panel, "Move"));
        Assert.True(HasButton(page.Panel, "Edit together"));
    }

    // -- Delete --------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_ForAnAdministrator_OpensOneDialogForEveryTickedRow()
    {
        Services.AddSingleton(Administrator());
        var host = Render<AppDialogHost>();
        var page = await StartAsync();

        MenuItem(page.Panel, "Delete from library…").Click();
        host.WaitForElement(".delete-library");

        var ids = Assert.Single(_deletionPreviews);
        Assert.Equal([EpisodeOne, EpisodeTwo, EpisodeThree], ids);
        Assert.Single(host.FindAll(".delete-library"));
    }

    [Fact]
    public async Task Delete_ForSomeoneWhoMayNotDelete_IsNotOffered()
    {
        var page = await StartAsync();

        Assert.False(HasMenuItem(page.Panel, "Delete"));
        Assert.True(HasButton(page.Panel, "Edit together"));
    }

    [Fact]
    public async Task Delete_ForRowsThatAreNotSingleTitles_IsNotOffered()
    {
        Services.AddSingleton(Administrator());
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), new DetailBatchRow(Guid.NewGuid(), "Season 1", DetailEntityType.TvSeason));

        Assert.False(HasMenuItem(page.Panel, "Delete"));
    }


    // -- Move is a section of Editing Mode ------------------------------------------------------

    private static LibraryMovePreview TwoRowPreview() => LibraryMoveTestData.AlbumPreview(
    [
        LibraryMoveTestData.Row(EpisodeOne, "Pilot", suggested: "t2"),
        LibraryMoveTestData.Row(EpisodeTwo, "Second", suggested: "t3"),
    ]);

    // Opens the bar's Move, picks the album and waits for the Match step: the person now has picks.
    private static void PickADestination(Page page)
    {
        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement($"[data-destination-id='{LibraryMoveTestData.AlbumId}']").Click();
        page.Panel.WaitForElement(".tl-match-step");
    }

    [Fact]
    public async Task Move_FromTheBar_IsASectionOfEditingModeAndPicksCountAsUnsavedInput()
    {
        var page = await StartAsync();

        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement(".tl-move-picker");

        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        Assert.False(page.Mode.SectionDirty);

        page.Panel.WaitForElement($"[data-destination-id='{LibraryMoveTestData.AlbumId}']").Click();
        page.Panel.WaitForElement(".tl-match-step");

        Assert.True(page.Mode.SectionDirty);
    }

    [Fact]
    public async Task Esc_WithMovePicks_AsksSaveOrDiscardAndDiscardDropsThePicksButKeepsTheTicksAndEditingMode()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        var used = await page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());

        Assert.True(used);
        Assert.Equal(1, _asked);
        Assert.Null(page.Mode.OpenSectionKind);
        Assert.True(page.Mode.IsOn);
        Assert.False(page.Mode.SectionDirty);
        Assert.Equal(3, page.Selection.Count);
        Assert.Empty(_moveCommits);
        page.Panel.WaitForAssertion(() => Assert.Empty(page.Panel.FindAll(".tl-batch-move")));
    }

    [Fact]
    public async Task Esc_WithMovePicks_AndCancel_KeepsTheMatchStepWithItsPicks()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        var used = await page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());

        Assert.True(used);
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        Assert.True(page.Mode.SectionDirty);
        Assert.NotEmpty(page.Panel.FindAll(".tl-match-step"));
    }

    [Fact]
    public async Task Esc_WithMovePicks_AndSave_ContinuesToTheMoveConfirmationAndMovesWhenConfirmed()
    {
        _movePreview = TwoRowPreview;
        var host = Render<AppDialogHost>();
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second"));
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Save);

        var escape = page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());
        host.WaitForElement(".app-confirm");

        Assert.Empty(_moveCommits);
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Move").Click();
        await escape;

        page.Panel.WaitForAssertion(() => Assert.Null(page.Mode.OpenSectionKind));
        var commit = Assert.Single(_moveCommits);
        Assert.Equal(2, commit.Rows.Count);
        Assert.Equal(0, page.Selection.Count);
        Assert.Equal(1, _refreshed);
    }

    [Fact]
    public async Task Esc_WithMovePicks_AndSave_WhenTheConfirmationIsDeclined_StaysOpenWithThePicks()
    {
        _movePreview = TwoRowPreview;
        var host = Render<AppDialogHost>();
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second"));
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Save);

        var escape = page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        await escape;

        Assert.Empty(_moveCommits);
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        Assert.True(page.Mode.SectionDirty);
        Assert.Equal(2, page.Selection.Count);
    }

    [Fact]
    public async Task Esc_WithMovePicks_AndSave_WithRowsStillUnmatched_StaysOpenAndSaysToMatchEveryRowFirst()
    {
        _movePreview = () => LibraryMoveTestData.AlbumPreview(
        [
            LibraryMoveTestData.Row(EpisodeOne, "Pilot", suggested: null),
            LibraryMoveTestData.Row(EpisodeTwo, "Second", suggested: "t3"),
        ]);
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second"));
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Save);

        await page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());

        Assert.Empty(_moveCommits);
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        page.Panel.WaitForAssertion(() => Assert.Contains(LibraryMoveText.MatchEveryRowFirst, page.Panel.Find(".tl-match-step").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Done_WithMovePicks_AsksSaveOrDiscardAndDiscardLeavesWithoutMoving()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        var left = await page.Panel.InvokeAsync(page.Mode.DoneAsync);

        Assert.True(left);
        Assert.Equal(1, _asked);
        Assert.False(page.Mode.IsOn);
        Assert.Empty(_moveCommits);
    }

    [Fact]
    public async Task Back_OnTheOverlay_WithMovePicks_GoesThroughTheSameQuestionAndCancelStays()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        var mayDismiss = await page.Panel.InvokeAsync(() => page.Mode.CanDismissAsync().AsTask());

        Assert.False(mayDismiss);
        Assert.Equal(1, _asked);
        Assert.True(page.Mode.IsOn);
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
    }

    [Fact]
    public async Task Move_WithNoPicksYet_ClosesOnEscWithoutAsking()
    {
        var page = await StartAsync();
        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement(".tl-move-picker");

        await page.Panel.InvokeAsync(() => page.Mode.HandleEscapeAsync().AsTask());

        Assert.Equal(0, _asked);
        Assert.Null(page.Mode.OpenSectionKind);
        Assert.True(page.Mode.IsOn);
    }

    [Fact]
    public async Task Move_Cancel_OnTheMatchStep_DropsThePicksAndClosesTheSection()
    {
        var page = await StartAsync();
        PickADestination(page);

        page.Panel.FindAll(".tl-match-step button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        page.Panel.WaitForAssertion(() => Assert.Null(page.Mode.OpenSectionKind));
        Assert.False(page.Mode.SectionDirty);
        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task OpeningAHeroSection_WhileMoveHasPicks_AsksSaveOrDiscardFirstAndCancelKeepsMove()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        var opened = await page.Panel.InvokeAsync(() => page.Mode.ShowSectionAsync(EditingSection.History));

        Assert.False(opened);
        Assert.Equal(1, _asked);
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        Assert.NotEmpty(page.Panel.FindAll(".tl-match-step"));
    }

    [Fact]
    public async Task OpeningAHeroSection_WhileMoveHasPicks_AndDiscard_ClosesMoveAndOpensTheHeroSection()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        var opened = await page.Panel.InvokeAsync(() => page.Mode.ShowSectionAsync(EditingSection.History));

        Assert.True(opened);
        Assert.Equal(EditingSection.History, page.Mode.OpenSectionKind);
        page.Panel.WaitForAssertion(() => Assert.Empty(page.Panel.FindAll(".tl-batch-move")));
        Assert.False(page.Mode.SectionDirty);
    }

    [Fact]
    public async Task EditTogether_WhileMoveHasPicks_AsksSaveOrDiscardFirst_AndDiscardOpensTheBatchSection()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Discard);

        Button(page.Panel, "Edit together").Click();
        page.Panel.WaitForElement(".tl-batch-field");

        Assert.Equal(1, _asked);
        Assert.Equal(EditingSection.Batch, page.Mode.OpenSectionKind);
        Assert.Empty(page.Panel.FindAll(".tl-batch-move"));
    }

    [Fact]
    public async Task EditTogether_WhileMoveHasPicks_AndCancel_KeepsMoveOpen()
    {
        var page = await StartAsync();
        PickADestination(page);
        _answers.Enqueue(SaveOrDiscardChoice.Cancel);

        Button(page.Panel, "Edit together").Click();

        page.Panel.WaitForAssertion(() => Assert.Equal(1, _asked));
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        Assert.Empty(page.Panel.FindAll("#tl-batch-section"));
    }

    [Fact]
    public async Task OneSectionAtATime_AcrossTheHeroSlotEditTogetherAndMove()
    {
        var page = await StartAsync();

        // A hero section is open; Move takes its place.
        Assert.True(page.Mode.OpenSection(EditingSection.Artwork));
        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement(".tl-move-picker");
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);

        // Edit together takes Move's place (no picks yet, so nothing is asked).
        Button(page.Panel, "Edit together").Click();
        page.Panel.WaitForElement(".tl-batch-field");
        Assert.Equal(EditingSection.Batch, page.Mode.OpenSectionKind);
        Assert.Empty(page.Panel.FindAll(".tl-batch-move"));

        // Move takes Edit together's place.
        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement(".tl-move-picker");
        Assert.Equal(EditingSection.BatchMove, page.Mode.OpenSectionKind);
        Assert.Empty(page.Panel.FindAll("#tl-batch-section"));

        // A hero section takes Move's place.
        Assert.True(await page.Panel.InvokeAsync(() => page.Mode.ShowSectionAsync(EditingSection.Match)));
        Assert.Equal(EditingSection.Match, page.Mode.OpenSectionKind);
        page.Panel.WaitForAssertion(() => Assert.Empty(page.Panel.FindAll(".tl-batch-move")));
        Assert.Equal(0, _asked);
    }

    [Fact]
    public void TheBatchSections_AreNotHostedInTheHeroSlot()
    {
        Assert.True(((EditingSection?)EditingSection.Batch).IsBatch());
        Assert.True(((EditingSection?)EditingSection.BatchMove).IsBatch());
        Assert.False(((EditingSection?)EditingSection.Match).IsBatch());
        Assert.False(((EditingSection?)null).IsBatch());
    }

    // -- The bar and its "..." menu --------------------------------------------------------------

    [Fact]
    public async Task Bar_ShowsTheCountEditTogetherTheMenuAndClear_WithMoveAndDeleteInsideTheMenuOnly()
    {
        Services.AddSingleton(Administrator());
        var page = await StartAsync();

        var buttons = page.Panel.FindAll(".tl-batch__bar button:not([hidden])")
            .Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim())
            .ToList();

        Assert.Equal(["Edit together", "More actions", "Clear"], buttons);
        var labels = OpenBarMenu(page.Panel).Select(ItemLabel).ToList();
        Assert.Equal(["Move…", "Delete from library…"], labels);
    }

    [Fact]
    public async Task BarMenu_ForSomeoneWhoMayNotDelete_HoldsOnlyMove()
    {
        var page = await StartAsync();

        var labels = OpenBarMenu(page.Panel).Select(ItemLabel).ToList();

        Assert.Equal(["Move…"], labels);
    }

    [Fact]
    public async Task BarMenu_ForRowsThatCannotMoveOrBeDeleted_HasNoMenuAtAll()
    {
        var page = await StartAsync(tick: true, Episode(EpisodeOne, "Pilot"), new DetailBatchRow(Guid.NewGuid(), "A song", DetailEntityType.Work));

        Assert.Empty(page.Panel.FindAll(".tl-batch__bar button[aria-label='More actions']"));
        Assert.True(HasButton(page.Panel, "Edit together"));
    }

    [Fact]
    public void BarMenuActions_PutDeleteLastInTheDestructiveToneAndOffAboveTwoHundredWithItsReason()
    {
        var few = Enumerable.Range(1, 3).Select(index => Episode(Guid.NewGuid(), $"Episode {index}")).ToList();
        var many = Enumerable.Range(1, 201).Select(index => Episode(Guid.NewGuid(), $"Episode {index}")).ToList();

        var fewActions = DetailBatchRules.BarMenuActions(few, canMove: true, canDelete: true, busy: false);
        var manyActions = DetailBatchRules.BarMenuActions(many, canMove: true, canDelete: true, busy: false);

        Assert.Equal([DetailBatchRules.MoveActionKey, DetailActionKeys.DeleteFromLibrary], fewActions.Select(action => action.Key));
        Assert.True(DetailMenuGrouping.IsDestructive(fewActions[^1]));
        Assert.False(fewActions[^1].IsDisabled);
        Assert.True(manyActions[^1].IsDisabled);
        Assert.Equal("Delete up to 200 at a time", manyActions[^1].Tooltip);
        Assert.False(manyActions[0].IsDisabled);
        Assert.Equal(DetailMenuGrouping.DestructiveGroup, DetailMenuGrouping.GroupOf(manyActions[^1]));
    }

    [Fact]
    public async Task BarMenu_DeleteAboveTwoHundred_IsOffWithItsReasonAndDoesNotOpenTheDialog()
    {
        Services.AddSingleton(Administrator());
        var host = Render<AppDialogHost>();
        var rows = Enumerable.Range(1, 201)
            .Select(index => Episode(Guid.Parse($"e1000000-0000-0000-0000-{index:D12}"), $"Episode {index}"))
            .ToArray();
        var page = await StartAsync(tick: true, rows);

        var delete = OpenBarMenu(page.Panel).Single(item => ItemLabel(item).StartsWith("Delete from library", StringComparison.Ordinal));

        Assert.Contains("Delete up to 200 at a time", ItemLabel(delete), StringComparison.Ordinal);
        Assert.True(delete.HasAttribute("disabled") || delete.GetAttribute("aria-disabled") == "true");
        delete.Click();
        Assert.Empty(host.FindAll(".delete-library"));
        Assert.Empty(_deletionPreviews);
    }

    [Fact]
    public async Task Bar_WhileASectionIsSaving_DisablesItsButtonsAndMenuEntries()
    {
        Services.AddSingleton(Administrator());
        var page = await StartAsync();

        page.Mode.SetSectionSaving(true);

        page.Panel.WaitForAssertion(() =>
        {
            Assert.True(Button(page.Panel, "Edit together").HasAttribute("disabled"));
            Assert.True(Button(page.Panel, "Clear").HasAttribute("disabled"));
        });
        Assert.All(OpenBarMenu(page.Panel), item => Assert.True(item.HasAttribute("disabled") || item.GetAttribute("aria-disabled") == "true"));
    }

    // -- Focus moves with the section ------------------------------------------------------------

    [Fact]
    public async Task BatchSection_OnOpening_RemembersTheOpenerAndMovesFocusIntoItsHeading()
    {
        var page = await StartAsync();

        OpenSection(page);

        page.Panel.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "tuvimaFocusById" && Equals(call.Arguments[0], "tl-batch-section-title")));
        Assert.Contains(JSInterop.Invocations, call => call.Identifier == "tuvimaFocusReturn.remember");
        Assert.Equal("-1", page.Panel.Find("#tl-batch-section-title").GetAttribute("tabindex"));
    }

    [Fact]
    public async Task BatchSection_OnClosing_ReturnsFocusToEditTogetherOrTheBar()
    {
        var page = await StartAsync();
        OpenSection(page);

        await page.Panel.InvokeAsync(page.Mode.CloseSectionAsync);

        page.Panel.WaitForAssertion(() =>
        {
            var restore = Assert.Single(JSInterop.Invocations, call => call.Identifier == "tuvimaFocusReturn.restore");
            var fallbacks = Assert.IsType<string[]>(restore.Arguments[0]);
            Assert.Equal(".tl-batch__edit-together", fallbacks[0]);
        });
        Assert.NotEmpty(page.Panel.FindAll(".tl-batch__edit-together"));
    }

    [Fact]
    public async Task BatchSection_WhenAnotherSectionTakesItsPlace_DoesNotStealFocusBack()
    {
        var page = await StartAsync();
        OpenSection(page);

        Assert.True(await page.Panel.InvokeAsync(() => page.Mode.ShowSectionAsync(EditingSection.History)));

        page.Panel.WaitForAssertion(() => Assert.Empty(page.Panel.FindAll("#tl-batch-section")));
        await Task.Delay(100);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "tuvimaFocusReturn.restore");
    }

    [Fact]
    public async Task MoveSection_OnOpening_MovesFocusIntoItAndOnClosingReturnsIt()
    {
        var page = await StartAsync();

        MenuItem(page.Panel, "Move…").Click();
        page.Panel.WaitForElement(".tl-move-picker");

        page.Panel.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "tuvimaFocusById" && Equals(call.Arguments[0], "tl-batch-move-section")));

        await page.Panel.InvokeAsync(page.Mode.CloseSectionAsync);

        page.Panel.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call => call.Identifier == "tuvimaFocusReturn.restore"));
    }

    // -- The old batch path is gone ------------------------------------------------------------

    [Fact]
    public void TheOldSingleValueBatchConversionAndTheEditorsBatchModeAreRemoved()
    {
        var root = FindRepoRoot();
        var client = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Integration", "EngineApiClient.LibraryOperations.cs"));
        var shell = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor.cs"));
        var models = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorModels.cs"));
        var launcher = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Services", "Editing", "MediaEditorLauncherService.cs"));

        Assert.DoesNotContain("BatchEditAsync", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ToBatchFieldChange", client, StringComparison.Ordinal);
        Assert.DoesNotContain("IsBatchMode", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadBatchAsync", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveBatchFields", models, StringComparison.Ordinal);
        Assert.DoesNotContain("SupportsBatch", models, StringComparison.Ordinal);
        // The modal editor still refuses several items at once: a batch is edited together on the details page.
        Assert.Contains("request.Mode == SharedMediaEditorMode.Batch || request.EntityIds.Count > 1", launcher, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
