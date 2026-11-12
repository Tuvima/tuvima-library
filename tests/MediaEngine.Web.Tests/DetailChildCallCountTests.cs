using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Counts Engine calls made by details-page children so parallel/cached reads stay that way.</summary>
public sealed class DetailChildCallCountTests : AsyncBunitContext
{
    private int _savedReads;

    public DetailChildCallCountTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetSavedItemsAsync),
            _ =>
            {
                Interlocked.Increment(ref _savedReads);
                return Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>([]);
            }));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    [Fact]
    public void MusicTrackList_ReadsProfileFavoritesOnce_AcrossParameterChanges()
    {
        var cut = Render<MusicTrackList>(p => p
            .Add(x => x.Groups, [new MediaGroupingViewModel { Key = "tracks" }])
            .Add(x => x.AlbumTitle, "Album"));
        var afterFirst = _savedReads;

        cut.Render(p => p
            .Add(x => x.Groups, [new MediaGroupingViewModel { Key = "tracks" }])
            .Add(x => x.AlbumTitle, "Album (updated)"));

        Assert.True(afterFirst <= 1);
        Assert.Equal(afterFirst, _savedReads);
    }
}
