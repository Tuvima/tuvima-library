using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Ticked songs go into a hand-made playlist (Listen music spec D9, D15): which actions the selection bar offers for songs, albums,
/// audiobooks and everything else; the playlist list (no rule-based playlists, a name for New playlist); and the add itself (songs
/// already there are skipped, the sentence in place gives the counts). The Engine and the dialog are hand-written spies.
/// </summary>
public sealed class BulkAddToPlaylistTests : AsyncBunitContext
{
    private static readonly Guid RoadTrip = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid SongA = Guid.Parse("50000000-0000-0000-0000-00000000000a");
    private static readonly Guid SongB = Guid.Parse("50000000-0000-0000-0000-00000000000b");
    private static readonly Guid SongC = Guid.Parse("50000000-0000-0000-0000-00000000000c");
    private static readonly Guid AlbumOne = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid AlbumTwo = Guid.Parse("a1000000-0000-0000-0000-000000000002");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly PlaylistChoiceDialogService _dialogs = new();
    private readonly List<(Guid Playlist, Guid Work)> _added = [];
    private readonly List<(string Name, IReadOnlyList<Guid> Works)> _created = [];
    private readonly List<Guid> _alreadyInPlaylist = [];
    private EngineApiClientStub _stub = default!;

    public BulkAddToPlaylistTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(_device);
        Services.AddSingleton(TestSessions.For(kids: false, administrator: true));
        Services.AddSingleton<IAppDialogService>(_dialogs);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            _stub = stub;
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ =>
                Task.FromResult(_alreadyInPlaylist.Select(id => new CollectionItemDto { Id = Guid.NewGuid(), WorkId = id, Title = "Held" }).ToList()));
            stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemAsync), args =>
            {
                _added.Add(((Guid)args![0]!, (Guid)args[1]!));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionWithItemsAsync), args =>
            {
                _created.Add(((string)args![0]!, (IReadOnlyList<Guid>)args[8]!));
                return Task.FromResult<Guid?>(Guid.NewGuid());
            });
            stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => Task.FromResult(new List<ManagedCollectionViewModel>
            {
                new() { Id = RoadTrip, Name = "Road trip", CollectionType = "Playlist", Resolution = "materialized", MembershipMode = "Manual", CanEdit = true, IsEnabled = true },
                new() { Id = Guid.NewGuid(), Name = "Newest rock", CollectionType = "Playlist", Resolution = "materialized", MembershipMode = "Smart", CanEdit = true, IsEnabled = true },
                new() { Id = Guid.NewGuid(), Name = "Not mine", CollectionType = "Playlist", Resolution = "materialized", MembershipMode = "Manual", CanEdit = false, IsEnabled = true },
            }));
        }));
    }

    // -- What the bar offers per kind ----------------------------------------------------------

    [Fact]
    public async Task TickedSongs_OfferAddToPlaylist_AndNoAddToCollection()
    {
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music");

        Assert.True(HasButton(bar, "Add to playlist"));
        Assert.DoesNotContain(bar.FindAll(".tl-batch__bar button"), button => button.TextContent.Contains("collection", StringComparison.OrdinalIgnoreCase));
        Assert.True(HasButton(bar, "Edit"));
    }

    [Fact]
    public async Task TickedAlbums_OfferAddToPlaylistAndAddToCollection()
    {
        var bar = await RenderBarAsync(
            [new BulkEditRow(AlbumOne, "One", DetailEntityType.MusicAlbum, null, "Music"), new BulkEditRow(AlbumTwo, "Two", DetailEntityType.MusicAlbum, null, "Music")],
            "Music");

        Assert.True(HasButton(bar, "Add to playlist"));
        Assert.True(HasButton(bar, "Add 2 to collection"));
    }

    [Fact]
    public async Task TickedAudiobooks_OfferAddToCollectionOnly()
    {
        var bar = await RenderBarAsync(
            [new BulkEditRow(SongA, "Dune", DetailEntityType.Audiobook, null, "Audiobooks"), new BulkEditRow(SongB, "Emma", DetailEntityType.Audiobook, null, "Audiobooks")],
            "Audiobooks");

        Assert.False(HasButton(bar, "Add to playlist"));
        Assert.True(HasButton(bar, "Add 2 to collection"));
    }

    [Fact]
    public async Task TickedBooks_KeepTheirBarUnchanged()
    {
        var bar = await RenderBarAsync(
            [new BulkEditRow(SongA, "Dune", DetailEntityType.Book, null, "Books"), new BulkEditRow(SongB, "Emma", DetailEntityType.Book, null, "Books")],
            "Books");

        Assert.False(HasButton(bar, "Add to playlist"));
        Assert.True(HasButton(bar, "Add 2 to collection"));
    }

    [Theory]
    [InlineData(DetailEntityType.Work, "Music", true, false)]
    [InlineData(DetailEntityType.MusicAlbum, "Music", true, true)]
    [InlineData(DetailEntityType.Audiobook, "Audiobooks", false, true)]
    [InlineData(DetailEntityType.Movie, "Movies", false, true)]
    [InlineData(DetailEntityType.TvEpisode, "TV", false, true)]
    public void TheKindRule_DecidesPlaylistAndCollectionOffersInOnePlace(DetailEntityType kind, string mediaType, bool playlist, bool collection)
    {
        BulkEditRow[] rows = [new(SongA, "One", kind, null, mediaType)];

        Assert.Equal(playlist, BulkEditRules.CanAddToPlaylist(rows, mediaType));
        Assert.Equal(collection, BulkEditRules.CanAddToCollection(rows, mediaType));
    }

    [Fact]
    public void ASongWhoseRowCarriesNoMediaType_IsStillASongOnAMusicList()
    {
        BulkEditRow[] rows = [new(SongA, "One", DetailEntityType.Work)];

        Assert.True(BulkEditRules.CanAddToPlaylist(rows, "Music"));
        Assert.False(BulkEditRules.CanAddToCollection(rows, "Music"));
    }

    // -- The playlist list ---------------------------------------------------------------------

    [Fact]
    public void ARuleBasedPlaylist_IsNeverAnAddTarget()
    {
        var smart = new ManagedCollectionDto
        {
            Id = Guid.NewGuid(), Name = "Newest rock", CollectionType = "Playlist", Resolution = "materialized",
            MembershipMode = "Smart", CanEdit = true, IsEnabled = true,
        };

        Assert.False(CollectionAuthoringRule.IsAddTarget(smart, playlist: true));
    }

    [Fact]
    public void ThePlaylistList_HoldsOnlyHandMadePlaylistsThePersonMayEdit()
    {
        var dialog = RenderDialog(out _, offerNew: true);

        var names = dialog.FindComponent<AppSelect>().Instance.Options!.Select(option => option.Label).ToList();
        Assert.Equal(["Road trip"], names);
    }

    [Fact]
    public void ChoosingNewPlaylist_AsksForANameAndClosesWithIt()
    {
        var dialog = RenderDialog(out var context, offerNew: true);
        var addButton = () => dialog.FindAll(".app-dialog-shell__footer button, footer button, button").Last(button => button.TextContent.Trim() == "Add");

        Assert.True(addButton().HasAttribute("disabled"));
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "New playlist").Click();
        dialog.Find("input[type='text']").Input("Road trip 2");
        addButton().Click();

        var choice = Assert.IsType<PlaylistChoice>(context.Closed?.Data);
        Assert.Null(choice.PlaylistId);
        Assert.Equal("Road trip 2", choice.Name);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task ChoosingAnExistingPlaylist_ClosesWithItAndAddsNothingItself()
    {
        var dialog = RenderDialog(out var context, offerNew: true);

        await dialog.InvokeAsync(() => dialog.FindComponent<AppSelect>().Instance.ValueChanged.InvokeAsync(RoadTrip.ToString("D")));
        dialog.FindAll("button").Last(button => button.TextContent.Trim() == "Add").Click();

        var choice = Assert.IsType<PlaylistChoice>(context.Closed?.Data);
        Assert.Equal(RoadTrip, choice.PlaylistId);
        Assert.Equal("Road trip", choice.Name);
        Assert.Empty(_added);
    }

    // -- The add and the sentence in place -----------------------------------------------------

    [Fact]
    public async Task ChoosingAPlaylist_AddsTheTickedSongsOnce_AndSaysHowManyWentIn()
    {
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(Songs(SongA, SongB, SongC), "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("3 songs added to Road trip", bar.Find("[data-bulk-selection-notice]").TextContent));
        Assert.Equal([(RoadTrip, SongA), (RoadTrip, SongB), (RoadTrip, SongC)], _added);
        Assert.Equal(["Add to playlist"], _dialogs.Titles);
        Assert.True(_dialogs.LastParameters!.ToDictionary().TryGetValue("Playlist", out var forPlaylist) && (bool)forPlaylist);
    }

    [Fact]
    public async Task ChoosingAPlaylistThatHoldsSomeOfThem_SkipsThose_AndSaysSo()
    {
        _alreadyInPlaylist.Add(SongB);
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(Songs(SongA, SongB, SongC), "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("2 added, 1 already there", bar.Find("[data-bulk-selection-notice]").TextContent));
        Assert.Equal([(RoadTrip, SongA), (RoadTrip, SongC)], _added);
    }

    [Fact]
    public async Task ChoosingAPlaylistThatHoldsThemAll_AddsNothing_AndSaysSo()
    {
        _alreadyInPlaylist.AddRange([SongA, SongB]);
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("already in Road trip", bar.Find("[data-bulk-selection-notice]").TextContent));
        Assert.Empty(_added);
    }

    [Fact]
    public async Task NewPlaylist_CreatesItWithTheTickedSongs_AndSaysHowManyWentIn()
    {
        _dialogs.Choice = new PlaylistChoice(null, "Weekend");
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("2 songs added to Weekend", bar.Find("[data-bulk-selection-notice]").TextContent));
        var created = Assert.Single(_created);
        Assert.Equal("Weekend", created.Name);
        Assert.Equal([SongA, SongB], created.Works);
        Assert.Empty(_added);
    }

    [Fact]
    public async Task NewPlaylistThatTheEngineCouldNotCreate_SaysThePlaylistCouldNotBeCreated()
    {
        _stub.SetHandler(nameof(IEngineApiClient.CreateCollectionWithItemsAsync), _ => Task.FromResult<Guid?>(null));
        _dialogs.Choice = new PlaylistChoice(null, "Weekend");
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Equal("The playlist could not be created.", bar.Find("[data-bulk-selection-notice]").TextContent.Trim()));
        Assert.Empty(_added);
    }

    [Fact]
    public async Task WhileTheListIsOpen_EveryBarButtonIsDisabled_AndASecondPressOpensNoSecondList()
    {
        _dialogs.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music");
        var add = () => bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist");

        add().Click();
        bar.WaitForAssertion(() => Assert.True(add().HasAttribute("disabled")));
        Assert.All(bar.FindAll(".tl-batch__bar button").Where(button => button.TextContent.Trim() is "Add to playlist" or "Clear" or "Edit"),
            button => Assert.True(button.HasAttribute("disabled")));
        add().Click();

        Assert.Single(_dialogs.Titles);
        _dialogs.Gate.SetResult();
        bar.WaitForAssertion(() => Assert.Contains("2 songs added to Road trip", bar.Find("[data-bulk-selection-notice]").TextContent));
        Assert.False(add().HasAttribute("disabled"));
        Assert.Equal(2, _added.Count);
    }

    [Fact]
    public async Task WhilePlayIsRunning_EveryBarButtonIsDisabled_AndAnotherPressDoesNothing()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        var selection = new BulkEditSelection { MediaType = "Music" };
        selection.Replace(Songs(SongA, SongB));
        var bar = Render<BulkSelectionBar>(parameters => parameters
            .Add(component => component.Selection, selection)
            .Add(component => component.OnPlay, EventCallback.Factory.Create<IReadOnlyList<BulkEditRow>>(this, async _ =>
            {
                played++;
                await gate.Task;
            })));
        var play = () => bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Play");

        play().Click();
        bar.WaitForAssertion(() => Assert.True(play().HasAttribute("disabled")));
        Assert.All(bar.FindAll(".tl-batch__bar button").Where(button => button.TextContent.Trim() is "Add to playlist" or "Clear"),
            button => Assert.True(button.HasAttribute("disabled")));

        gate.SetResult();
        bar.WaitForAssertion(() => Assert.False(play().HasAttribute("disabled")));
        Assert.Equal(1, played);
    }

    [Fact]
    public async Task TheStatusRegion_IsAlwaysInThePage_AndEmptyUntilThereIsANotice()
    {
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(Songs(SongA), "Music");
        var region = bar.Find("[data-bulk-selection-announce]");

        Assert.Equal("status", region.GetAttribute("role"));
        Assert.Equal(string.Empty, region.TextContent.Trim());
        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("1 song added to Road trip", bar.Find("[data-bulk-selection-announce]").TextContent));
    }

    [Fact]
    public void ForAKidsProfile_TheListDoesNotOfferNewPlaylist()
    {
        Services.AddSingleton(TestSessions.For(kids: true, administrator: false));

        var dialog = RenderDialog(out _, offerNew: true);

        Assert.DoesNotContain(dialog.FindAll("button"), button => button.TextContent.Trim() == "New playlist");
    }

    [Fact]
    public void ForAnAdultProfile_TheListOffersNewPlaylist()
    {
        var dialog = RenderDialog(out _, offerNew: true);

        Assert.Contains(dialog.FindAll("button"), button => button.TextContent.Trim() == "New playlist");
    }

    [Fact]
    public async Task ClosingTheListWithoutChoosing_ChangesNothingAndSaysNothing()
    {
        _dialogs.Choice = null;
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Single(_dialogs.Titles));
        Assert.Empty(bar.FindAll("[data-bulk-selection-notice]"));
        Assert.Empty(_added);
        Assert.Empty(_created);
    }

    [Fact]
    public async Task TickedAlbums_AddEverySongOnThemInTrackOrder()
    {
        _stub.SetHandler(nameof(IEngineApiClient.GetCollectionGroupDetailAsync), args =>
        {
            var album = (Guid)args![0]!;
            return Task.FromResult<CollectionGroupDetailViewModel?>(CollectionGroupDetailViewModel.FromContract(new CollectionGroupDetailDto
            {
                CollectionId = album,
                DisplayName = "Album",
                Works = album == AlbumOne
                    ? [Track(SongB, "Second", "2"), Track(SongA, "First", "1")]
                    : [Track(SongC, "Only", "1")],
            }));
        });
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(
            [new BulkEditRow(AlbumOne, "One", DetailEntityType.MusicAlbum, null, "Music"), new BulkEditRow(AlbumTwo, "Two", DetailEntityType.MusicAlbum, null, "Music")],
            "Music");

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("3 songs added to Road trip", bar.Find("[data-bulk-selection-notice]").TextContent));
        Assert.Equal([SongA, SongB, SongC], _added.Select(pair => pair.Work));
    }

    [Fact]
    public async Task OnAPhone_TheDesktopBarIsNotDrawn_BecauseThePhoneHasItsOwnBottomBar()
    {
        var bar = await RenderBarAsync(Songs(SongA), "Music", "phone");

        Assert.Empty(bar.FindAll(".tl-batch__bar"));
        Assert.Empty(_dialogs.Titles);
    }

    [Fact]
    public async Task OnATablet_TickedSongsStillAddToAPlaylist_WithoutAnyRightToEdit()
    {
        _dialogs.Choice = new PlaylistChoice(RoadTrip, "Road trip");
        var bar = await RenderBarAsync(Songs(SongA, SongB), "Music", "tablet");

        Assert.False(HasButton(bar, "Edit"));
        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        bar.WaitForAssertion(() => Assert.Contains("2 songs added to Road trip", bar.Find("[data-bulk-selection-notice]").TextContent));
    }

    // -- Helpers -------------------------------------------------------------------------------

    private static BulkEditRow[] Songs(params Guid[] ids) =>
        [.. ids.Select((id, index) => new BulkEditRow(id, $"Song {index + 1}", DetailEntityType.Work, AlbumOne, "Music"))];

    private static CollectionGroupWorkDto Track(Guid id, string title, string number) =>
        new() { WorkId = id, Title = title, TrackNumber = number };

    private async Task<IRenderedComponent<BulkSelectionBar>> RenderBarAsync(IEnumerable<BulkEditRow> rows, string mediaType, string formFactor = "desktop")
    {
        await (formFactor switch
        {
            "tablet" => _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true)),
            "phone" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)),
            _ => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)),
        });
        var selection = new BulkEditSelection { MediaType = mediaType };
        selection.Replace(rows);
        return Render<BulkSelectionBar>(parameters => parameters.Add(component => component.Selection, selection));
    }

    private static bool HasButton(IRenderedComponent<BulkSelectionBar> bar, string label) =>
        bar.FindAll(".tl-batch__bar button").Any(button => button.TextContent.Trim() == label);

    private IRenderedComponent<MediaMembershipDialog> RenderDialog(out RecordingContext context, bool offerNew)
    {
        var recording = new RecordingContext();
        context = recording;
        var dialog = Render<MediaMembershipDialog>(parameters => parameters
            .AddCascadingValue<IAppDialogContext>(recording)
            .Add(component => component.Playlist, true)
            .Add(component => component.ChooseOnly, true)
            .Add(component => component.OfferNewPlaylist, offerNew)
            .Add(component => component.WorkIds, new[] { SongA }));
        dialog.WaitForAssertion(() => Assert.DoesNotContain(dialog.FindAll("p"), p => p.TextContent.Contains("Loading", StringComparison.OrdinalIgnoreCase)));
        return dialog;
    }

    /// <summary>A dialog window that remembers what it was closed with.</summary>
    private sealed class RecordingContext : IAppDialogContext
    {
        public AppDialogResult? Closed { get; private set; }
        public Guid Id { get; } = Guid.NewGuid();
        public AppDialogOptions Options { get; } = new();
        public string? PresentationClass => null;
        public void Close(AppDialogResult? result = null) => Closed = result;
        public void Cancel() => Closed = AppDialogResult.Cancel();
        public Task CloseAsync(AppDialogResult? result = null)
        {
            Close(result);
            return Task.CompletedTask;
        }

        public Task CancelAsync()
        {
            Cancel();
            return Task.CompletedTask;
        }

        public Task SetOptionsAsync(AppDialogOptions options) => Task.CompletedTask;
        public Task SetPresentationClassAsync(string? value) => Task.CompletedTask;
        public void SetCloseGuard(Func<Task<bool>>? guard)
        {
        }

        public IDisposable RegisterCloseGuard(Func<Task<bool>> guard) => new Registration();

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
