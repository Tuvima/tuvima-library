using System.Reflection;
using Bunit;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The rail's Reorder mode: moves change only a staged copy, and Save order replays them to the page one move at a time.</summary>
public sealed class ReorderModeTests : AsyncBunitContext
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    public ReorderModeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/app-dialog.js").Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddScoped<DetailsOverlayState>();
    }

    private static MediaSectionNavigationItem Playlist(string name, Guid id) =>
        new(name, $"/p/{id:N}", AppMaterialIcons.Outlined.Movie, Management: new MediaSectionNavigationItemManagement(id, "Playlist"));

    private IRenderedComponent<MediaSectionShell> RenderShell(List<MediaSectionNavigationReorderEvent> events, bool accept = true) =>
        Render<MediaSectionShell>(parameters => parameters
            .Add(shell => shell.Title, "Listen")
            .Add(shell => shell.ChildContent, (RenderFragment)(_ => { }))
            .Add(shell => shell.NavigationGroups, (IReadOnlyList<MediaSectionNavigationGroup>)
            [
                new("Playlists", [Playlist("One", A), Playlist("Two", B), Playlist("Three", C)]),
            ])
            .Add(shell => shell.OnNavigationItemReorder, reorder =>
            {
                events.Add(reorder);
                return Task.FromResult(accept);
            }));

    private static async Task Call(IRenderedComponent<MediaSectionShell> cut, string method, params object[] args)
    {
        var info = typeof(MediaSectionShell).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = cut.InvokeAsync(() => info.Invoke(cut.Instance, args));
        if (await result is Task task)
        {
            await task;
        }

        cut.Render();
    }

    private static MediaSectionNavigationItem Row(IRenderedComponent<MediaSectionShell> cut, Guid id) =>
        cut.Instance.NavigationGroups[0].Items.Single(item => item.Management!.ContainerId == id);

    [Fact]
    public async Task Moving_InReorderMode_WritesNothingUntilSaveOrder()
    {
        var events = new List<MediaSectionNavigationReorderEvent>();
        var cut = RenderShell(events);
        await Call(cut, "BeginReorderMode", Row(cut, A));

        await Call(cut, "MoveManagedItemAsync", Row(cut, A), 1);
        await Call(cut, "MoveManagedItemAsync", Row(cut, A), 1);

        Assert.Empty(events);
        Assert.NotEmpty(cut.FindAll("[data-reorder-bar]"));
        Assert.Equal(["Two", "Three", "One"], cut.FindAll(".media-section-shell__rail-item--managed span").Select(span => span.TextContent.Trim()).Where(text => text.Length > 0).Take(3));
    }

    [Fact]
    public async Task SaveOrder_ReplaysTheStagedOrder_AndLeavesTheMode()
    {
        var events = new List<MediaSectionNavigationReorderEvent>();
        var cut = RenderShell(events);
        await Call(cut, "BeginReorderMode", Row(cut, A));
        await Call(cut, "MoveManagedItemAsync", Row(cut, A), 1);
        await Call(cut, "MoveManagedItemAsync", Row(cut, A), 1);

        await Call(cut, "SaveReorderAsync");

        // Applying the events in order to [A, B, C] must give [B, C, A].
        var order = new List<Guid> { A, B, C };
        foreach (var move in events)
        {
            order.Remove(move.Management.ContainerId);
            order.Insert(order.IndexOf(move.BeforeContainerId!.Value), move.Management.ContainerId);
        }

        Assert.Equal([B, C, A], order);
        Assert.Empty(cut.FindAll("[data-reorder-bar]"));
    }

    [Fact]
    public async Task AFingerDroppingARowBeforeOrAfterAnotherStagesTheOrderAndWritesNothingUntilSaveOrder()
    {
        var events = new List<MediaSectionNavigationReorderEvent>();
        var cut = RenderShell(events);
        await Call(cut, "BeginReorderMode", Row(cut, A));

        await cut.InvokeAsync(() => cut.Instance.TouchReorderManagedAsync(A.ToString("D"), C.ToString("D"), true));
        Assert.Equal(["Two", "Three", "One"], cut.FindAll(".media-section-shell__rail-item--managed span").Select(span => span.TextContent.Trim()).Where(text => text.Length > 0).Take(3));
        await cut.InvokeAsync(() => cut.Instance.TouchReorderManagedAsync(C.ToString("D"), B.ToString("D"), false));

        Assert.Equal(["Three", "Two", "One"], cut.FindAll(".media-section-shell__rail-item--managed span").Select(span => span.TextContent.Trim()).Where(text => text.Length > 0).Take(3));
        Assert.Empty(events);
        Assert.NotEmpty(cut.FindAll("[data-reorder-bar]"));
    }

    [Fact]
    public async Task AFingerDropOutsideReorderModeOrNamingAnUnknownRowChangesNothing()
    {
        var events = new List<MediaSectionNavigationReorderEvent>();
        var cut = RenderShell(events);

        await cut.InvokeAsync(() => cut.Instance.TouchReorderManagedAsync(A.ToString("D"), C.ToString("D"), true));
        await Call(cut, "BeginReorderMode", Row(cut, A));
        await cut.InvokeAsync(() => cut.Instance.TouchReorderManagedAsync(A.ToString("D"), Guid.NewGuid().ToString("D"), true));
        await cut.InvokeAsync(() => cut.Instance.TouchReorderManagedAsync("nope", C.ToString("D"), true));

        Assert.Equal(["One", "Two", "Three"], cut.FindAll(".media-section-shell__rail-item--managed span").Select(span => span.TextContent.Trim()).Where(text => text.Length > 0).Take(3));
    }

    [Fact]
    public async Task OnlyReorderModeDrawsGripsAndHandsTheRailToTheScriptForFingerDrags()
    {
        var module = JSInterop.SetupModule("./js/playlist-reorder.js");
        module.Mode = JSRuntimeMode.Loose;
        var cut = RenderShell([]);
        Assert.Empty(cut.FindAll("[data-playlist-reorder-handle]"));
        Assert.Empty(cut.FindAll("[data-playlist-reorder-row]"));

        await Call(cut, "BeginReorderMode", Row(cut, A));

        Assert.Equal(3, cut.FindAll("[data-playlist-reorder-handle]").Count);
        Assert.Equal([A, B, C], cut.FindAll("[data-playlist-reorder-row]").Select(row => Guid.Parse(row.GetAttribute("data-playlist-reorder-row")!)));
        var attach = Assert.Single(module.Invocations["attach"]);
        Assert.Equal("TouchReorderManagedAsync", attach.Arguments[2]);

        await Call(cut, "CancelReorder");

        Assert.Empty(cut.FindAll("[data-playlist-reorder-handle]"));
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["detach"]));
    }

    [Fact]
    public async Task Cancel_ThrowsTheStagedOrderAway()
    {
        var events = new List<MediaSectionNavigationReorderEvent>();
        var cut = RenderShell(events);
        await Call(cut, "BeginReorderMode", Row(cut, A));
        await Call(cut, "MoveManagedItemAsync", Row(cut, A), 1);

        await Call(cut, "CancelReorder");

        Assert.Empty(events);
        Assert.Empty(cut.FindAll("[data-reorder-bar]"));
    }
}
