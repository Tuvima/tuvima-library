using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Deleting a playlist from the Listen rail asks once in the shared confirm; only "Delete" deletes it.</summary>
public sealed class ListenPlaylistDeleteConfirmTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly Guid _playlistId = Guid.NewGuid();
    private readonly List<Guid> _deleted = [];

    public ListenPlaylistDeleteConfirmTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(ActiveId, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => Task.FromResult(new List<ManagedCollectionViewModel>
            {
                new() { Id = _playlistId, Name = "Road Trip", CollectionType = "Playlist" },
            }));
            stub.SetHandler(nameof(IEngineApiClient.DeleteCollectionAsync), args =>
            {
                _deleted.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddScoped<ListenAudioDragService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new DefaultPreferences());
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));
    }

    private (IRenderedComponent<AppDialogHost> Host, IRenderedComponent<ListenPage> Page) RenderRailWithOnePlaylist()
    {
        var host = Render<AppDialogHost>();
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.Section, "playlists"));
        page.WaitForElement(".listen-rail__playlist-row");
        return (host, page);
    }

    private void AskToDeleteThePlaylist(IRenderedComponent<AppDialogHost> host, IRenderedComponent<ListenPage> page)
    {
        page.Find(".listen-rail__playlist-row .app-overflow-menu__trigger").Click();
        // The click finishes only when the question is answered, so it is not awaited here.
        _ = page.FindAll("button.app-menu-item").Single(button => button.TextContent.Contains("Delete", StringComparison.Ordinal)).ClickAsync(new());
        host.WaitForElement(".app-confirm");
    }

    [Fact]
    public void ListenPage_DeletesThePlaylistWhenTheConfirmIsAccepted()
    {
        var (host, page) = RenderRailWithOnePlaylist();

        AskToDeleteThePlaylist(host, page);
        Assert.Empty(_deleted);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Delete").Click();

        page.WaitForAssertion(() => Assert.Equal([_playlistId], _deleted));
    }

    [Fact]
    public void ListenPage_DeletesNothingWhenThePlaylistConfirmIsCancelled()
    {
        var (host, page) = RenderRailWithOnePlaylist();

        AskToDeleteThePlaylist(host, page);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Empty(_deleted);
    }

    private sealed class DefaultPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(ActiveId));

        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
