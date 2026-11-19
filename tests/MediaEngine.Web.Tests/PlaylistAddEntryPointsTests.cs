using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Add to playlist" gives the same sentence wherever it starts (Listen playlists spec WP2, D6): the selection bar, the song menu, the player
/// bar menu, the page action and dragged songs all go through one service, so the same add says the same thing. The Engine and the picker
/// are hand-written stand-ins.
/// </summary>
public sealed class PlaylistAddEntryPointsTests : AsyncBunitContext
{
    private static readonly Guid RoadTrip = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    private static readonly Guid SongA = Guid.Parse("50000000-0000-0000-0000-00000000000a");
    private static readonly Guid SongB = Guid.Parse("50000000-0000-0000-0000-00000000000b");
    private static readonly Guid SongC = Guid.Parse("50000000-0000-0000-0000-00000000000c");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly PlaylistChoiceDialogService _dialogs = new() { Choice = new PlaylistChoice(RoadTrip, "Road trip") };
    private readonly List<Guid> _alreadyThere = [];
    private readonly List<Guid> _added = [];
    private bool _engineAnswers = true;

    public PlaylistAddEntryPointsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<SongDragDropService>();
        Services.AddSingleton(_device);
        Services.AddSingleton(TestSessions.For(kids: false, administrator: true));
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Services.AddSingleton<IAppDialogService>(_dialogs);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemsAsync), args =>
                _engineAnswers
                    ? PlaylistAddSpy.Answer(args, _alreadyThere, (_, work) => _added.Add(work))
                    : Task.FromResult<CollectionItemsAddedResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalStatusAsync), _ =>
                Task.FromResult<PersonalStatusInfo?>(new PersonalStatusInfo("r1", 1, 0, 0, false)));
        }));
    }

    /// <summary>One way of starting an add of these songs; it returns the sentence the person reads afterwards.</summary>
    public enum EntryPoint
    {
        SelectionBar,
        SongMenu,
        PlayerMenu,
        PageAction,
        DraggedSongs,
    }

    // -- The same sentence from every entry point ---------------------------------------------

    [Theory]
    [InlineData(EntryPoint.SelectionBar)]
    [InlineData(EntryPoint.SongMenu)]
    [InlineData(EntryPoint.PlayerMenu)]
    [InlineData(EntryPoint.PageAction)]
    [InlineData(EntryPoint.DraggedSongs)]
    public async Task OneNewSong_IsSaidTheSameWayFromEveryEntryPoint(EntryPoint entry)
    {
        Assert.Equal("1 song added to Road trip", await SentenceAsync(entry, SongA));
        Assert.Equal([SongA], _added);
    }

    [Theory]
    [InlineData(EntryPoint.SelectionBar)]
    [InlineData(EntryPoint.SongMenu)]
    [InlineData(EntryPoint.PlayerMenu)]
    [InlineData(EntryPoint.PageAction)]
    [InlineData(EntryPoint.DraggedSongs)]
    public async Task ASongThePlaylistAlreadyHolds_IsSaidTheSameWayFromEveryEntryPoint(EntryPoint entry)
    {
        _alreadyThere.Add(SongA);

        Assert.Equal("Already in Road trip", await SentenceAsync(entry, SongA));
        Assert.Empty(_added);
    }

    [Theory]
    [InlineData(EntryPoint.SelectionBar)]
    [InlineData(EntryPoint.PageAction)]
    [InlineData(EntryPoint.DraggedSongs)]
    public async Task SeveralSongsWithOneAlreadyThere_AreSaidTheSameWayFromEveryEntryPointThatCarriesSeveral(EntryPoint entry)
    {
        _alreadyThere.Add(SongB);

        Assert.Equal("2 added, 1 already there", await SentenceAsync(entry, SongA, SongB, SongC));
        Assert.Equal([SongA, SongC], _added);
    }

    [Theory]
    [InlineData(EntryPoint.SelectionBar)]
    [InlineData(EntryPoint.SongMenu)]
    [InlineData(EntryPoint.PlayerMenu)]
    [InlineData(EntryPoint.PageAction)]
    [InlineData(EntryPoint.DraggedSongs)]
    public async Task WhenTheEngineDoesNotAnswer_EveryEntryPointSaysWhatFailed(EntryPoint entry)
    {
        _engineAnswers = false;

        Assert.Equal("The songs could not be added to Road trip.", await SentenceAsync(entry, SongA));
        Assert.Empty(_added);
    }

    [Fact]
    public async Task TheSongMenuAndPlayerMenuSentences_ComeAsALineWithNoUndoButton_AndItsSeverity()
    {
        _engineAnswers = false;
        var offers = new List<UndoOffer>();
        var cut = Render<PersonalStatusMenuItems>(parameters => parameters
            .Add(menu => menu.WorkId, SongA)
            .Add(menu => menu.OnChanged, EventCallback.Factory.Create<UndoOffer>(this, offers.Add)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Add to playlist").Click();

        cut.WaitForAssertion(() => Assert.Single(offers));
        Assert.Null(offers[0].UndoAsync);
        Assert.Equal(AppSeverity.Error, offers[0].Severity);

        var note = Render<AppUndoNote>(parameters => parameters.Add(n => n.Offer, offers[0]));
        Assert.Empty(note.FindAll("[data-undo-note-action]"));
        Assert.Contains("could not be added", note.Find("[data-undo-note-line]").TextContent);
    }

    [Fact]
    public async Task ClosingThePickerWithoutChoosing_AddsNothing_AndEveryEntryPointSaysNothing()
    {
        _dialogs.Choice = null;

        foreach (var entry in new[] { EntryPoint.SelectionBar, EntryPoint.SongMenu, EntryPoint.PlayerMenu, EntryPoint.PageAction })
        {
            Assert.Equal(string.Empty, await SentenceAsync(entry, SongA));
        }

        Assert.Empty(_added);
    }

    [Fact]
    public async Task EveryEntryPoint_OpensThePhoneSheetOnAPhone_AndTheSmallWindowElsewhere()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        await PlaylistMembershipFlow.RunForSongsAsync(Services, [SongA], "Add to playlist");
        Assert.Equal(AppDialogSheetMode.Phone, _dialogs.LastOptions?.Sheet);

        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        await PlaylistMembershipFlow.RunForSongsAsync(Services, [SongA], "Add to playlist");
        Assert.NotEqual(AppDialogSheetMode.Phone, _dialogs.LastOptions?.Sheet);
    }

    // -- Starting an add from each entry point -------------------------------------------------

    private async Task<string> SentenceAsync(EntryPoint entry, params Guid[] songs)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        return entry switch
        {
            EntryPoint.SelectionBar => FromBar(songs),
            EntryPoint.SongMenu => FromSongMenu(songs[0]),
            EntryPoint.PlayerMenu => await FromPlayerMenuAsync(songs[0]),
            EntryPoint.PageAction => await FromPageActionAsync(songs),
            _ => await FromDragAsync(songs),
        };
    }

    private string FromBar(Guid[] songs)
    {
        var selection = new BulkEditSelection { MediaType = "Music" };
        selection.Replace(songs.Select((id, index) => new BulkEditRow(id, $"Song {index + 1}", DetailEntityType.Work, null, "Music")));
        var bar = Render<BulkSelectionBar>(parameters => parameters.Add(component => component.Selection, selection));

        bar.FindAll(".tl-batch__bar button").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        return WaitForSentence(() => _dialogs.Titles.Count > 0, () => bar.FindAll("[data-bulk-selection-notice]").FirstOrDefault()?.TextContent.Trim());
    }

    private string FromSongMenu(Guid song)
    {
        var offers = new List<UndoOffer>();
        var cut = Render<PersonalStatusMenuItems>(parameters => parameters
            .Add(menu => menu.WorkId, song)
            .Add(menu => menu.OnChanged, EventCallback.Factory.Create<UndoOffer>(this, offers.Add)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Add to playlist").Click();

        return WaitForSentence(() => _dialogs.Titles.Count > 0, () => offers.FirstOrDefault()?.Message);
    }

    private async Task<string> FromPlayerMenuAsync(Guid song)
    {
        Render<AppPopoverHost>();
        var offers = new List<UndoOffer>();
        var item = new ListenQueueItem { WorkId = song, AssetId = Guid.NewGuid(), Title = "Track" };
        var cut = Render<PlaybackItemMenu>(parameters => parameters
            .Add(menu => menu.Media, PlaybackBarMedia.Music)
            .Add(menu => menu.Item, item)
            .Add(menu => menu.OnStatusChanged, EventCallback.Factory.Create<UndoOffer>(this, offers.Add)));
        await cut.InvokeAsync(() => cut.FindComponent<PlaybackPanel>().Instance.OpenAsync(true));
        cut.WaitForElement("[data-playback-item-menu]");

        cut.Find("[data-playback-item-action='AddToPlaylist']").Click();

        return WaitForSentence(() => _dialogs.Titles.Count > 0, () => offers.FirstOrDefault()?.Message);
    }

    private async Task<string> FromPageActionAsync(Guid[] songs)
    {
        // The detail page's "Add to playlist" action and the phone's bottom bar hand their songs to this call.
        var notice = await PlaylistMembershipFlow.RunForSongsAsync(Services, songs, "Add to playlist");
        return notice?.Message ?? string.Empty;
    }

    private async Task<string> FromDragAsync(Guid[] songs)
    {
        var owner = new object();
        var drop = Services.GetRequiredService<SongDragDropService>();
        string? said = null;
        drop.NoticeRaised += raised => said = raised.Notice.Message;
        drop.Begin(owner, songs);

        await drop.DropOnPlaylistAsync(RoadTrip, "Road trip");

        return said ?? string.Empty;
    }

    private static string WaitForSentence(Func<bool> opened, Func<string?> read)
    {
        // A picker closed without a choice says nothing, so once the picker has been opened the wait ends after a short grace period.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        DateTime? openedAt = null;
        while (DateTime.UtcNow < deadline)
        {
            if (opened())
            {
                openedAt ??= DateTime.UtcNow;
                if (read() is { Length: > 0 } sentence)
                {
                    return sentence;
                }

                if (DateTime.UtcNow - openedAt > TimeSpan.FromMilliseconds(400))
                {
                    return string.Empty;
                }
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException("The picker was never opened.");
    }
}
