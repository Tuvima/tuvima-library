using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// A test context with the live Libraries state wired to a stubbed Engine: the status line and the Activity list both read the
/// Engine's queue snapshot, its presentation snapshot and its added-history page, which a test sets before it renders.
/// </summary>
public abstract class LiveLibraryTestContext : AsyncBunitContext
{
    protected readonly UniverseStateContainer State = new();
    protected readonly List<string> Calls = [];
    protected Func<Task<IngestionPresentationSnapshotDto?>> Presentation =
        () => Task.FromResult<IngestionPresentationSnapshotDto?>(new IngestionPresentationSnapshotDto());
    protected Func<Task<IngestionQueueProgressDto?>> Queue = () => Task.FromResult<IngestionQueueProgressDto?>(null);
    protected List<IngestionMediaGroupDto> History = [];

    /// <summary>The signed-in session; it starts as an administrator who may open Activity (<see cref="SignInAs"/> changes that).</summary>
    protected readonly DashboardSessionAccessor Session = new();

    protected LiveLibraryTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionPresentationAsync), _ =>
            {
                Calls.Add("presentation");
                return Presentation();
            });
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ =>
            {
                Calls.Add("queue");
                return Queue();
            });
            stub.SetHandler(nameof(IEngineApiClient.GetRecentIngestionAdditionsAsync), _ =>
            {
                Calls.Add("history");
                return Task.FromResult<PagedResponse<IngestionMediaGroupDto>?>(new PagedResponse<IngestionMediaGroupDto>(History, 0, 25, false, History.Count));
            });
            stub.SetHandler(nameof(IEngineApiClient.TriggerRescanAsync), _ =>
            {
                Calls.Add("rescan");
                return Task.FromResult(true);
            });
        });
        SignInAs(administrator: true);
        Services.AddSingleton(Session);
        Services.AddSingleton(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(State);
        Services.AddSingleton(provider =>
        {
            var orchestrator = new UIOrchestratorService(
                api,
                State,
                new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
                provider.GetRequiredService<IConfiguration>(),
                NullLogger<UIOrchestratorService>.Instance);
            // A normal circuit: the live connection to the Engine is up.
            orchestrator.SetEngineConnectionState(EngineConnectionState.Online);
            return orchestrator;
        });
        Services.AddScoped<MediaEngine.Web.Services.Navigation.SettingsAttentionState>();
        Services.AddSingleton(provider => new IngestionLiveDashboardState(
            api,
            provider.GetRequiredService<UIOrchestratorService>(),
            State));
        Services.AddSingleton(provider => new ContextSidebarPreferences(
            provider.GetRequiredService<UIOrchestratorService>(),
            new NoPlaybackPreferences()));
        Services.AddSingleton<ContextSidebarCoordinator>();
        Render<AppPopoverHost>();
    }

    /// <summary>Signs the test session in as an administrator (who may open Activity) or as someone who may not.</summary>
    protected void SignInAs(bool administrator)
    {
        var profile = Guid.NewGuid();
        Session.Set("token", profile, profile, Guid.NewGuid(),
            new DashboardAuthorityResponse(Guid.NewGuid(), profile, true, true, 1, 1, administrator, administrator, null, 1, [], administrator ? ["settings.administration"] : [], []));
    }

    public sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<MediaEngine.Contracts.Playback.UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<MediaEngine.Contracts.Playback.UserPlaybackSettingsDto?>(null);

        public void UpdateCache(MediaEngine.Contracts.Playback.UserPlaybackSettingsDto settings)
        {
        }

        public void Invalidate()
        {
        }
    }
}
