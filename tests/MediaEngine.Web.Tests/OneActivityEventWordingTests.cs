using Bunit;
using MediaEngine.Contracts.Activity;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Spec ingestion-and-readiness WP7 (D8): one event reads the same on the live Library and Ingestion page, in the
/// Activity log and in the menu indicator's tooltip. The tooltip describes the Library's work as a whole, so it shares
/// the page's status sentence and its last-activity time; the page's Now row and the log share the item's title; the
/// log's time is the moment the page and the tooltip call the last activity.
/// </summary>
public sealed class OneActivityEventWordingTests : AsyncBunitContext
{
    private static readonly Guid DuneId = Guid.Parse("4f0a3a6e-0000-4000-8000-000000000001");

    private readonly UniverseStateContainer _state = new();
    private IngestionPresentationSnapshotDto _presentation = new();
    private IngestionQueueProgressDto? _queue;
    private PagedResponse<ActivityLogEntryDto> _log = new([], 0, 50, false, 0);

    public OneActivityEventWordingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionPresentationAsync), _ =>
                Task.FromResult<IngestionPresentationSnapshotDto?>(_presentation));
            stub.SetHandler(nameof(IEngineApiClient.GetIngestionQueueProgressAsync), _ =>
                Task.FromResult(_queue));
            stub.SetHandler(nameof(IEngineApiClient.GetActivityLogAsync), _ =>
                Task.FromResult<PagedResponse<ActivityLogEntryDto>?>(_log));
        });
        Services.AddSingleton(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(_state);
        Services.AddSingleton(provider =>
        {
            var orchestrator = new UIOrchestratorService(
                api,
                _state,
                new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
                provider.GetRequiredService<IConfiguration>(),
                NullLogger<UIOrchestratorService>.Instance);
            orchestrator.SetEngineConnectionState(EngineConnectionState.Online);
            return orchestrator;
        });
        Services.AddSingleton(provider => new IngestionLiveDashboardState(
            api,
            provider.GetRequiredService<UIOrchestratorService>(),
            _state));
        Services.AddSingleton(provider => new ContextSidebarPreferences(
            provider.GetRequiredService<UIOrchestratorService>(),
            new NoPlaybackPreferences()));
        Services.AddSingleton<ContextSidebarCoordinator>();
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        Services.AddSingleton<ShellActivityState>();
        Render<AppPopoverHost>();
    }

    [Fact]
    public void OneEvent_ReadsTheSameOnTheLivePageInTheActivityLogAndInTheMenuTooltip()
    {
        // The event: Dune finishes being added while the Library works through 508 files.
        var addedAt = DateTimeOffset.UtcNow.AddMinutes(-2).AddSeconds(-20);
        var dune = new IngestionMediaGroupDto
        {
            GroupId = DuneId,
            MediaType = "Movies",
            Title = "Dune",
            Availability = "ready",
            StatusLabel = "Ready",
            UpdatedAt = addedAt,
        };
        _presentation = new IngestionPresentationSnapshotDto { IsRunning = true, CurrentMedia = [dune], CurrentMediaTotal = 1 };
        _queue = Queue("working", addedAt, filesDone: 254);
        _log = new PagedResponse<ActivityLogEntryDto>(
            [new ActivityLogEntryDto
            {
                Id = 1,
                OccurredAt = addedAt,
                Category = ActivityLogCategoryValues.Added,
                Kind = "MediaAdded",
                Title = "Dune",
                MediaType = "Movies",
            }],
            0, 50, false, 1);

        // While the Library works: the page's status sentence is the tooltip's, and the Now row names Dune as the log does.
        var page = Render<IngestionTasksTab>();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll($"[data-group-id='{DuneId}']")));
        _state.PushIngestionQueueProgress(_queue);
        var indicator = Render<SystemActivityIndicator>();
        var log = Render<ActivityLogPanel>(parameters => parameters.Add(p => p.Filter, ActivityLogCategoryValues.Added));
        log.WaitForAssertion(() => Assert.NotEmpty(log.FindAll("[data-testid='activity-log-row']")));

        var workingSentence = $"{page.Find(".ingestion-status h2").TextContent.Trim()} · {page.Find(".ingestion-status__line").TextContent.Trim()}";
        Assert.StartsWith(workingSentence, Tooltip(indicator), StringComparison.Ordinal);

        var nowTitle = page.Find($"[data-group-id='{DuneId}'] .ingestion-media-list-row__open strong").TextContent.Trim();
        var logRow = log.Find("[data-testid='activity-log-row']");
        Assert.Equal("Dune", nowTitle);
        Assert.Equal(nowTitle, logRow.QuerySelector(".activity-log__title")!.TextContent.Trim());

        // Once the Library is idle: the page and the tooltip give the same last-activity time, and it is the log row's moment.
        var idle = Queue("idle", addedAt, filesDone: 508);
        _state.PushIngestionQueueProgress(idle);
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll(".ingestion-status.is-idle")));
        indicator.WaitForAssertion(() => Assert.StartsWith(SystemActivityText.IdleLabel, Tooltip(indicator), StringComparison.Ordinal));

        var pageLastActivity = page.Find(".ingestion-status__meta").TextContent.Trim();
        Assert.Equal("Last activity 2 minutes ago", pageLastActivity);
        Assert.Equal($"{SystemActivityText.IdleLabel} · {pageLastActivity}", Tooltip(indicator));
        Assert.Equal(addedAt.ToString("O"), logRow.QuerySelector("time")!.GetAttribute("datetime"));
    }

    private static string Tooltip(IRenderedComponent<SystemActivityIndicator> cut) =>
        cut.Find("[data-playback-tooltip]").GetAttribute("data-playback-tooltip")!;

    private static IngestionQueueProgressDto Queue(string state, DateTimeOffset lastActivity, int filesDone) => new()
    {
        State = state,
        FilesFound = 508,
        FilesDone = filesDone,
        Progress = filesDone / 508d,
        Tally = [new IngestionQueueTypeTallyDto { MediaType = "Movies", Files = 508 }],
        StartedAt = lastActivity.AddMinutes(-10),
        LastActivityAt = lastActivity,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
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
