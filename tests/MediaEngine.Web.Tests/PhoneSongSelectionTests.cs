using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
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
/// Select several songs on a phone (Listen music spec D12): Select in a song's sheet, three ticked songs, Add to playlist through the
/// phone bottom sheet, and the sentence with the count on the bottom bar. The Engine and the dialog are hand-written spies.
/// </summary>
public sealed class PhoneSongSelectionTests : AsyncBunitContext
{
    private static readonly Guid Playlist = Guid.Parse("b1000000-0000-0000-0000-000000000009");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly PlaylistChoiceDialogService _dialogs = new();
    private readonly List<(Guid Playlist, Guid Work)> _added = [];
    private readonly List<Guid> _alreadyThere = [];

    public PhoneSongSelectionTests()
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
        Services.AddSingleton<ISongTablePreferenceStore>(new NoSavedColumns());
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ =>
                Task.FromResult(_alreadyThere.Select(id => new CollectionItemDto { Id = Guid.NewGuid(), WorkId = id, Title = "Held" }).ToList()));
            stub.SetHandler(nameof(IEngineApiClient.AddCollectionItemAsync), args =>
            {
                _added.Add(((Guid)args![0]!, (Guid)args[1]!));
                return Task.FromResult(true);
            });
        }));
    }

    [Fact]
    public async Task AtPhoneWidth_ThreeTickedSongsAreAddedToAPlaylist_AndTheBarSaysHowManyWentIn()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        _dialogs.Choice = new PlaylistChoice(Playlist, "Road trip");
        var rows = Songs();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, _ => { })));

        ChooseSelect(cut, rows[0]);
        foreach (var (row, count) in new[] { (rows[1], 2), (rows[2], 3) })
        {
            cut.Find($"[data-song-row-id='{row.Id}']").Click();
            cut.WaitForAssertion(() => Assert.Equal($"{count} selected", cut.Find("[data-song-selection-bar] [role='status']").TextContent.Trim()));
        }

        cut.FindAll(".song-selection-bar__action").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        cut.WaitForAssertion(() => Assert.Contains("3 songs added to Road trip", cut.Find("[data-song-selection-notice]").TextContent));
        Assert.Equal(rows.Select(row => (Playlist, row.WorkId!.Value)).ToHashSet(), _added.ToHashSet());
        Assert.Equal("Add to playlist", Assert.Single(_dialogs.Titles));
        Assert.Equal(AppDialogSheetMode.Phone, _dialogs.LastOptions?.Sheet);
        Assert.True((bool)_dialogs.LastParameters!.ToDictionary()["Playlist"]);
        // Still in select mode, so the person can do something else with the same songs.
        Assert.Equal("3 selected", cut.Find("[data-song-selection-bar] [role='status']").TextContent.Trim());
    }

    [Fact]
    public async Task OnAPhone_SongsAlreadyInThePlaylistAreSkipped_AndTheBarCountsThem()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        var rows = Songs();
        _alreadyThere.Add(rows[0].WorkId!.Value);
        _dialogs.Choice = new PlaylistChoice(Playlist, "Road trip");
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, _ => { })));
        ChooseSelect(cut, rows[0]);
        cut.Find($"[data-song-row-id='{rows[1].Id}']").Click();
        cut.WaitForAssertion(() => Assert.Equal("2 selected", cut.Find("[data-song-selection-bar] [role='status']").TextContent.Trim()));

        cut.FindAll(".song-selection-bar__action").Single(button => button.TextContent.Trim() == "Add to playlist").Click();

        cut.WaitForAssertion(() => Assert.Contains("1 added, 1 already there", cut.Find("[data-song-selection-notice]").TextContent));
        Assert.Equal([(Playlist, rows[1].WorkId!.Value)], _added);
    }

    [Fact]
    public async Task OnAPhone_TheBarsStatusLineIsAlwaysInThePage_AndEmptyUntilThereIsANotice()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        _dialogs.Choice = new PlaylistChoice(Playlist, "Road trip");
        var rows = Songs();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, _ => { })));

        ChooseSelect(cut, rows[0]);

        var notice = cut.Find("[data-song-selection-notice]");
        Assert.Equal("status", notice.GetAttribute("role"));
        Assert.Equal(string.Empty, notice.TextContent.Trim());
        cut.FindAll(".song-selection-bar__action").Single(button => button.TextContent.Trim() == "Add to playlist").Click();
        cut.WaitForAssertion(() => Assert.Contains("1 song added to Road trip", cut.Find("[data-song-selection-notice]").TextContent));
    }

    [Fact]
    public async Task OnAPhone_WhileThePlaylistListIsOpen_EveryActionIsDisabled_AndASecondPressOpensNoSecondList()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        _dialogs.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogs.Choice = new PlaylistChoice(Playlist, "Road trip");
        var rows = Songs();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, _ => { }))
            .Add(table => table.OnQueue, EventCallback.Factory.Create<SongRow>(this, _ => { })));
        ChooseSelect(cut, rows[0]);
        var add = () => cut.FindAll(".song-selection-bar__action").Single(button => button.TextContent.Trim() == "Add to playlist");

        add().Click();
        cut.WaitForAssertion(() => Assert.True(add().HasAttribute("disabled")));
        Assert.All(cut.FindAll(".song-selection-bar__action"), button => Assert.True(button.HasAttribute("disabled")));
        add().Click();

        Assert.Single(_dialogs.Titles);
        _dialogs.Gate.SetResult();
        cut.WaitForAssertion(() => Assert.Contains("1 song added to Road trip", cut.Find("[data-song-selection-notice]").TextContent));
        Assert.All(cut.FindAll(".song-selection-bar__action"), button => Assert.False(button.HasAttribute("disabled")));
        Assert.Single(_added);
    }

    [Fact]
    public async Task OnAPhone_WhilePlayIsRunning_EveryActionIsDisabled()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        var rows = Songs();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, async _ =>
            {
                played++;
                await gate.Task;
            })));
        ChooseSelect(cut, rows[0]);
        var play = () => cut.FindAll(".song-selection-bar__action").Single(button => button.TextContent.Trim() == "Play");

        play().Click();
        cut.WaitForAssertion(() => Assert.True(play().HasAttribute("disabled")));
        Assert.All(cut.FindAll(".song-selection-bar__action"), button => Assert.True(button.HasAttribute("disabled")));

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.False(play().HasAttribute("disabled")));
        Assert.Equal(1, played);
    }

    [Fact]
    public async Task OnAPhone_WhenASearchHidesTickedSongs_TheirTicksAreDropped_AndTheCountIsTheSongsTheActionsUse()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));
        _dialogs.Choice = new PlaylistChoice(Playlist, "Road trip");
        var rows = Songs();
        var cut = Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, _ => { })));
        ChooseSelect(cut, rows[0]);
        cut.Find($"[data-song-row-id='{rows[1].Id}']").Click();
        cut.WaitForAssertion(() => Assert.Equal("2 selected", cut.Find("[data-song-selection-bar] [role='status']").TextContent.Trim()));

        // The search now shows only the second and third songs: the first (ticked) is hidden.
        cut.Render(parameters => parameters.Add(table => table.Rows, new List<SongRow> { rows[1], rows[2] }));

        cut.WaitForAssertion(() => Assert.Equal("1 selected", cut.Find("[data-song-selection-bar] [role='status']").TextContent.Trim()));
        cut.FindAll(".song-selection-bar__action").Single(button => button.TextContent.Trim() == "Add to playlist").Click();
        cut.WaitForAssertion(() => Assert.Contains("1 song added to Road trip", cut.Find("[data-song-selection-notice]").TextContent));
        Assert.Equal([(Playlist, rows[1].WorkId!.Value)], _added);
    }

    private static void ChooseSelect(IRenderedComponent<SongTable> cut, SongRow row)
    {
        cut.Find($"[data-song-row-id='{row.Id}'] button[aria-haspopup]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Select").Click();
        cut.WaitForElement("[data-song-selection-bar]");
    }

    private static List<SongRow> Songs() =>
    [
        .. new[] { "Alpha", "Beta", "Gamma" }.Select(title => new SongRow
        {
            Id = Guid.NewGuid().ToString("D"),
            Source = SongRowSource.Work,
            Title = title,
            Artist = "The Artist",
            Album = "The Album",
            IsOwned = true,
            DurationSeconds = 185,
            WorkId = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            ArtistPersonId = Guid.NewGuid(),
        }),
    ];

    private sealed class NoSavedColumns : ISongTablePreferenceStore
    {
        public ValueTask<SongTableChoice> LoadAsync(Guid profileId, SongTablePlace place) => ValueTask.FromResult(SongTableChoice.Default);

        public ValueTask SaveAsync(Guid profileId, SongTablePlace place, SongTableChoice choice) => ValueTask.CompletedTask;
    }
}
