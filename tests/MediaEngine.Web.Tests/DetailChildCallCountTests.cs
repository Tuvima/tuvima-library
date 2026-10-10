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
    private int _reactionReads;

    public DetailChildCallCountTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetProfileReactionsAsync),
            _ =>
            {
                Interlocked.Increment(ref _reactionReads);
                return Task.FromResult<IReadOnlyList<ProfileReactionDto>>([]);
            }));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
    }

    [Fact]
    public void AudioItemTable_ReadsProfileFavoritesOnce_AcrossParameterChanges()
    {
        var cut = Render<AudioItemTable>(p => p
            .Add(x => x.Groups, [new MediaGroupingViewModel { Key = "tracks" }])
            .Add(x => x.SourceTitle, "Album"));
        var afterFirst = _reactionReads;

        cut.Render(p => p
            .Add(x => x.Groups, [new MediaGroupingViewModel { Key = "tracks" }])
            .Add(x => x.SourceTitle, "Album (updated)"));

        Assert.True(afterFirst <= 1);
        Assert.Equal(afterFirst, _reactionReads);
    }
}
