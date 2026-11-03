using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Entry points over lists (universal editor v3 WP3): a list row's Edit, a Needs Review row's Fix, a library's Identify and the player's
/// lyrics button all open the one pop-over over the list through <see cref="ListEditLauncher"/>, on the right tool, with no navigation
/// away. The edit permission is the Engine's own per-item answer; without it, or on a device that may not edit, nothing opens. The
/// Engine is a hand-written stub; nothing is changed anywhere.
/// </summary>
public sealed class ListEditEntryPointTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid SongId = Guid.Parse("50000000-0000-0000-0000-0000000000a1");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(DetailEntityType Type, Guid Id)> _detailReads = [];
    private DetailPageViewModel? _detailAnswer;

    public ListEditEntryPointTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _detailAnswer = Page(withEdit: true);
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), args =>
            {
                _detailReads.Add(((DetailEntityType)args![0]!, (Guid)args[1]!));
                return Task.FromResult(_detailAnswer);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview { AffectedCount = 1, Fields = [] })));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        }));
    }

    private static DetailPageViewModel Page(bool withEdit, bool disabled = false, bool unmatched = false)
    {
        var actions = new List<DetailAction>();
        if (withEdit)
        {
            actions.Add(new DetailAction { Key = "edit", Label = "Edit this movie", Icon = "edit", IsDisabled = disabled, Tooltip = disabled ? "Updating details." : null });
        }

        if (unmatched)
        {
            actions.Add(new DetailAction { Key = DetailActionKeys.UnmatchedRetry, Label = "Try again", Icon = "sync" });
        }

        return new DetailPageViewModel
        {
            Id = MovieId.ToString("D"),
            EntityType = DetailEntityType.Movie,
            Title = "Dune: Part One",
            OverflowActions = actions,
        };
    }

    private Task UseDevice(string formFactor) => formFactor switch
    {
        "phone" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)),
        _ => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)),
    };

    private (ListEditLauncher Launcher, EditorHost Host, IRenderedComponent<EditorPopover> Popover) Parts()
    {
        var popover = Render<EditorPopover>();
        return (Services.GetRequiredService<ListEditLauncher>(), Services.GetRequiredService<EditorHost>(), popover);
    }

    private static ListEditTarget Movie() => ListEditTarget.ForLibraryRow(MovieId, "Dune: Part One", "Movie");

    [Fact]
    public async Task Fix_OpensThePopOverOnMatch_OverTheList_WithoutNavigatingAway()
    {
        await UseDevice("desktop");
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        var (launcher, host, popover) = Parts();

        Assert.True(await launcher.OpenAsync(Movie(), EditTool.Match));

        Assert.True(host.IsOpen);
        Assert.Equal(EditTool.Match, host.Session.ActiveTool);
        popover.WaitForAssertion(() => Assert.Single(popover.FindAll(".tl-editor-panel[data-tool='match']")));
        Assert.Equal(before, navigation.Uri);
        Assert.Equal(MovieId, host.Session.ActiveItem?.EntityId);
    }

    [Fact]
    public async Task Edit_OnALibraryRow_OpensOnDetails_AndAnUnmatchedRowOpensOnMatch()
    {
        await UseDevice("desktop");
        var (launcher, host, _) = Parts();
        Assert.True(await launcher.OpenAsync(Movie()));
        Assert.Equal(EditTool.Details, host.Session.ActiveTool);
        Assert.True(await host.RequestCloseAsync());

        _detailAnswer = Page(withEdit: true, unmatched: true);
        Assert.True(await launcher.OpenAsync(Movie()));
        Assert.Equal(EditTool.Match, host.Session.ActiveTool);
    }

    [Fact]
    public async Task TheEnginesAnswer_DecidesPermission_AndWithoutItNothingOpens()
    {
        await UseDevice("desktop");
        var (launcher, host, _) = Parts();

        _detailAnswer = Page(withEdit: false);
        Assert.False(await launcher.OpenAsync(Movie(), EditTool.Match));
        _detailAnswer = Page(withEdit: true, disabled: true);
        Assert.False(await launcher.OpenAsync(Movie(), EditTool.Match));
        _detailAnswer = null;
        Assert.False(await launcher.OpenAsync(Movie(), EditTool.Match));

        Assert.False(host.IsOpen);
        Assert.Equal(3, _detailReads.Count);
        Assert.Contains(Services.GetRequiredService<AppToastService>().Toasts, toast => toast.Message.Contains("Updating details", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnADeviceThatMayNotEdit_NothingIsAskedAndNothingOpens()
    {
        await UseDevice("phone");
        var (launcher, host, _) = Parts();

        Assert.False(await launcher.OpenAsync(Movie(), EditTool.Match));
        Assert.False(await launcher.OpenRowAsync(new BulkEditRow(SongId, "Airbag", DetailEntityType.Work, null, "Music")));

        Assert.False(host.IsOpen);
        Assert.Empty(_detailReads);
    }

    [Fact]
    public async Task ASongsEdit_OpensThePopOverWithTheSongTickedAndTheEditPanelOpen()
    {
        await UseDevice("desktop");
        var (launcher, host, popover) = Parts();
        var row = new BulkEditRow(SongId, "Airbag", DetailEntityType.Work, Guid.NewGuid(), "Music");

        Assert.True(await launcher.OpenRowAsync(row, DetailPresentationContext.Listen));

        Assert.True(host.IsOpen);
        Assert.Equal([SongId], host.Batch.Rows.Select(ticked => ticked.Id));
        Assert.Equal(BulkPanelKind.Edit, host.Batch.Panel);
        popover.WaitForAssertion(() => Assert.Equal("Editing 1 song", popover.Find(".tl-editor-panel__title").TextContent.Trim()));
        Assert.Contains(_detailReads, read => read.Id == SongId);
    }

    [Fact]
    public async Task ClosingAfterACommittedChange_TellsTheListWhichItemsChanged()
    {
        await UseDevice("desktop");
        var (launcher, host, _) = Parts();
        var closures = new List<EditorClosedEventArgs>();
        host.Closed += closures.Add;
        await launcher.OpenAsync(Movie(), EditTool.Match);

        await host.NotifyCommittedAsync();
        Assert.True(await host.RequestCloseAsync());

        var closed = Assert.Single(closures);
        Assert.True(closed.Changed);
        Assert.Contains(MovieId, closed.ItemIds);
    }

    [Fact]
    public async Task ClosingWithNothingChanged_TellsTheListNothingChanged_SoItDoesNotReload()
    {
        await UseDevice("desktop");
        var (launcher, host, _) = Parts();
        var closures = new List<EditorClosedEventArgs>();
        host.Closed += closures.Add;
        await launcher.OpenAsync(Movie(), EditTool.Match);

        Assert.True(await host.RequestCloseAsync());

        Assert.False(Assert.Single(closures).Changed);
    }

    [Fact]
    public async Task AListThatThrowsWhileRefreshing_DoesNotStopThePopOverClosing()
    {
        await UseDevice("desktop");
        var (launcher, host, _) = Parts();
        host.Closed += _ => throw new InvalidOperationException("a list failed to refresh");
        var later = 0;
        host.Closed += _ => later++;
        await launcher.OpenAsync(Movie());

        Assert.True(await host.RequestCloseAsync());

        Assert.False(host.IsOpen);
        Assert.Equal(1, later);
    }

    [Fact]
    public async Task TheLibraryRowsMediaTypeText_PicksTheEditorMediaTypeAndTheSurface()
    {
        Assert.Equal("Movies", ListEditLauncher.NormalizeMediaType("Movie"));
        Assert.Equal("TV", ListEditLauncher.NormalizeMediaType("TV Show"));
        Assert.Equal("Music", ListEditLauncher.NormalizeMediaType("Music"));
        Assert.Equal("Audiobooks", ListEditLauncher.NormalizeMediaType("Audiobook"));
        Assert.Equal("Comics", ListEditLauncher.NormalizeMediaType("Comic"));
        Assert.Equal("Books", ListEditLauncher.NormalizeMediaType("Epub"));
        Assert.Equal(DetailPresentationContext.Watch, ListEditTarget.ForLibraryRow(MovieId, "x", "TV").Context);
        Assert.Equal(DetailPresentationContext.Listen, ListEditTarget.ForLibraryRow(MovieId, "x", "Music").Context);
        await Task.CompletedTask;
    }
}
