using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
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
/// Editing several ticked rows together (universal editor spec D14): the selection bar, the "Edit N items" panel with "Mixed, leave
/// unchanged", only changed fields staged (one entry per item and field), Renumber sequentially and Change album, and the list host that
/// keeps filters, sort and ticks while it saves. The Engine is a hand-written stub; nothing is changed anywhere.
/// </summary>
public sealed class BulkEditingTests : AsyncBunitContext
{
    private static readonly Guid Album = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid SongOne = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid SongTwo = Guid.Parse("50000000-0000-0000-0000-000000000002");
    private static readonly Guid SongThree = Guid.Parse("50000000-0000-0000-0000-000000000003");
    private static readonly Guid SongFour = Guid.Parse("50000000-0000-0000-0000-000000000004");
    private static readonly Guid EpisodeOne = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid EpisodeTwo = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly Guid Season = Guid.Parse("c1000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<EditSessionRequest> _sessions = [];
    private readonly List<IReadOnlyList<Guid>> _deletionPreviews = [];
    private int _previewReads;

    // What each song holds before anything is edited: the four songs disagree on genres and share one year.
    private readonly Dictionary<Guid, (string Year, string[] Genres)> _songs = new()
    {
        [SongOne] = ("2001", ["Rock"]),
        [SongTwo] = ("2001", ["Rock", "Pop"]),
        [SongThree] = ("2001", ["Jazz"]),
        [SongFour] = ("2001", []),
    };

    public BulkEditingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), args =>
            {
                _previewReads++;
                var id = ((LibraryBatchEditPreviewRequest)args![0]!).EntityIds.Single();
                return Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(PreviewOf(id)));
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                _sessions.Add(request);
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
                {
                    PreviewOnly = request.PreviewOnly,
                    Valid = true,
                    DataSaved = !request.PreviewOnly,
                    Changes = [.. request.Changes.Select(change => new EditSessionChangeResult
                    {
                        Id = change.Id,
                        Kind = change.Kind,
                        EntityId = change.EntityId,
                        Status = EditSessionChangeStatuses.Ok,
                    })],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryMoveDestinationsAsync), _ =>
                Task.FromResult(LibraryMoveOutcome<LibraryMoveDestinationsResponse>.Ok(new LibraryMoveDestinationsResponse
                {
                    Destinations = [LibraryMoveTestData.Album()],
                })));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), args =>
            {
                var ids = (IReadOnlyList<Guid>)args![0]!;
                _deletionPreviews.Add(ids);
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [.. ids.Select(id => new LibraryItemDeletionItemDto { WorkId = id, Title = id.ToString(), MediaType = "Music", FileCount = 1 })],
                    Files = [],
                    CuratedCollections = [],
                    Fingerprint = "fp-bulk",
                }));
            });
        }));
    }

    // -- Fixtures ------------------------------------------------------------------------------

    private LibraryBatchEditPreview PreviewOf(Guid id)
    {
        var (year, genres) = _songs.GetValueOrDefault(id, ("2001", []));
        return new LibraryBatchEditPreview
        {
            AffectedCount = 1,
            Fields =
            [
                new LibraryBatchFieldState
                {
                    Key = "genre",
                    Label = "Genres",
                    Kind = LibraryBatchFieldKinds.List,
                    Operations = [LibraryBatchEditOps.Add, LibraryBatchEditOps.Replace, LibraryBatchEditOps.Remove],
                    Editable = true,
                    Values = [.. genres],
                },
                new LibraryBatchFieldState
                {
                    Key = "year",
                    Label = "Year",
                    Kind = LibraryBatchFieldKinds.Scalar,
                    Operations = [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear],
                    Editable = true,
                    Value = year,
                },
                new LibraryBatchFieldState
                {
                    Key = "director",
                    Label = "Directors",
                    Kind = LibraryBatchFieldKinds.List,
                    Operations = [],
                    Editable = false,
                    LockedItems = [new LibraryBatchLockedItem { EntityId = id, Reason = "Credits come from the catalogue and cannot be edited." }],
                },
            ],
        };
    }

    private static BulkEditRow Song(Guid id, string title, Guid? album = null) =>
        new(id, title, DetailEntityType.Work, album ?? Album, "Music");

    private static BulkEditRow Episode(Guid id, string title) =>
        new(id, title, DetailEntityType.TvEpisode, Season, "TV");

    private static IReadOnlyList<BulkEditRow> FourSongs() =>
    [
        Song(SongOne, "Airbag"),
        Song(SongTwo, "Paranoid Android"),
        Song(SongThree, "Subterranean Homesick Alien"),
        Song(SongFour, "Exit Music"),
    ];

    private static BulkEditSelection Ticked(IEnumerable<BulkEditRow> rows, string mediaType = "Music")
    {
        var selection = new BulkEditSelection { MediaType = mediaType };
        selection.Replace(rows);
        return selection;
    }

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

    private async Task<IRenderedComponent<BulkSelectionBar>> RenderBarAsync(BulkEditSelection selection, string formFactor = "desktop")
    {
        await UseDevice(formFactor);
        return Render<BulkSelectionBar>(parameters => parameters.Add(component => component.Selection, selection));
    }

    // The panel with the fields loaded: the four songs are read one by one, so wait for the form.
    private async Task<(IRenderedComponent<BulkEditPanel> Panel, PendingEditList Pending, BulkEditSelection Selection)> OpenPanelAsync(
        IEnumerable<BulkEditRow>? rows = null, BulkPanelKind kind = BulkPanelKind.Edit, string mediaType = "Music")
    {
        await UseDevice("desktop");
        var selection = Ticked(rows ?? FourSongs(), mediaType);
        selection.OpenPanel(kind);
        var pending = new PendingEditList();
        var panel = Render<BulkEditPanel>(parameters => parameters
            .Add(component => component.Selection, selection)
            .Add(component => component.Pending, pending));
        if (kind == BulkPanelKind.Edit)
        {
            panel.WaitForElement("[data-field-key='year']");
        }

        return (panel, pending, selection);
    }

    private static IElement Button(IRenderedComponent<BulkSelectionBar> bar, string label) =>
        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == label);

    private static bool HasButton(IRenderedComponent<BulkSelectionBar> bar, string label) =>
        bar.FindAll(".tl-batch__bar button").Any(button => button.TextContent.Trim() == label);

    private static string ItemLabel(IElement item) => Regex.Replace(item.TextContent, "\\s+", " ").Trim();

    private static IReadOnlyList<IElement> OpenBarMenu(IRenderedComponent<BulkSelectionBar> bar)
    {
        bar.Find(".tl-batch__bar button[aria-label='More actions']").Click();
        bar.WaitForAssertion(() => Assert.NotEmpty(bar.FindAll("[role='menuitem']")));
        return bar.FindAll("[role='menuitem']");
    }

    private static void TypeScalar(IRenderedComponent<Microsoft.AspNetCore.Components.IComponent> panel, string key, string text) =>
        panel.Find($"[data-field-key='{key}'] input").Input(text);

    private static void AddListValue(IRenderedComponent<BulkEditPanel> panel, string key, string value)
    {
        panel.Find($"[data-field-key='{key}'] input[aria-label='Tag to add']").Input(value);
        panel.Find($"[data-field-key='{key}'] form").Submit();
    }

    private static string? FieldValue(PendingEditList pending, Guid id, string key) =>
        pending.TryGetField(id, key, out var value) ? value : null;

    // -- Tick boxes ----------------------------------------------------------------------------

    private async Task<(EditingModeContext Mode, BulkEditSelection Selection)> EditingModeAsync(string formFactor = "desktop")
    {
        await UseDevice(formFactor);
        var session = new ItemFieldEditSession(Services.GetRequiredService<IEngineApiClient>(), _device);
        var mode = new EditingModeContext(_device, session) { HasPermission = true };
        if (formFactor == "desktop")
        {
            Assert.True(await mode.EnterAsync());
        }

        return (mode, new BulkEditSelection { MediaType = "TV" });
    }

    private IRenderedComponent<DetailRowSelectBox> RenderBox(EditingModeContext mode, BulkEditSelection selection, Guid id, string title, DetailEntityType kind) =>
        Render<DetailRowSelectBox>(parameters => parameters
            .AddCascadingValue(mode)
            .AddCascadingValue(selection)
            .Add(component => component.ItemId, id)
            .Add(component => component.Title, title)
            .Add(component => component.Kind, kind));

    [Fact]
    public async Task TickBox_InEditingModeOnADesktop_IsLabelledWithTheRowTitle()
    {
        var (mode, selection) = await EditingModeAsync();

        var box = RenderBox(mode, selection, EpisodeOne, "Pilot", DetailEntityType.TvEpisode);

        Assert.Equal("Select Pilot", box.Find("input[type=checkbox]").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task TickBox_OutsideEditingMode_DoesNotRender()
    {
        var (mode, selection) = await EditingModeAsync();
        mode.Leave();

        var box = RenderBox(mode, selection, EpisodeOne, "Pilot", DetailEntityType.TvEpisode);

        Assert.Empty(box.FindAll("input"));
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task TickBox_OnADeviceThatMayNotEdit_DoesNotRender(string formFactor)
    {
        var (mode, selection) = await EditingModeAsync(formFactor);

        var box = RenderBox(mode, selection, EpisodeOne, "Pilot", DetailEntityType.TvEpisode);

        Assert.Empty(box.FindAll("input"));
    }

    [Fact]
    public async Task TickBox_ForASeasonRow_DoesNotRender()
    {
        var (mode, selection) = await EditingModeAsync();

        var box = RenderBox(mode, selection, Guid.NewGuid(), "Season 1", DetailEntityType.TvSeason);

        Assert.Empty(box.FindAll("input"));
    }

    [Fact]
    public async Task Ticking_ARowOnTheDetailPage_AddsItWithItsAlbumToTheSelection()
    {
        var (mode, selection) = await EditingModeAsync();
        var box = Render<DetailRowSelectBox>(parameters => parameters
            .AddCascadingValue(mode)
            .AddCascadingValue(selection)
            .Add(component => component.ItemId, SongOne)
            .Add(component => component.Title, "Airbag")
            .Add(component => component.Kind, DetailEntityType.Work)
            .Add(component => component.ParentId, Album)
            .Add(component => component.MediaType, "Music"));

        box.Find("input").Change(true);

        var row = Assert.Single(selection.Rows);
        Assert.Equal(new BulkEditRow(SongOne, "Airbag", DetailEntityType.Work, Album, "Music"), row);
    }

    // -- The selection bar ---------------------------------------------------------------------

    [Fact]
    public async Task Bar_AppearsWhenRowsAreTickedAndDisappearsWhenTheyAreCleared()
    {
        var selection = new BulkEditSelection { MediaType = "Music" };
        var bar = await RenderBarAsync(selection);
        Assert.Empty(bar.FindAll(".tl-batch__bar"));

        selection.Replace(FourSongs());
        bar.WaitForAssertion(() => Assert.Equal("4 selected", bar.Find(".tl-batch__count").TextContent.Trim()));

        Button(bar, "Clear").Click();

        bar.WaitForAssertion(() => Assert.Empty(bar.FindAll(".tl-batch__bar")));
        Assert.Equal(0, selection.Count);
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task Bar_OnADeviceThatMayNotEdit_IsHiddenEvenWithRowsTicked(string formFactor)
    {
        var bar = await RenderBarAsync(Ticked(FourSongs()), formFactor);

        Assert.Empty(bar.FindAll(".tl-batch__bar"));
    }

    [Fact]
    public async Task Bar_ForSongsOfOneAlbum_OffersEditMatchAndMoveToAlbumAndClear()
    {
        var bar = await RenderBarAsync(Ticked(FourSongs()));

        var buttons = bar.FindAll(".tl-batch__bar button:not([hidden])").Select(button => button.TextContent.Trim()).ToList();

        Assert.Equal(["Edit", "Match", "Move to album", "Clear"], buttons);
        Assert.Empty(bar.FindAll(".tl-batch__bar button[aria-label='More actions']"));
    }

    [Fact]
    public async Task Bar_ForEpisodesOfOneSeason_OffersMoveToSeason()
    {
        var bar = await RenderBarAsync(Ticked([Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second")], "TV"));

        Assert.True(HasButton(bar, "Move to season"));
        Assert.True(HasButton(bar, "Match"));
    }

    [Fact]
    public async Task Bar_ForSongsOfDifferentAlbums_HidesMatchButKeepsEditAndMove()
    {
        var bar = await RenderBarAsync(Ticked([Song(SongOne, "Airbag"), Song(SongTwo, "Lucky", Guid.NewGuid())]));

        Assert.False(HasButton(bar, "Match"));
        Assert.True(HasButton(bar, "Edit"));
        Assert.True(HasButton(bar, "Move to album"));
    }

    [Fact]
    public async Task Bar_ForRowsOfDifferentKinds_HidesMatchAndMove()
    {
        var bar = await RenderBarAsync(Ticked([Episode(EpisodeOne, "Pilot"), Song(SongOne, "Airbag")]));

        Assert.True(HasButton(bar, "Edit"));
        Assert.False(HasButton(bar, "Match"));
        Assert.DoesNotContain(bar.FindAll(".tl-batch__bar button"), button => button.TextContent.Contains("Move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PressingEdit_OpensThePanelAndPressingItAgainClosesIt()
    {
        var selection = Ticked(FourSongs());
        var bar = await RenderBarAsync(selection);

        Button(bar, "Edit").Click();
        Assert.Equal(BulkPanelKind.Edit, selection.Panel);

        Button(bar, "Edit").Click();

        Assert.Equal(BulkPanelKind.None, selection.Panel);
        Assert.Equal(4, selection.Count);
    }

    [Fact]
    public async Task Delete_ForAnAdministrator_OpensOneDialogForEveryTickedRow()
    {
        Services.AddSingleton(Administrator());
        var host = Render<AppDialogHost>();
        var bar = await RenderBarAsync(Ticked(FourSongs()));

        var menu = OpenBarMenu(bar);
        Assert.Equal(["Delete from library…"], menu.Select(ItemLabel));
        menu.Single().Click();
        host.WaitForElement(".delete-library");

        var ids = Assert.Single(_deletionPreviews);
        Assert.Equal([SongOne, SongTwo, SongThree, SongFour], ids);
    }

    [Fact]
    public async Task Delete_ForSomeoneWhoMayNotDelete_IsNotOffered()
    {
        var bar = await RenderBarAsync(Ticked(FourSongs()));

        Assert.Empty(bar.FindAll(".tl-batch__bar button[aria-label='More actions']"));
    }

    [Fact]
    public async Task Delete_AboveTwoHundred_IsOffWithItsReasonAndDoesNotOpenTheDialog()
    {
        Services.AddSingleton(Administrator());
        var host = Render<AppDialogHost>();
        var rows = Enumerable.Range(1, 201).Select(index => Song(Guid.Parse($"e1000000-0000-0000-0000-{index:D12}"), $"Song {index}")).ToList();
        var bar = await RenderBarAsync(Ticked(rows));

        var delete = OpenBarMenu(bar).Single();

        Assert.Contains(BulkEditRules.DeleteLimitReason, ItemLabel(delete), StringComparison.Ordinal);
        delete.Click();
        Assert.Empty(host.FindAll(".delete-library"));
        Assert.Empty(_deletionPreviews);
    }

    // -- Rules ---------------------------------------------------------------------------------

    [Fact]
    public void Rules_NameTheRowsAndTheirMove()
    {
        Assert.Equal("Edit 4 songs", BulkEditRules.EditTitle(FourSongs(), "Music"));
        Assert.Equal("Edit 1 song", BulkEditRules.EditTitle([Song(SongOne, "Airbag")], "Music"));
        Assert.Equal("Edit 2 episodes", BulkEditRules.EditTitle([Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second")], "TV"));
        Assert.Equal("Move to album", BulkEditRules.MoveLabel(FourSongs(), "Music"));
        Assert.Equal("Move to season", BulkEditRules.MoveLabel([Episode(EpisodeOne, "Pilot")], "TV"));
    }

    [Fact]
    public void Rules_RenumberNeedsSongsOfOneAlbumAndMatchNeedsOneAlbumOrSeason()
    {
        Assert.True(BulkEditRules.CanRenumber(FourSongs(), "Music"));
        Assert.True(BulkEditRules.CanChangeAlbum(FourSongs(), "Music"));
        Assert.False(BulkEditRules.CanRenumber([Song(SongOne, "A"), Song(SongTwo, "B", Guid.NewGuid())], "Music"));
        Assert.False(BulkEditRules.CanRenumber([new BulkEditRow(SongOne, "A", DetailEntityType.Work, null, "Music")], "Music"));
        Assert.False(BulkEditRules.CanMatch([new BulkEditRow(EpisodeOne, "Pilot", DetailEntityType.TvEpisode)]));
        Assert.True(BulkEditRules.CanMatch([Episode(EpisodeOne, "Pilot")]));
    }

    // -- Mixed detection -----------------------------------------------------------------------

    [Fact]
    public void Build_MarksAFieldMixedWhenTheItemsDisagreeAndSharedWhenTheyAgree()
    {
        var reads = new[] { SongOne, SongTwo, SongThree, SongFour }
            .ToDictionary(id => id, id => (IReadOnlyList<LibraryBatchFieldState>)PreviewOf(id).Fields);

        var views = BulkEditFields.Build([SongOne, SongTwo, SongThree, SongFour], reads);

        var genre = views.Single(view => view.Key == "genre");
        Assert.True(genre.IsMixed);
        Assert.Equal("Rock (2), Jazz (1), Pop (1)", genre.MixedHint());
        var year = views.Single(view => view.Key == "year");
        Assert.False(year.IsMixed);
        Assert.Equal("2001", year.SharedText);
        Assert.True(views.Single(view => view.Key == "director").IsProviderFact);
    }

    [Fact]
    public async Task Panel_SaysMixedLeaveUnchangedOnADifferingFieldAndShowsTheSharedValueOtherwise()
    {
        var (panel, _, _) = await OpenPanelAsync();

        Assert.Contains(BulkEditRules.MixedLeaveUnchanged, panel.Find("[data-field-key='genre']").TextContent, StringComparison.Ordinal);
        Assert.Contains("Same on every item: 2001", panel.Find("[data-field-key='year']").TextContent, StringComparison.Ordinal);
        Assert.Equal("2001", panel.Find("[data-field-key='year'] input").GetAttribute("value"));
        Assert.Contains("cannot be edited", panel.Find("[data-field-key='director']").InnerHtml, StringComparison.Ordinal);
    }

    // -- Staging: only what changed ------------------------------------------------------------

    [Fact]
    public async Task Panel_StagesOnlyTheFieldThatWasChanged_OneEntryPerItem()
    {
        var (panel, pending, _) = await OpenPanelAsync();

        TypeScalar(panel, "year", "2021");

        panel.WaitForAssertion(() => Assert.Equal("2021", FieldValue(pending, SongOne, "year")));
        foreach (var id in new[] { SongOne, SongTwo, SongThree, SongFour })
        {
            Assert.Equal("2021", FieldValue(pending, id, "year"));
            Assert.Null(FieldValue(pending, id, "genre"));
            Assert.Equal(1, pending.CountForItem(id));
        }

        Assert.Equal(4, pending.ChangeCount);
    }

    [Fact]
    public async Task Panel_TypingTheValueEveryItemAlreadyHolds_StagesNothing()
    {
        var (panel, pending, _) = await OpenPanelAsync();

        TypeScalar(panel, "year", "2021");
        panel.WaitForAssertion(() => Assert.False(pending.IsEmpty));
        TypeScalar(panel, "year", "2001");

        panel.WaitForAssertion(() => Assert.True(pending.IsEmpty));
    }

    [Fact]
    public async Task Panel_AddingAGenre_StagesEachItemsOwnListWithTheGenreAdded()
    {
        var (panel, pending, _) = await OpenPanelAsync();

        AddListValue(panel, "genre", "Alternative");

        panel.WaitForAssertion(() => Assert.NotNull(FieldValue(pending, SongOne, "genre")));
        Assert.Equal("Rock, Alternative", FieldValue(pending, SongOne, "genre"));
        Assert.Equal("Rock, Pop, Alternative", FieldValue(pending, SongTwo, "genre"));
        Assert.Equal("Jazz, Alternative", FieldValue(pending, SongThree, "genre"));
        Assert.Equal("Alternative", FieldValue(pending, SongFour, "genre"));
        Assert.Null(FieldValue(pending, SongOne, "year"));
    }

    [Fact]
    public async Task Panel_AddingAGenreAnItemAlreadyHas_LeavesThatItemWithoutAnEntry()
    {
        var (panel, pending, _) = await OpenPanelAsync();

        AddListValue(panel, "genre", "Rock");

        panel.WaitForAssertion(() => Assert.NotNull(FieldValue(pending, SongThree, "genre")));
        Assert.Null(FieldValue(pending, SongOne, "genre"));
        Assert.Null(FieldValue(pending, SongTwo, "genre"));
        Assert.Equal("Jazz, Rock", FieldValue(pending, SongThree, "genre"));
    }

    [Fact]
    public async Task Panel_UndoingAField_TakesItsStagedValuesBackOut()
    {
        var (panel, pending, _) = await OpenPanelAsync();
        TypeScalar(panel, "year", "2021");
        panel.WaitForAssertion(() => Assert.False(pending.IsEmpty));

        panel.Find("[data-field-key='year'] .tl-editor-field__reset").Click();

        panel.WaitForAssertion(() => Assert.True(pending.IsEmpty));
    }

    [Fact]
    public void StageScalar_OnAMixedFieldLeftEmpty_StagesNothing()
    {
        var pending = new PendingEditList();
        var reads = new[] { SongOne, SongTwo, SongThree, SongFour }
            .ToDictionary(id => id, id => (IReadOnlyList<LibraryBatchFieldState>)PreviewOf(id).Fields);
        var genre = BulkEditFields.Build([SongOne, SongTwo, SongThree, SongFour], reads).Single(view => view.Key == "genre");

        var result = BulkEditStager.StageList(pending, genre, LibraryBatchEditOps.Replace, []);

        Assert.Equal(0, result.Staged);
        Assert.True(pending.IsEmpty);
    }

    [Fact]
    public void StageList_ReplacingWithNothingNeverEmptiesAGenreList()
    {
        var pending = new PendingEditList();
        var reads = new[] { SongOne, SongTwo }
            .ToDictionary(id => id, id => (IReadOnlyList<LibraryBatchFieldState>)PreviewOf(id).Fields);
        var genre = BulkEditFields.Build([SongOne, SongTwo], reads).Single(view => view.Key == "genre");

        var result = BulkEditStager.StageList(pending, genre, LibraryBatchEditOps.Remove, ["Rock", "Pop"]);

        Assert.Equal(0, result.Staged);
        Assert.Equal(2, result.Skipped);
        Assert.Contains("can't be left empty", result.Note, StringComparison.Ordinal);
        Assert.True(pending.IsEmpty);
    }

    // -- Renumber sequentially and Change album ------------------------------------------------

    [Fact]
    public void Renumber_StagesOneToNInTheOrderTheListShowsNow()
    {
        var selection = Ticked(FourSongs());
        // The list is sorted by title right now, so the order differs from the tick order.
        selection.ListOrder = () => [SongTwo, SongOne, SongFour, SongThree];
        var pending = new PendingEditList();

        var result = BulkEditStager.Renumber(pending, selection.OrderedRows());

        Assert.Equal(4, result.Staged);
        var tokens = pending.Changes.Where(change => change.Kind == EditSessionChangeKinds.Relationship)
            .ToDictionary(change => change.EntityId, change => change.Token);
        Assert.Equal(EditSessionPosition.Token(1), tokens[SongTwo]);
        Assert.Equal(EditSessionPosition.Token(2), tokens[SongOne]);
        Assert.Equal(EditSessionPosition.Token(3), tokens[SongFour]);
        Assert.Equal(EditSessionPosition.Token(4), tokens[SongThree]);
        Assert.All(pending.Changes, change => Assert.Equal(Album.ToString("D"), change.TargetId));
    }

    [Fact]
    public async Task Panel_RenumberSequentially_StagesTheNumbersAndSaysSo()
    {
        var (panel, pending, _) = await OpenPanelAsync();

        panel.Find(".tl-bulk__renumber").Click();

        panel.WaitForAssertion(() => Assert.Equal(4, pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Relationship)));
        Assert.Equal(EditSessionPosition.Token(1), pending.Changes.Single(change => change.EntityId == SongOne).Token);
        Assert.Contains("4 will be renumbered", panel.Find(".tl-bulk__staged").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Panel_ForEpisodes_OffersNoRenumberAndNoChangeAlbum()
    {
        var (panel, _, _) = await OpenPanelAsync([Episode(EpisodeOne, "Pilot"), Episode(EpisodeTwo, "Second")], mediaType: "TV");

        Assert.Empty(panel.FindAll(".tl-bulk__renumber"));
        Assert.Empty(panel.FindAll(".tl-bulk__change-album"));
    }

    [Fact]
    public async Task Panel_ChangeAlbum_StagesARelationshipToThePickedAlbumForEverySong()
    {
        var (panel, pending, selection) = await OpenPanelAsync();

        panel.Find(".tl-bulk__change-album").Click();
        panel.WaitForElement($"[data-destination-id='{LibraryMoveTestData.AlbumId}']").Click();

        panel.WaitForAssertion(() => Assert.Equal(BulkPanelKind.Edit, selection.Panel));
        var moves = pending.Changes.Where(change => change.Kind == EditSessionChangeKinds.Relationship).ToList();
        Assert.Equal(4, moves.Count);
        Assert.All(moves, change =>
        {
            Assert.Equal(LibraryMoveTestData.AlbumId.ToString("D"), change.TargetId);
            Assert.False(EditSessionPosition.IsPosition(change.Token, out _));
        });
    }

    [Fact]
    public void ChangeAlbum_ReplacesANumberStagedForTheSameSong()
    {
        var pending = new PendingEditList();
        var rows = FourSongs();
        BulkEditStager.Renumber(pending, rows);

        BulkEditStager.MoveTo(pending, rows, LibraryMoveTestData.AlbumId);

        var changes = pending.Changes.Where(change => change.Kind == EditSessionChangeKinds.Relationship).ToList();
        Assert.Equal(4, changes.Count);
        Assert.All(changes, change => Assert.False(EditSessionPosition.IsPosition(change.Token, out _)));
    }

    [Fact]
    public void Match_StagesEachRowAgainstTheAlbumItAlreadySitsIn()
    {
        var pending = new PendingEditList();

        var result = BulkEditStager.MatchInPlace(pending, FourSongs());

        Assert.Equal(4, result.Staged);
        Assert.All(pending.Changes, change => Assert.Equal(Album.ToString("D"), change.TargetId));
    }

    // -- Four songs in one pass ----------------------------------------------------------------

    [Fact]
    public async Task EditingFourSongsInOnePass_StagesTheYearTheGenreAndTheNumbersInOnePendingList()
    {
        var (panel, pending, _) = await OpenPanelAsync();

        TypeScalar(panel, "year", "1997");
        AddListValue(panel, "genre", "Alternative");
        panel.Find(".tl-bulk__renumber").Click();

        panel.WaitForAssertion(() => Assert.Equal(12, pending.ChangeCount));
        foreach (var id in new[] { SongOne, SongTwo, SongThree, SongFour })
        {
            Assert.Equal("1997", FieldValue(pending, id, "year"));
            Assert.NotNull(FieldValue(pending, id, "genre"));
            Assert.Equal(2, pending.CountForItem(id));
        }

        Assert.Equal(4, pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Relationship));
        Assert.Equal(4, pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Details));
    }

    // -- The list host: filters, sort and ticks stay -------------------------------------------

    [Fact]
    public async Task Host_SavesTheStagedChangesWithOneCheckAndOneSave_ThenAsksTheListToReloadAndKeepsTheTicks()
    {
        await UseDevice("desktop");
        var dialogs = Render<AppDialogHost>();
        var saved = 0;
        var cleared = 0;
        var rows = FourSongs();
        var host = Render<BulkEditHost>(parameters => parameters
            .Add(component => component.Rows, rows)
            .Add(component => component.MediaType, "Music")
            .Add(component => component.OnSaved, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => saved++))
            .Add(component => component.OnCleared, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => cleared++)));
        host.Find(".tl-batch__edit").Click();
        host.WaitForElement("[data-field-key='year']");
        Assert.Equal("Edit 4 songs", host.Find(".tl-editor-panel__title").TextContent.Trim());
        TypeScalar(host, "year", "2021");
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".tl-batch__pending")));

        // Save changes opens Review changes, the same dialog the editing bar opens; nothing is saved until its own Save.
        host.Find(".tl-batch__save").Click();
        dialogs.WaitForElement(".review-changes");
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".review-changes__state")));
        Assert.Equal(0, saved);
        Assert.Equal([true], _sessions.Select(request => request.PreviewOnly));
        dialogs.Find(".app-dialog-shell__footer button:last-child").Click();

        host.WaitForAssertion(() => Assert.Equal(1, saved));
        Assert.Equal([true, true, false], _sessions.Select(request => request.PreviewOnly));
        Assert.Equal(4, _sessions[2].Changes.Count);
        Assert.Empty(host.FindAll(".tl-batch__pending"));
        Assert.Equal("4 selected", host.Find(".tl-batch__count").TextContent.Trim());
        Assert.NotEmpty(host.FindAll(".tl-editor-panel"));
        Assert.Equal(0, cleared);
    }

    [Fact]
    public async Task Host_RereadsTheSavedValuesAfterASave()
    {
        await UseDevice("desktop");
        var dialogs = Render<AppDialogHost>();
        var host = Render<BulkEditHost>(parameters => parameters
            .Add(component => component.Rows, FourSongs())
            .Add(component => component.MediaType, "Music"));
        host.Find(".tl-batch__edit").Click();
        host.WaitForElement("[data-field-key='year']");
        var readsBefore = _previewReads;
        TypeScalar(host, "year", "2021");
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".tl-batch__save")));

        host.Find(".tl-batch__save").Click();
        dialogs.WaitForElement(".review-changes");
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".review-changes__state")));
        dialogs.Find(".app-dialog-shell__footer button:last-child").Click();

        host.WaitForAssertion(() => Assert.True(_previewReads >= readsBefore + 4));
    }

    [Fact]
    public async Task Host_Cancel_ThrowsTheStagedChangesAwayButKeepsTheTicks()
    {
        await UseDevice("desktop");
        var dialogs = Render<AppDialogHost>();
        var host = Render<BulkEditHost>(parameters => parameters
            .Add(component => component.Rows, FourSongs())
            .Add(component => component.MediaType, "Music"));
        host.Find(".tl-batch__edit").Click();
        host.WaitForElement("[data-field-key='year']");
        TypeScalar(host, "year", "2021");
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".tl-batch__cancel")));

        host.Find(".tl-batch__cancel").Click();
        dialogs.WaitForElement(".app-confirm");
        dialogs.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == EditingUnsavedChangesDialog.DiscardLabel).Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".tl-batch__pending")));
        Assert.Empty(_sessions);
        Assert.Equal("4 selected", host.Find(".tl-batch__count").TextContent.Trim());
    }

    [Theory]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task Host_OnADeviceThatMayNotEdit_RendersNothing(string formFactor)
    {
        await UseDevice(formFactor);

        var host = Render<BulkEditHost>(parameters => parameters
            .Add(component => component.Rows, FourSongs())
            .Add(component => component.MediaType, "Music"));

        Assert.Empty(host.FindAll(".tl-batch"));
        Assert.Empty(host.FindAll(".tl-editor-panel"));
        Assert.Empty(host.FindAll(".tl-batch__pending"));
    }

    [Fact]
    public async Task Host_WhenTheListUnticksEverything_RemovesTheBarAndClosesThePanel()
    {
        await UseDevice("desktop");
        var host = Render<BulkEditHost>(parameters => parameters
            .Add(component => component.Rows, FourSongs())
            .Add(component => component.MediaType, "Music"));
        host.Find(".tl-batch__edit").Click();
        host.WaitForElement(".tl-editor-panel");

        host.Render(parameters => parameters
            .Add(component => component.Rows, Array.Empty<BulkEditRow>())
            .Add(component => component.MediaType, "Music"));

        host.WaitForAssertion(() =>
        {
            Assert.Empty(host.FindAll(".tl-batch__bar"));
            Assert.Empty(host.FindAll(".tl-editor-panel"));
        });
    }

    [Fact]
    public async Task Selection_KeepsTheTicksWhenThePanelIsClosed()
    {
        var selection = Ticked(FourSongs());
        selection.OpenPanel(BulkPanelKind.Edit);

        selection.ClosePanel();

        Assert.Equal(BulkPanelKind.None, selection.Panel);
        Assert.Equal(4, selection.Count);
        await Task.CompletedTask;
    }
}
