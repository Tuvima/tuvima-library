using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Select episodes" (spec details-page-bottom D9, decision A of 2026-11-14): the Season (or Section) dropdown of the editor's header offers a ticked
/// list of that season's episodes, then the editor's own "Editing N episodes" panel and the normal Save changes and Review changes. These tests
/// hold the pure rules, the list's state, and the editor host's device and permission checks (desktop, tablet and phone may; a TV never).
/// </summary>
public sealed class EditorEpisodeSelectTests
{
    private static readonly Guid ItemOne = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid Entity, Guid? Level, int Offset)> _reads = [];

    private static EditorNavItemDto Episode(int number) => EditorHeaderTestData.Episode(
        Guid.Parse($"b1000000-0000-0000-0000-{number:000000000000}"), $"S02E{number:00}", $"Episode {number}");

    // Three episodes, served two to a page, so the whole list needs two reads.
    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
        stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), args =>
        {
            var offset = (int)args![3]!;
            _reads.Add(((Guid)args[0]!, (Guid?)args[1], offset));
            EditorNavItemDto[] all = [Episode(1), Episode(2), Episode(3)];
            var page = all.Skip(offset).Take(2).ToList();
            return Task.FromResult<EditorHeaderLevelDto?>(new EditorHeaderLevelDto
            {
                LevelId = EditorHeaderTestData.SeasonTwoId,
                Total = all.Length,
                Offset = offset,
                PageSize = 2,
                HasMore = offset + page.Count < all.Length,
                Items = page,
            });
        }));

    private async Task<EditorHost> HostAsync(string formFactor = "desktop", bool permission = true)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", formFactor, false, FinePointer: true, Hover: true));
        var host = new EditorHost(Api(), _device, new ServiceCollection().BuildServiceProvider(), NullLogger<EditorHost>.Instance);
        var binding = new EditorBinding(_device)
        {
            EntityId = ItemOne,
            HasPermission = permission,
            ShowHistory = true,
            OpenItem = _ => Task.FromResult(true),
            Trail = () => [new EditTrailItem(ItemOne, "Echoes in the Dark", "Item")],
        };
        await host.OpenAsync(binding, ItemOne);
        return host;
    }

    private static EpisodeSelectRequest SeasonTwo() => EditorEpisodeSelectRules.Offer(EditorHeaderTestData.TvEpisode())!;

    // -- The rules ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ForAnEpisodeInASeason_TheEntryIsOfferedForThatSeason()
    {
        var request = EditorEpisodeSelectRules.Offer(EditorHeaderTestData.TvEpisode());

        Assert.NotNull(request);
        Assert.Equal(EditorHeaderTestData.SeasonTwoId, request.LevelId);
        Assert.Equal("Season 2", request.LevelTitle);
        Assert.Equal(EditorHeaderTestData.EpisodeThreeId, request.EntityId);
        Assert.False(request.Videos);
        Assert.Equal("TV", request.MediaType);
    }

    [Fact]
    public void ForASectionOfVideos_TheEntryIsOfferedForVideos()
    {
        var request = EditorEpisodeSelectRules.Offer(EditorHeaderTestData.CourseLesson());

        Assert.NotNull(request);
        Assert.True(request.Videos);
        Assert.Equal("Videos", request.MediaType);
        Assert.Equal("videos", EditorEpisodeSelectRules.Noun(request));
        Assert.Equal("video", EditorEpisodeSelectRules.Noun(request, 1));
    }

    [Fact]
    public void WithNoSeasonDropdown_OrNoSeasonChosen_OrASeasonWithNoEpisodes_NoEntryIsOffered()
    {
        Assert.Null(EditorEpisodeSelectRules.Offer(null));
        Assert.Null(EditorEpisodeSelectRules.Offer(EditorHeaderTestData.Movie()));

        var tv = EditorHeaderTestData.TvEpisode();
        var noSeasonChosen = new EditorHeaderDto
        {
            Kind = tv.Kind,
            Root = tv.Root,
            CurrentId = tv.CurrentId,
            SecondLevel = new EditorHeaderSecondLevelDto { Label = "Season", CurrentId = null, Entries = tv.SecondLevel!.Entries },
        };
        Assert.Null(EditorEpisodeSelectRules.Offer(noSeasonChosen));

        var emptySeason = new EditorHeaderDto
        {
            Kind = tv.Kind,
            Root = tv.Root,
            CurrentId = tv.CurrentId,
            SecondLevel = new EditorHeaderSecondLevelDto
            {
                Label = "Season",
                CurrentId = EditorHeaderTestData.SeasonOneId,
                Entries = [new EditorHeaderLevelEntryDto { Id = EditorHeaderTestData.SeasonOneId, Title = "Season 1", ItemCount = 0 }],
            },
        };
        Assert.Null(EditorEpisodeSelectRules.Offer(emptySeason));
    }

    [Fact]
    public void ARowForAnEpisode_CarriesTheSeasonAsItsParent_TheTvMediaType_AndItsStill()
    {
        var request = SeasonTwo();

        var row = EditorEpisodeSelectRules.RowFor(Episode(2), request);

        Assert.Equal(DetailEntityType.TvEpisode, row.Kind);
        Assert.Equal(EditorHeaderTestData.SeasonTwoId, row.ParentId);
        Assert.Equal("TV", row.MediaType);
        Assert.Equal("Episode 2", row.Title);
        Assert.NotNull(row.ImageUrl);
    }

    // -- The list ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OpeningTheList_ReadsEveryPageOfTheSeason_AndTicksNothing()
    {
        var select = new EditorEpisodeSelect(Api(), NullLogger.Instance);

        await select.OpenAsync(SeasonTwo());

        Assert.True(select.IsOpen);
        Assert.Equal(3, select.Items.Count);
        Assert.Equal([0, 2], _reads.Select(read => read.Offset));
        Assert.All(_reads, read => Assert.Equal(EditorHeaderTestData.SeasonTwoId, read.Level));
        Assert.Equal(0, select.TickedCount);
        Assert.False(select.Loading);
    }

    [Fact]
    public async Task Ticking_KeepsTheListOrder_AndSelectAllThenClearAllTicksEveryRowAndNone()
    {
        var select = new EditorEpisodeSelect(Api(), NullLogger.Instance);
        await select.OpenAsync(SeasonTwo());

        select.Toggle(Episode(3).Id);
        select.Toggle(Episode(1).Id);
        Assert.Equal(["Episode 1", "Episode 3"], select.TickedRows().Select(row => row.Title));

        select.ToggleAll();
        Assert.True(select.AllTicked);
        Assert.Equal(3, select.TickedCount);

        select.ToggleAll();
        Assert.Equal(0, select.TickedCount);
    }

    [Fact]
    public async Task AFailedRead_SaysSo_AndReadingAgainRecovers()
    {
        var fail = true;
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetEditorNavLevelAsync), _ => Task.FromResult<EditorHeaderLevelDto?>(
                fail ? null : new EditorHeaderLevelDto { Items = [Episode(1)] })));
        var select = new EditorEpisodeSelect(api, NullLogger.Instance);

        await select.OpenAsync(SeasonTwo());
        Assert.True(select.Failed);
        Assert.Empty(select.Items);

        fail = false;
        await select.RetryAsync();
        Assert.False(select.Failed);
        Assert.Single(select.Items);
    }

    [Fact]
    public async Task ComingBackFromThePanel_KeepsTheRowsAlreadyTicked_AndDropsOnesThatAreGone()
    {
        var select = new EditorEpisodeSelect(Api(), NullLogger.Instance);

        await select.OpenAsync(SeasonTwo(), [Episode(2).Id, Guid.NewGuid()]);

        Assert.Equal(["Episode 2"], select.TickedRows().Select(row => row.Title));
    }

    // -- The editor: who may use it ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("desktop")]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task OnADesktopTabletOrPhone_TheEditorMayOpenTheList(string formFactor)
    {
        var host = await HostAsync(formFactor);

        Assert.True(host.HeaderHooks.CanSelectEpisodes!());
        Assert.True(await host.OpenEpisodeSelectAsync(SeasonTwo()));
        Assert.True(host.EpisodeSelect.IsOpen);
        Assert.Equal(3, host.EpisodeSelect.Items.Count);
    }

    [Fact]
    public async Task OnATv_TheEntryIsNeverOffered_AndTheListRefusesToOpen()
    {
        var host = await HostAsync();
        await _device.SwitchDeviceAsync(new DeviceContextSnapshot("television", "tv", false));

        Assert.False(host.HeaderHooks.CanSelectEpisodes!());
        Assert.False(await host.OpenEpisodeSelectAsync(SeasonTwo()));
        Assert.False(host.EpisodeSelect.IsOpen);
    }

    [Fact]
    public async Task WithoutTheEnginesPermissionToEdit_TheEntryIsNotOffered_AndTheListRefusesToOpen()
    {
        var host = await HostAsync(permission: false);

        Assert.False(host.HeaderHooks.CanSelectEpisodes!());
        Assert.False(await host.OpenEpisodeSelectAsync(SeasonTwo()));
        Assert.False(host.EpisodeSelect.IsOpen);
    }

    [Fact]
    public async Task WhenTheEditorIsNotOpen_TheEntryIsNotOffered()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = new EditorHost(Api(), _device, new ServiceCollection().BuildServiceProvider(), NullLogger<EditorHost>.Instance);

        Assert.False(host.HeaderHooks.CanSelectEpisodes!());
        Assert.False(await host.OpenEpisodeSelectAsync(SeasonTwo()));
    }

    // -- The editor: from the ticked list to the ordinary panel -------------------------------------------------------------------

    [Fact]
    public async Task EditN_HandsTheTickedRowsToTheEditorsOwnSeveralItemsPanel_AndCloses_TheList()
    {
        var host = await HostAsync();
        await host.OpenEpisodeSelectAsync(SeasonTwo());
        host.EpisodeSelect.Toggle(Episode(1).Id);
        host.EpisodeSelect.Toggle(Episode(3).Id);

        Assert.True(host.EditSelectedEpisodes());

        Assert.False(host.EpisodeSelect.IsOpen);
        Assert.Equal(BulkPanelKind.Edit, host.Batch.Panel);
        Assert.Equal(["Episode 1", "Episode 3"], host.Batch.Rows.Select(row => row.Title));
        Assert.Equal("TV", host.Batch.MediaType);
        Assert.Equal("Editing 2 episodes", BulkEditRules.EditingTitle(host.Batch.Rows, host.Batch.MediaType));
        Assert.Equal(SeasonTwo(), host.PickedFrom);
        // Nothing is saved or staged by choosing rows: the work waits for the panel, Save changes and Review changes.
        Assert.Equal(0, host.Session.Pending.ChangeCount);
    }

    [Fact]
    public async Task EditN_WithNothingTicked_DoesNothing()
    {
        var host = await HostAsync();
        await host.OpenEpisodeSelectAsync(SeasonTwo());

        Assert.False(host.EditSelectedEpisodes());

        Assert.True(host.EpisodeSelect.IsOpen);
        Assert.Equal(BulkPanelKind.None, host.Batch.Panel);
    }

    [Fact]
    public async Task ChoosingAgainFromThePanel_ReopensTheListWithTheTicksKept()
    {
        var host = await HostAsync();
        await host.OpenEpisodeSelectAsync(SeasonTwo());
        host.EpisodeSelect.Toggle(Episode(2).Id);
        host.EditSelectedEpisodes();

        Assert.True(await host.OpenEpisodeSelectAsync(host.PickedFrom!));

        Assert.True(host.EpisodeSelect.IsOpen);
        Assert.Equal(["Episode 2"], host.EpisodeSelect.TickedRows().Select(row => row.Title));
    }

    [Fact]
    public async Task ClosingTheEditor_EndsTheListTheTicksAndTheirSource()
    {
        var host = await HostAsync();
        await host.OpenEpisodeSelectAsync(SeasonTwo());
        host.EpisodeSelect.Toggle(Episode(1).Id);
        host.EditSelectedEpisodes();
        await host.OpenEpisodeSelectAsync(SeasonTwo());

        await host.Flow.LeaveAsync();

        Assert.False(host.IsOpen);
        Assert.False(host.EpisodeSelect.IsOpen);
        Assert.Equal(0, host.Batch.Count);
        Assert.Null(host.PickedFrom);
    }
}
