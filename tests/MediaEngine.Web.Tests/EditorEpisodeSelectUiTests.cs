using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What "Select episodes" looks like (spec details-page-bottom D9): the entry at the foot of the header's Season (or Section) dropdown, and the
/// ticked list the window shows. The rules behind them are in <see cref="EditorEpisodeSelectTests"/>.
/// </summary>
public sealed class EditorEpisodeSelectUiTests : AsyncBunitContext
{
    private readonly List<EpisodeSelectRequest> _asked = [];

    public EditorEpisodeSelectUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), _ => Task.FromResult<EditorHeaderLevelDto?>(new EditorHeaderLevelDto
            {
                Items =
                [
                    EditorHeaderTestData.Episode(EditorHeaderTestData.EpisodeOneId, "S02E01", "Signal lost"),
                    EditorHeaderTestData.Episode(EditorHeaderTestData.EpisodeTwoId, "S02E02", "Static"),
                ],
            }));
            // The several-items panel reads each ticked row's fields once; these tests only look at the panel's frame.
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview { AffectedCount = 1 })));
        }));
        Render<AppPopoverHost>();
    }

    private EditorHeaderHooks Hooks(bool canSelect = true) => new()
    {
        CanSelectEpisodes = () => canSelect,
        OpenEpisodeSelect = request =>
        {
            _asked.Add(request);
            return Task.CompletedTask;
        },
    };

    private IRenderedComponent<EditorHeaderNav> Show(EditorHeaderDto header, EditorHeaderHooks? hooks)
    {
        var cut = Render<EditorHeaderNav>(parameters => parameters.Add(nav => nav.Header, header).Add(nav => nav.Hooks, hooks));
        cut.Find("#tl-hnav-second").Click();
        return cut;
    }

    // -- The entry in the Season dropdown -----------------------------------------------------------------------------------------

    [Fact]
    public void TheSeasonDropdown_EndsWithSelectEpisodes_WhenTheEditorMayEdit()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode(), Hooks());

        var entry = cut.Find("#tl-hnav-select-episodes");

        Assert.Contains("Select episodes", entry.TextContent, StringComparison.Ordinal);
        Assert.Equal("select-episodes", entry.GetAttribute("data-testid"));
    }

    [Fact]
    public void ASectionDropdown_SaysSelectVideos()
    {
        var cut = Show(EditorHeaderTestData.CourseLesson(), Hooks());

        Assert.Contains("Select videos", cut.Find("#tl-hnav-select-episodes").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheEditorSaysThisDeviceOrProfileMayNotEdit_NoSelectEpisodesEntryShows()
    {
        // A TV and a profile the Engine does not let edit both reach the dropdown as the editor's "no".
        var cut = Show(EditorHeaderTestData.TvEpisode(), Hooks(canSelect: false));

        Assert.Empty(cut.FindAll("#tl-hnav-select-episodes"));
        Assert.NotEmpty(cut.FindAll(".tl-hnav__option"));
    }

    [Fact]
    public void WithNoHooks_NoSelectEpisodesEntryShows()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode(), hooks: null);

        Assert.Empty(cut.FindAll("#tl-hnav-select-episodes"));
    }

    [Fact]
    public void ASeasonWithNoEpisodes_HasNoSelectEpisodesEntry()
    {
        var tv = EditorHeaderTestData.TvEpisode();
        var header = new EditorHeaderDto
        {
            Kind = tv.Kind,
            Root = tv.Root,
            CurrentId = tv.CurrentId,
            ShowHome = true,
            HomeId = tv.HomeId,
            SecondLevel = new EditorHeaderSecondLevelDto
            {
                Label = "Season",
                CurrentId = EditorHeaderTestData.SeasonOneId,
                Entries = [new EditorHeaderLevelEntryDto { Id = EditorHeaderTestData.SeasonOneId, Title = "Season 1", ItemCount = 0 }],
            },
            Level = tv.Level,
        };

        var cut = Show(header, Hooks());

        Assert.Empty(cut.FindAll("#tl-hnav-select-episodes"));
    }

    [Fact]
    public void ChoosingSelectEpisodes_AsksTheEditorForThatSeason_AndClosesTheDropdown()
    {
        var cut = Show(EditorHeaderTestData.TvEpisode(), Hooks());

        cut.Find("#tl-hnav-select-episodes").Click();

        var asked = Assert.Single(_asked);
        Assert.Equal(EditorHeaderTestData.SeasonTwoId, asked.LevelId);
        Assert.Equal("false", cut.Find("#tl-hnav-second").GetAttribute("aria-expanded"));
    }

    // -- The ticked list -----------------------------------------------------------------------------------------------------------

    private async Task<(EditorEpisodeSelect State, IRenderedComponent<EditorEpisodeSelectList> Cut, List<string> Calls)> ListAsync(
        bool canEdit = true, EditorWindowLayout? layout = null)
    {
        var state = new EditorEpisodeSelect(Services.GetRequiredService<IEngineApiClient>(), NullLogger.Instance);
        await state.OpenAsync(EditorEpisodeSelectRules.Offer(EditorHeaderTestData.TvEpisode())!);
        var calls = new List<string>();
        var cut = Render<EditorEpisodeSelectList>(parameters =>
        {
            parameters.Add(list => list.State, state)
                .Add(list => list.CanEdit, canEdit)
                .Add(list => list.OnEdit, () => calls.Add("edit"))
                .Add(list => list.OnBack, () => calls.Add("back"));
            if (layout is not null)
            {
                parameters.AddCascadingValue(layout);
            }
        });
        return (state, cut, calls);
    }

    [Fact]
    public async Task TheList_ShowsTheSeasonsEpisodesWithTheirCodes_AndNoneTickedAtFirst()
    {
        var (_, cut, _) = await ListAsync();

        Assert.Equal("Season 2", cut.Find("[data-testid=episode-select-heading]").TextContent.Trim());
        var rows = cut.FindAll("[data-testid=episode-select-row]");
        Assert.Equal(2, rows.Count);
        Assert.Contains("S02E01", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Signal lost", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("0 of 2 selected", cut.Find("[data-testid=episode-select-count]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=episode-select-edit]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task TickingARow_CountsIt_AndTurnsOnEditN()
    {
        var (state, cut, calls) = await ListAsync();

        cut.FindAll("[data-testid=episode-select-tick]")[1].Change(true);

        Assert.Equal(1, state.TickedCount);
        Assert.Contains("1 of 2 selected", cut.Find("[data-testid=episode-select-count]").TextContent, StringComparison.Ordinal);
        var edit = cut.Find("[data-testid=episode-select-edit]");
        Assert.False(edit.HasAttribute("disabled"));
        Assert.Contains("Edit 1 episode", edit.TextContent, StringComparison.Ordinal);
        edit.Click();
        Assert.Equal(["edit"], calls);
    }

    [Fact]
    public async Task SelectAll_TicksEveryRow_AndSaysClearAllNext()
    {
        var (state, cut, _) = await ListAsync();

        cut.Find("[data-testid=episode-select-all]").Click();

        Assert.True(state.AllTicked);
        Assert.Contains("Clear all", cut.Find("[data-testid=episode-select-all]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Edit 2 episodes", cut.Find("[data-testid=episode-select-edit]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Back_GoesBackToTheEditor()
    {
        var (_, cut, calls) = await ListAsync();

        cut.Find("[data-testid=episode-select-back]").Click();

        Assert.Equal(["back"], calls);
    }

    [Fact]
    public async Task WhenTheEditorMayNotEdit_NothingCanBeTicked()
    {
        var (_, cut, _) = await ListAsync(canEdit: false);

        Assert.All(cut.FindAll("[data-testid=episode-select-tick]"), tick => Assert.True(tick.HasAttribute("disabled")));
        Assert.True(cut.Find("[data-testid=episode-select-edit]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task OnAPhoneSheet_TheListUsesThePhoneArrangement()
    {
        var (_, phone, _) = await ListAsync(layout: EditorWindowLayout.PhoneSheet);
        var (_, desktop, _) = await ListAsync(layout: EditorWindowLayout.Desktop);

        Assert.Contains("tl-epsel--phone", phone.Find("[data-testid=episode-select]").ClassList);
        Assert.DoesNotContain("tl-epsel--phone", desktop.Find("[data-testid=episode-select]").ClassList);
    }

    // -- The several-items panel offers the way back -------------------------------------------------------------------------------

    [Fact]
    public void TheChooseAgainButton_ShowsOnlyForRowsChosenFromASeasonList()
    {
        var selection = new BulkEditSelection { MediaType = "TV" };
        var row = EditorEpisodeSelectRules.RowFor(EditorHeaderTestData.Episode(EditorHeaderTestData.EpisodeOneId, "S02E01", "Signal lost"),
            EditorEpisodeSelectRules.Offer(EditorHeaderTestData.TvEpisode())!);
        selection.Replace([row]);
        selection.OpenPanel(BulkPanelKind.Edit);
        var calls = 0;

        var plain = Render<BulkEditPanel>(parameters => parameters.Add(panel => panel.Selection, selection).Add(panel => panel.Pending, new PendingEditList()));
        Assert.Empty(plain.FindAll("[data-testid=bulk-choose-rows]"));

        var chosen = Render<BulkEditPanel>(parameters => parameters
            .Add(panel => panel.Selection, selection)
            .Add(panel => panel.Pending, new PendingEditList())
            .Add(panel => panel.OnChooseRows, () => calls++));
        chosen.Find("[data-testid=bulk-choose-rows]").Click();
        Assert.Equal(1, calls);
    }
}
