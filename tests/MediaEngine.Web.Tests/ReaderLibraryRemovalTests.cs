using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A title that is deleted from the library while it is open in a reader (the reader route or the book reader itself): the reader stops,
/// says why, leaves, and stops listening once the page is gone.
/// </summary>
public sealed class ReaderLibraryRemovalTests : AsyncBunitContext
{
    private static readonly Guid AssetId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000001");

    private readonly UIOrchestratorService _orchestrator;

    public ReaderLibraryRemovalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(api);
        var profiles = new ActiveProfileSessionService(null!, api);
        Services.AddSingleton(profiles);
        _orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        Services.AddSingleton(_orchestrator);
        Services.AddSingleton<IUserPlaybackPreferencesAccessor, NoPreferences>();

        // What the foliate reader's script answers while it opens a book.
        var module = JSInterop.SetupModule("./js/book-reader.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<BookReaderOpenOptions>("loadSettings", _ => true)
            .SetResult(ReaderProfileSettings.ToOpenOptions(UserPlaybackSettingsDto.CreateDefaults(Guid.Empty).Reading));
        module.Setup<BookReaderOpenResult>("open", _ => true)
            .SetResult(new BookReaderOpenResult { Ok = true, Title = "Dune", Layout = "reflowable" });
        module.Setup<List<BookReaderTocItem>>("getToc").SetResult([]);
    }

    private IReadOnlyList<string> Toasts => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    private IEnumerable<Delegate> Listeners() =>
        (typeof(UIOrchestratorService).GetField(nameof(UIOrchestratorService.OnLibraryItemsRemoved), BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(_orchestrator) as Delegate)?.GetInvocationList() ?? [];

    [Fact]
    public async Task TheReaderRoute_LeavesWithAToastWhenItsFileIsDeleted()
    {
        var cut = Render<EpubReader>(parameters => parameters.Add(page => page.AssetId, AssetId));
        var manager = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        manager.NavigateTo($"/read/{AssetId}");

        await cut.InvokeAsync(() => _orchestrator.RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([Guid.NewGuid()], [AssetId])));

        cut.WaitForAssertion(() => Assert.Equal(["This item is no longer in your library."], Toasts));
        Assert.Equal(manager.BaseUri, manager.Uri);
    }

    [Fact]
    public async Task TheReaderRoute_IgnoresTheDeletionOfAnotherFile()
    {
        var cut = Render<EpubReader>(parameters => parameters.Add(page => page.AssetId, AssetId));
        var manager = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        manager.NavigateTo($"/read/{AssetId}");

        await cut.InvokeAsync(() => _orchestrator.RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([Guid.NewGuid()], [Guid.NewGuid()])));

        Assert.Empty(Toasts);
        Assert.EndsWith($"/read/{AssetId}", manager.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReaderRoute_ListensOnlyThroughTheBookReaderSoOneDeletionToastsOnce()
    {
        Render<EpubReader>(parameters => parameters.Add(page => page.AssetId, AssetId));

        Assert.DoesNotContain(Listeners(), listener => listener.Target is EpubReader);
        Assert.Single(Listeners(), listener => listener.Target is BookReader);
    }

    [Fact]
    public async Task TheBookReader_StopsListeningOnceThePageIsLeft()
    {
        Render<BookReader>(parameters => parameters.Add(page => page.AssetId, AssetId));
        Assert.Contains(Listeners(), listener => listener.Target is BookReader);

        await DisposeComponentsAsync();

        Assert.DoesNotContain(Listeners(), listener => listener.Target is BookReader);
    }

    private sealed class NoPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);

        public void UpdateCache(UserPlaybackSettingsDto settings) { }

        public void Invalidate() { }
    }
}
