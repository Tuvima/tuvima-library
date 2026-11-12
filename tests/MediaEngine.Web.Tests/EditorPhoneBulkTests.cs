using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Editing several items in the phone window (universal editor v6 WP17, D19 to D22): the same Details fields as on a computer, with lock reasons
/// written out (a finger has no hover), the group Match's picked show stacked so it fits, a plain error with Try again when the fields cannot be
/// read, and the selection bar laid out for a phone. The window's arrangement comes from the cascaded layout; who may edit is still the device rule,
/// so these walks use a device that may edit. Nothing is sent to the Engine.
/// </summary>
public sealed class EditorPhoneBulkTests : AsyncBunitContext
{
    private static readonly Guid Album = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid[] Songs =
    [
        Guid.Parse("50000000-0000-0000-0000-000000000001"),
        Guid.Parse("50000000-0000-0000-0000-000000000002"),
        Guid.Parse("50000000-0000-0000-0000-000000000003"),
    ];

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private bool _failReads;
    private int _reads;

    public EditorPhoneBulkTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), args =>
            {
                _reads++;
                if (_failReads)
                {
                    return Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Failed(LibraryBatchEditOutcomeStatus.Unavailable));
                }

                var id = ((LibraryBatchEditPreviewRequest)args![0]!).EntityIds.Single();
                return Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(PreviewOf(id)));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorMatchContextAsync), _ =>
                Task.FromResult<EditorMatchContextDto?>(new EditorMatchContextDto { ParsedShow = "The Last Signal", ParsedSeason = 2 }));
            stub.SetHandler(nameof(IEngineApiClient.SearchTvMatchBrowserShowsAsync), _ =>
                Task.FromResult<TvMatchBrowserShowsDto?>(new TvMatchBrowserShowsDto("The Last Signal",
                    [new TvMatchBrowserShowDto("100", "The Last Signal", "2019", null, null, string.Empty, false)])));
            stub.SetHandler(nameof(IEngineApiClient.GetTvMatchBrowserSeasonsAsync), _ =>
                Task.FromResult<TvMatchBrowserSeasonsDto?>(new TvMatchBrowserSeasonsDto("100", "The Last Signal",
                    [new TvMatchBrowserSeasonDto("s2", "100", 2, "Season 2", null, false)])));
            stub.SetHandler(nameof(IEngineApiClient.LineUpEditorGroupMatchAsync), _ =>
                Task.FromResult<EditorGroupMatchDto?>(new EditorGroupMatchDto
                {
                    Kind = EditorGroupMatchKinds.Episodes,
                    TargetLabel = "The Last Signal Season 2",
                    SubjectMediaType = "TV",
                    SubjectScopeId = "episode",
                    SubjectTargetGroup = "show_episode",
                    Lines = [.. Episodes.Select((id, index) => LineFor(id, index))],
                }));
        }));
    }

    private static readonly Guid[] Episodes = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    private static EditorGroupMatchLineDto LineFor(Guid id, int index) => new()
    {
        EntityId = id,
        Title = $"s02e0{index + 1}.mkv",
        NumberText = $"S02E0{index + 1}",
        TargetTitle = $"Episode {index + 1}",
        Verdict = EditorMatchVerdicts.Strong,
        Fits = true,
        Candidate = EditorMatchCandidates.ForTvEpisode("100", "The Last Signal", $"e{index + 1}", 2, index + 1, $"Episode {index + 1}", "2021-01-04", null, null, 45),
        Placement = new EditorMatchPlacementDto
        {
            DestinationId = Guid.NewGuid(),
            ParentLabel = "The Last Signal · Season 2",
            CurrentParentLabel = "Signal Lost · Season 2",
            Managed = true,
            FromPath = $"/tv/old/{index}.mkv",
            ToPath = $"/tv/new/{index}.mkv",
        },
    };

    private static LibraryBatchEditPreview PreviewOf(Guid id) => new()
    {
        AffectedCount = 1,
        Fields =
        [
            new LibraryBatchFieldState
            {
                Key = "year",
                Label = "Year",
                Kind = LibraryBatchFieldKinds.Scalar,
                Operations = [LibraryBatchEditOps.Set, LibraryBatchEditOps.Clear],
                Editable = true,
                Value = "2001",
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

    private static BulkEditSelection ThreeSongs()
    {
        var selection = new BulkEditSelection { MediaType = "Music" };
        selection.Replace(Songs.Select((id, index) => new BulkEditRow(id, $"Song {index + 1}", DetailEntityType.Work, Album, "Music")));
        selection.OpenPanel(BulkPanelKind.Edit);
        return selection;
    }

    private Task UseEditingDevice() => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private IRenderedComponent<BulkEditPanel> RenderPanel(BulkEditSelection selection, PendingEditList pending, bool phone) =>
        Render<BulkEditPanel>(parameters =>
        {
            parameters.Add(component => component.Selection, selection).Add(component => component.Pending, pending);
            if (phone)
            {
                parameters.AddCascadingValue(EditorWindowLayout.PhoneSheet);
            }
        });

    [Fact]
    public async Task ThreeSongs_AreGivenAYear_InThePhoneWindow_OneStagedChangeForEach()
    {
        await UseEditingDevice();
        var pending = new PendingEditList();
        var panel = RenderPanel(ThreeSongs(), pending, phone: true);
        panel.WaitForElement("[data-field-key='year']");

        Assert.Contains("tl-bulk--phone", panel.Find(".tl-bulk").ClassList);
        // The lock is a real button a thumb can press, and the field says in words whether it is locked.
        panel.UnlockField("year");
        panel.Find("[data-field-key='year'] input").Input("2005");

        foreach (var id in Songs)
        {
            Assert.True(pending.TryGetField(id, "year", out var value));
            Assert.Equal("2005", value);
        }
    }

    [Fact]
    public async Task AProviderFact_OnAPhone_WritesItsReasonOutInsteadOfHidingItInATooltip()
    {
        await UseEditingDevice();
        var panel = RenderPanel(ThreeSongs(), new PendingEditList(), phone: true);
        panel.WaitForElement("[data-field-key='director']");

        Assert.Equal("Credits come from the catalogue and cannot be edited.", panel.Find("[data-testid=bulk-fact-reason]").TextContent.Trim());
        Assert.Empty(panel.FindAll("[data-field-key='director'] [tabindex='0']"));
    }

    [Fact]
    public async Task AProviderFact_OnAComputer_KeepsItsTooltipAndShowsNoExtraLine()
    {
        await UseEditingDevice();
        var panel = RenderPanel(ThreeSongs(), new PendingEditList(), phone: false);
        panel.WaitForElement("[data-field-key='director']");

        Assert.Empty(panel.FindAll("[data-testid=bulk-fact-reason]"));
        Assert.DoesNotContain("tl-bulk--phone", panel.Find(".tl-bulk").ClassList);
    }

    [Fact]
    public async Task WhenTheFieldsCannotBeRead_APlainLineAndTryAgainShow_AndWhatWasStagedIsKept()
    {
        await UseEditingDevice();
        var pending = new PendingEditList();
        var selection = ThreeSongs();
        var panel = RenderPanel(selection, pending, phone: true);
        panel.WaitForElement("[data-field-key='year']");
        panel.UnlockField("year");
        panel.Find("[data-field-key='year'] input").Input("2005");
        Assert.True(pending.TryGetField(Songs[0], "year", out _));

        // The connection drops while one more song is ticked.
        _failReads = true;
        selection.Toggle(new BulkEditRow(Guid.Parse("50000000-0000-0000-0000-000000000004"), "Song 4", DetailEntityType.Work, Album, "Music"));
        panel.WaitForElement("[data-testid=bulk-fields-failed]");
        Assert.True(pending.TryGetField(Songs[0], "year", out var kept));
        Assert.Equal("2005", kept);

        _failReads = false;
        panel.Find("[data-testid=bulk-fields-retry]").Click();

        panel.WaitForAssertion(() => Assert.Empty(panel.FindAll("[data-testid=bulk-fields-failed]")));
        panel.WaitForElement("[data-field-key='year']");
        Assert.True(pending.TryGetField(Songs[0], "year", out kept));
        Assert.Equal("2005", kept);
    }

    [Fact]
    public async Task TheGroupMatch_OnAPhone_StacksThePickedShow_AndGivesThreeEpisodesOneMatchByTouch()
    {
        await UseEditingDevice();
        var pending = new PendingEditList();
        var cut = OpenGroupMatch(pending, phone: true);

        var picked = cut.WaitForElement("[data-testid=group-match-picked]");
        // The name and Change show stack, and no blank label line sits above the button.
        Assert.Contains("app-form-row--stack", picked.ClassList);
        Assert.Empty(picked.QuerySelectorAll("[data-form-row-field] .app-field__label"));

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=group-match-lineup]").HasAttribute("disabled")));
        cut.Find("[data-testid=group-match-lineup]").Click();
        cut.WaitForElement("[data-testid=group-match-summary]");
        cut.Find("[data-testid=group-match-apply]").Click();

        cut.WaitForAssertion(() => Assert.Equal(3, pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Identity)));
    }

    [Fact]
    public async Task TheGroupMatch_OnAComputer_KeepsItsPickedRowWithTheBlankLabelLine()
    {
        await UseEditingDevice();
        var cut = OpenGroupMatch(new PendingEditList(), phone: false);

        var picked = cut.WaitForElement("[data-testid=group-match-picked]");

        Assert.DoesNotContain("app-form-row--stack", picked.ClassList);
        Assert.Single(picked.QuerySelectorAll("[data-form-row-field] .app-field__label"));
    }

    // Searches for the show named in the file names and picks it, which leaves the panel on the picked-show row.
    private IRenderedComponent<EditorGroupMatchPanel> OpenGroupMatch(PendingEditList pending, bool phone)
    {
        IReadOnlyList<BulkEditRow> rows = [.. Episodes.Select((id, index) => new BulkEditRow(id, $"s02e0{index + 1}.mkv", DetailEntityType.TvEpisode, Guid.NewGuid(), "TV"))];
        var cut = Render<EditorGroupMatchPanel>(parameters =>
        {
            parameters.Add(panel => panel.Rows, rows)
                .Add(panel => panel.MediaType, "TV")
                .Add(panel => panel.Pending, pending)
                .Add(panel => panel.CanChange, true);
            if (phone)
            {
                parameters.AddCascadingValue(EditorWindowLayout.PhoneSheet);
            }
        });
        cut.WaitForAssertion(() => Assert.Equal("The Last Signal", cut.Find("input[aria-label='Show']").GetAttribute("value")));
        cut.Find("[data-testid=group-match-search]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=group-match-shows] button")));
        cut.Find("[data-testid=group-match-shows] button").Click();
        return cut;
    }

    [Fact]
    public async Task TheSelectionBar_OnATablet_GetsThePhoneLayout_AndOnAComputerDoesNot()
    {
        // A tablet shows ticked songs' own actions (Play, Add to queue, Add to playlist) without needing the right to edit, so the bar draws there.
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true));
        var selection = ThreeSongs();
        var tabletBar = Render<BulkSelectionBar>(parameters => parameters.Add(component => component.Selection, selection));
        Assert.Contains("tl-batch--phone", Assert.Single(tabletBar.FindAll(".tl-batch")).ClassList);

        // The device is shared, so the same bar follows it when the window becomes a computer's.
        await UseEditingDevice();
        tabletBar.WaitForAssertion(() => Assert.DoesNotContain("tl-batch--phone", Assert.Single(tabletBar.FindAll(".tl-batch")).ClassList));
    }
}
