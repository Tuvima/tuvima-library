using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Listen;
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
/// Edit for ticked songs on a phone: a "..." button beside Done opens a bottom sheet with one item, "Edit N items", drawn only when the
/// person may edit and the device may edit items (never for ticked albums, never on a TV). The host's spy receives exactly the ticked songs.
/// </summary>
public sealed class PhoneSongBulkEditTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<IReadOnlyList<SongRow>> _edited = [];

    public PhoneSongBulkEditTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(_device);
        Services.AddSingleton(TestSessions.For(kids: false, administrator: true));
        Services.AddSingleton<ISongTablePreferenceStore>(new NoSavedColumns());
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview { AffectedCount = 1, Fields = [] })));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        }));
    }

    private Task UsePhoneAsync() => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true, FinePointer: false, Hover: false));

    private IRenderedComponent<SongTable> RenderTable(List<SongRow> rows, bool canEdit) =>
        Render<SongTable>(parameters => parameters
            .Add(table => table.Place, SongTablePlace.Songs)
            .Add(table => table.Rows, rows)
            .Add(table => table.CanEdit, canEdit)
            .Add(table => table.OnPlay, EventCallback.Factory.Create<SongPlayRequest>(this, _ => { }))
            .Add(table => table.OnEditSelected, EventCallback.Factory.Create<IReadOnlyList<SongRow>>(this, ticked => _edited.Add(ticked))));

    [Fact]
    public async Task OnAPhone_APersonWhoMayEditSeesTheMoreButtonBesideDone_WithEditNItems()
    {
        await UsePhoneAsync();
        var rows = Songs();
        var cut = RenderTable(rows, canEdit: true);
        ChooseSelect(cut, rows[0]);
        TickAndWait(cut, rows[1], 2);
        TickAndWait(cut, rows[2], 3);

        var head = cut.Find(".song-selection-bar__head");
        var more = head.QuerySelector("button.song-selection-bar__more");
        Assert.NotNull(more);
        Assert.Equal("More actions", more.GetAttribute("aria-label"));
        Assert.NotNull(head.QuerySelector("button.song-selection-bar__done"));

        more.Click();
        cut.WaitForAssertion(() => Assert.Equal(["Edit 3 items"], cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())));
    }

    [Fact]
    public async Task OnAPhone_ChoosingEditOpensTheEditorForExactlyTheTickedSongs()
    {
        await UsePhoneAsync();
        var rows = Songs();
        var cut = RenderTable(rows, canEdit: true);
        ChooseSelect(cut, rows[0]);
        TickAndWait(cut, rows[2], 2);

        cut.Find("button.song-selection-bar__more").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single().Click();

        cut.WaitForAssertion(() => Assert.Single(_edited));
        Assert.Equal([rows[0].Id, rows[2].Id], _edited[0].Select(row => row.Id));
    }

    [Fact]
    public async Task OnAPhone_APersonWhoMayNotEditSeesNoMoreButton()
    {
        await UsePhoneAsync();
        var rows = Songs();
        var cut = RenderTable(rows, canEdit: false);

        ChooseSelect(cut, rows[0]);

        Assert.Empty(cut.FindAll("button.song-selection-bar__more"));
        Assert.NotEmpty(cut.FindAll("button.song-selection-bar__done"));
    }

    [Fact]
    public async Task OnAPhone_TickedAlbumsGetNoMoreButton()
    {
        await UsePhoneAsync();

        var cut = Render<SongSelectionBar>(parameters => parameters
            .Add(bar => bar.AlbumRows, [new MediaEngine.Web.Services.Editing.BulkEditRow(Guid.NewGuid(), "The Album", MediaEngine.Contracts.Details.DetailEntityType.Work, null, "Music")])
            .Add(bar => bar.Count, 1)
            .Add(bar => bar.CanEdit, true)
            .Add(bar => bar.OnEdit, EventCallback.Factory.Create(this, () => { })));

        Assert.Empty(cut.FindAll("button.song-selection-bar__more"));
    }

    [Fact]
    public async Task OnATv_TheBarOffersNoEdit_EvenForAPersonWhoMayEdit()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true));

        var cut = Render<SongSelectionBar>(parameters => parameters
            .Add(bar => bar.WorkIds, [Guid.NewGuid()])
            .Add(bar => bar.Count, 1)
            .Add(bar => bar.CanEdit, true)
            .Add(bar => bar.OnEdit, EventCallback.Factory.Create(this, () => { })));

        Assert.Empty(cut.FindAll("button.song-selection-bar__more"));
    }

    [Fact]
    public async Task OnAPhone_ASongsMoreMenuEditOpensTheEditorOnExactlyThatSong()
    {
        await UsePhoneAsync();
        var tracks = Tracks(3);
        var cut = Render<SongList>(parameters => parameters.Add(list => list.Place, SongTablePlace.Songs).Add(list => list.Tracks, tracks));
        var host = Services.GetRequiredService<EditorHost>();

        cut.FindAll(".song-table__pmenu button[aria-haspopup='menu']")[1].Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Edit"));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();

        cut.WaitForAssertion(() => Assert.True(host.IsOpen));
        Assert.Equal([tracks[1].Id], host.Batch.Rows.Select(row => row.Id));
        Assert.Equal(BulkPanelKind.Edit, host.Batch.Panel);
    }

    [Fact]
    public async Task OnAPhone_ASongsMoreMenuHasNoEditForAPersonWhoMayNotEdit()
    {
        await UsePhoneAsync();
        var rows = Songs();
        var cut = RenderTable(rows, canEdit: false);

        cut.Find($"[data-song-row-id='{rows[0].Id}'] button[aria-haspopup]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        Assert.DoesNotContain(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Edit");
    }

    [Fact]
    public async Task OnAPhone_AProfileThatMayNotEditDetails_GetsNoEditInASongsMenuAndNoMoreButtonWhileSelecting()
    {
        Services.AddSingleton(TestSessions.For(kids: false, administrator: true, canManageMetadata: false));
        await UsePhoneAsync();
        var tracks = Tracks(3);
        var cut = Render<SongList>(parameters => parameters.Add(list => list.Place, SongTablePlace.Songs).Add(list => list.Tracks, tracks));
        var host = Services.GetRequiredService<EditorHost>();

        cut.FindAll(".song-table__pmenu button[aria-haspopup='menu']")[1].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        Assert.DoesNotContain(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Edit");
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Select").Click();
        cut.WaitForElement("[data-song-selection-bar]");

        Assert.Empty(cut.FindAll("button.song-selection-bar__more"));
        Assert.NotEmpty(cut.FindAll("button.song-selection-bar__done"));
        Assert.False(host.IsOpen);
    }

    private static IReadOnlyList<WorkViewModel> Tracks(int count) =>
        Enumerable.Range(1, count).Select(index => new WorkViewModel
        {
            Id = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
            CollectionId = Guid.NewGuid(),
            ArtistPersonId = Guid.NewGuid(),
            MediaType = "Music",
            CanonicalValues =
            [
                new CanonicalValueViewModel { Key = "title", Value = $"Track {index}" },
                new CanonicalValueViewModel { Key = "artist", Value = "The Artist" },
                new CanonicalValueViewModel { Key = "album", Value = "The Album" },
                new CanonicalValueViewModel { Key = "duration_sec", Value = "185" },
            ],
        }).ToList();

    private static void TickAndWait(IRenderedComponent<SongTable> cut, SongRow row, int count)
    {
        cut.Find($"[data-song-row-id='{row.Id}']").Click();
        cut.WaitForAssertion(() => Assert.Equal($"{count} selected", cut.Find("[data-song-selection-bar] [role='status']").TextContent.Trim()));
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
