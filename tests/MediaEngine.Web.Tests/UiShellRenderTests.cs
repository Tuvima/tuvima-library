using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using MudBlazor;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class UiShellRenderTests : AsyncBunitContext
{
    public UiShellRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string>("detectDeviceClass").SetResult("web");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:BaseUrl"] = "http://localhost:61495",
            })
            .Build();

        var apiClient = EngineApiClientStub.CreateDefault();

        Services.AddLocalization();
        Services.AddLogging();
        Services.AddMudServices();
        Services.AddSingleton<IConfiguration>(configuration);
        Services.AddSingleton<IEngineApiClient>(apiClient);
        Services.AddSingleton<IWebHostEnvironment>(new TestWebHostEnvironment());
        Services.AddSingleton<ThemeService>();
        Services.AddScoped<DeviceContextService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddScoped<MediaTileComposerService>();
        Services.AddSingleton(new ListenPlaybackClientSettings());
        Services.AddScoped<PlaybackSessionController>();
        Services.AddScoped<PlaybackTransientToolCoordinator>();
        Services.AddScoped<VideoPresentationResolver>();
        Services.AddScoped<AudiobookBookmarkActionService>();
        Services.AddScoped<IAudiobookBookmarkActions>(provider => provider.GetRequiredService<AudiobookBookmarkActionService>());
        Services.AddScoped<IAudiobookBookmarkLeaseInvalidator>(provider => provider.GetRequiredService<AudiobookBookmarkActionService>());
        Services.AddScoped<ListenPlaybackCommandOwner>();
        Services.AddScoped<IPlaybackCommandSink, DirectPlaybackCommandSink>();
        Services.AddScoped<PlaybackLyricsSelectionOwner>();
        Services.AddScoped<PlaybackLyricsPresenter>();
        Services.AddScoped<ShellActivityState>();
        Services.AddScoped<ActivityNotificationQueue>();
        Services.AddSingleton(new DashboardAuthUiOptions(false));
        Services.AddScoped<ListenAudioDragService>();
        Services.AddScoped<IUserPlaybackPreferencesAccessor, UserPlaybackPreferencesAccessor>();
        Services.AddScoped<ContextSidebarPreferences>();
        Services.AddScoped<IContextSidebarPreferences>(services => services.GetRequiredService<ContextSidebarPreferences>());
        Services.AddScoped<ListenContextSidebarState>();
        Services.AddScoped<ContextSidebarCoordinator>();
        Services.AddScoped<MediaReactionService>();
        Services.AddScoped<SavedItemService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddSingleton<IAdministratorSurfaceAccessService>(new AlwaysUnlockedAdministratorSurfaceAccessService());
        var profileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profileId, profileId, true, true, 1, 1, true, true, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profileId, profileId, "Test User", null,
                true, true, true, new MediaEngine.Contracts.Authentication.GrantAdminProtectionDto(
                    false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], ["access.manage"]);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", profileId, profileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new DelayedLockHandler(authority, session)));
        Services.AddScoped<DashboardIdentityClient>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedIdentityNavigationRevealsDetailsAndPreservesTheCurrentAudioSession(bool popupSender)
    {
        var profile = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var book = Guid.NewGuid(); var asset = Guid.NewGuid();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ =>
            Task.FromResult<MediaEngine.Contracts.Details.DetailPageViewModel?>(new() { Id = book.ToString("D"), EntityType = MediaEngine.Contracts.Details.DetailEntityType.Audiobook })));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new FixedNavigationPreferences(profile));
        Services.AddScoped<PlaybackIdentityNavigationOwner>();
        var playback = Services.GetRequiredService<PlaybackSessionController>();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        playback.RestoreState(new() { ProfileId = profile, Queue = [new() { WorkId = book, AudiobookWorkId = book,
            AssetId = asset, MediaType = "Audiobooks", Title = "Book", StreamUrl = "/stream/book", Duration = "10:00" }],
            CurrentIndex = 0, CurrentTimeSeconds = 42, DurationSeconds = 600, PlaybackRate = 1.25,
            PlaybackRequestVersion = 9, IsPopupOpen = true,
            SleepTimerState = new() { ProfileId = profile, WorkId = book, BoundAssetId = asset, PlaybackRequestVersion = 9,
                Mode = AudiobookSleepTimerModes.Timer, ChosenMinutes = 30, DeadlineUtc = deadline, TimerSessionId = Guid.NewGuid(), TimerGeneration = 12 } });
        var prepared = playback.CreateSnapshot();
        Assert.Equal(deadline, prepared.SleepTimerState.DeadlineUtc);
        var layout = Render<MainLayout>(parameters => parameters.Add(component => component.Body, builder => builder.AddContent(0, "Current page")));
        await layout.InvokeAsync(() => { playback.RestoreState(prepared); playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying); playback.UpdateTransportState(currentTimeSeconds: 42, isPlaying: true); });
        var before = playback.CreateSnapshot();
        var lease = playback.AudiobookBookSessionLeaseId;
        var transportCommands = 0;
        playback.TransportCommandRequested += _ => { transportCommands++; return Task.CompletedTask; };
        var owner = Services.GetRequiredService<ListenPlaybackCommandOwner>();
        var command = new ListenPlaybackCommandDto { Action = ListenPlaybackPresentationActions.NavigateIdentity,
            CommandId = Guid.NewGuid(), SenderId = popupSender ? Guid.NewGuid() : owner.RecipientId, RecipientId = owner.RecipientId,
            ProfileId = profile, WorkId = book, ExpectedAssetId = asset, ExpectedPlaybackRequestVersion = before.PlaybackRequestVersion,
            IdentityKind = "audiobook", IdentityId = book };
        Assert.False((await owner.HandleAsync(command with { CommandId = Guid.NewGuid(), ExpectedAssetId = Guid.NewGuid() }))?.BooleanResult);
        Assert.Equal(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface);
        var tools = Services.GetRequiredService<PlaybackTransientToolCoordinator>();
        tools.Open("audio-chapters", true);
        Assert.True((await owner.HandleAsync(command))?.BooleanResult);
        Assert.EndsWith($"/details/audiobook/{book:D}?context=listen", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal(PlaybackPresentationSurface.Docked, playback.PresentationSurface);
        Assert.Null(tools.OpenToolId);
        Assert.Equal(before.Queue[0].AssetId, playback.CurrentItem?.AssetId);
        Assert.Equal(before.Queue[0].StreamUrl, playback.CurrentItem?.StreamUrl);
        Assert.Equal(before.PlaybackRequestVersion, playback.PlaybackRequestVersion);
        Assert.Equal(before.CurrentTimeSeconds, playback.CurrentTimeSeconds);
        Assert.Equal(before.PlaybackRate, playback.PlaybackRate);
        Assert.Equal(before.IsPlaying, playback.IsPlaying);
        Assert.Equal(before.SleepTimerState, playback.SleepTimerState);
        Assert.Equal(deadline, playback.SleepTimerState.DeadlineUtc);
        Assert.Equal(lease, playback.AudiobookBookSessionLeaseId);
        Assert.True(playback.IsPopupOpen);
        Assert.Equal(0, transportCommands);
    }

    private sealed class FixedNavigationPreferences(Guid profile) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profile;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    [Theory]
    [InlineData(true, "Work")]
    [InlineData(false, "Work")]
    [InlineData(true, "Collection")]
    [InlineData(false, "Collection")]
    public void StructuralCollectionEditor_SavesParentOverridesAndKeepsFailuresOpen(bool saveSucceeds, string entityKind)
    {
        var rootId = Guid.NewGuid();
        Guid? savedId = null;
        Dictionary<string, string>? savedFields = null;
        bool? closed = null;
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(entityKind == "Collection" ? nameof(IEngineApiClient.SaveCollectionDisplayOverridesAsync) : nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
            {
                savedId = (Guid)args![0]!;
                savedFields = (Dictionary<string, string>)args[1]!;
                return Task.FromResult(saveSucceeds);
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdateCollectionAsync), _ =>
                throw new InvalidOperationException("A shelf must not be converted to curated membership."));
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Render<MudPopoverProvider>();
        var cut = Render<CollectionEditorShell>(parameters => parameters
            .Add(component => component.Inline, true)
            .Add(component => component.Closed, result => closed = result)
            .Add(component => component.Request, new CollectionEditorLaunchRequest
            {
                EditingCollection = new CollectionListItemViewModel
                {
                    Id = rootId, Name = "Original series", Description = "Series description", CollectionType = "Series",
                },
                StructuralEntityKind = entityKind,
                StructuralDetail = new MediaEngine.Contracts.Details.DetailPageViewModel
                {
                    Id = Guid.NewGuid().ToString(), Title = "Original series",
                },
            }));

        Assert.Contains("Collection editor", cut.Markup);
        Assert.DoesNotContain("Ownership and visibility", cut.Markup);
        Assert.DoesNotContain("Delete collection", cut.Markup);
        Assert.Equal(new[] { "Details", "Artwork", "Match & Identity", "History" },
            cut.FindAll("nav button").Select(button => button.TextContent.Trim()));
        cut.Find("input").Input("Renamed series");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(rootId, savedId);
            Assert.Equal("Renamed series", savedFields!["title"]);
            Assert.Single(savedFields);
            Assert.Equal(saveSucceeds ? true : (bool?)null, closed);
        });
    }

    [Fact]
    public void MainLayout_RendersMudShellAndBody()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section id=\"test-body\">Body content</section>")));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".mud-appbar"));
            Assert.Equal(5, cut.FindAll(".layout-shell__nav-link").Count);
            Assert.Contains(cut.FindAll(".layout-shell__nav-link"),
                link => string.Equals(link.GetAttribute("href"), "/view", StringComparison.Ordinal));
            Assert.Equal("/images/library.svg", cut.Find(".layout-shell__brand-lockup").GetAttribute("src"));
            Assert.Empty(cut.FindAll(".layout-shell__mobile-menu"));
            Assert.Contains("Body content", cut.Markup);
            Assert.Equal(6, cut.FindAll(".layout-shell__intent-dock-link").Count);
            Assert.Contains(cut.FindAll(".layout-shell__intent-dock-link"),
                link => string.Equals(link.GetAttribute("href"), "/", StringComparison.Ordinal));
            Assert.DoesNotContain("Search your library", cut.Markup);
            Assert.Empty(cut.FindAll(".layout-shell__search-shell"));
            Assert.Single(cut.FindAll(".layout-shell__search-action"));
            Assert.Single(cut.FindAll(".top-nav-account-menu__trigger"));
            Assert.Single(cut.FindAll(".layout-shell__my-list"));
            Assert.Empty(cut.FindAll(".system-activity-indicator"));
            Assert.Empty(cut.FindAll(".layout-shell__review-button"));
            Assert.Empty(cut.FindAll(".layout-shell__avatar-trigger"));
        });
    }

    [Fact]
    public void MainLayout_StartsLiveUpdatesAndUsesDedicatedNavbarComponents()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor"));
        var accountSource = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Navigation", "TopNavAccountMenu.razor"));
        var accountCss = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Navigation", "TopNavAccountMenu.razor.css"));
        var activityIndicator = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Navigation", "SystemActivityIndicator.razor"));
        var activityIndicatorCss = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Navigation", "SystemActivityIndicator.razor.css"));
        var globalCss = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "app.css"));
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor.css"));

        Assert.Contains("await Orchestrator.StartSignalRAsync();", source);
        Assert.Contains("await Activity.InitializeAsync();", source);
        var activityState = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Services", "Integration", "ShellActivityState.cs"));
        Assert.Contains("LiveIngestionActivityTtl", activityState);
        Assert.Contains("RefreshSystemActivityAsync", activityState);
        Assert.Contains("LastStateChangeRequiresSnapshotRefresh", activityState);
        Assert.Contains("BatchProgressReceivedAt", activityState);
        Assert.Contains("IngestionProgressReceivedAt", activityState);
        Assert.Contains("<TopNavAccountMenu", source);
        Assert.Contains("<SystemActivityIndicator", source);
        Assert.Contains("ShellActivityKind.Ingestion or ShellActivityKind.Enrichment", activityIndicator);
        Assert.Contains("system-activity-indicator__ring is-animated", activityIndicator);
        Assert.Contains("@keyframes system-activity-ring-spin", activityIndicatorCss);
        Assert.Contains("/images/library.svg", source);
        Assert.DoesNotContain("<AppLogo", source);
        Assert.Contains("Nav_Search", source);
        Assert.Contains("OpenSearch", source);
        Assert.DoesNotContain("<MudTextField", source);
        Assert.Contains("TopBar_MyList", source);
        Assert.DoesNotContain("NotificationsNone", source);
        Assert.DoesNotContain("ToggleProfileMenu", source);
        Assert.Contains("@if (CanViewReview)", accountSource);
        Assert.Contains("TopBar_NeedsReview", accountSource);
        Assert.Contains("ShowSignOut", accountSource);
        Assert.Contains("href=\"/settings/recently-added?review=expanded\"", accountSource);
        Assert.Contains("href=\"/settings\"", accountSource);
        Assert.DoesNotContain("<MudMenu", accountSource);
        Assert.DoesNotContain("SettingsSelected", accountSource);
        Assert.DoesNotContain("ReviewSelected", accountSource);
        Assert.DoesNotContain("NotificationsNone", accountSource);
        Assert.Contains("top-nav-account-menu__trigger-copy", accountSource);
        Assert.Contains("href=\"/settings/profile\"", accountSource);
        Assert.Contains("Switch profile?", accountSource);
        Assert.Contains("ProfileSwitchStatus.PinRequired", accountSource);
        Assert.Contains("font-family: var(--font-brand);", css);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) auto minmax(0, 1fr);", css);
        Assert.Contains("grid-column: 2;", css);
        Assert.Contains("font-family: var(--font-ui);", accountCss);
        Assert.Contains("z-index: var(--tl-z-popover, 1600);", accountCss);
        Assert.Contains(".top-nav-account-menu__panel ::deep .top-nav-account-menu__item", accountCss);
        Assert.Contains("display: flex;", accountCss);
        Assert.Contains("width: 100%;", accountCss);
        Assert.Contains("border-radius: var(--tl-radius-lg, 12px);", accountCss);
    }

    [Fact]
    public async Task MainLayoutInvalidatesChangedBookmarkLeasesWhileSuccessfulReplayDismissesAndRestoresFocus()
    {
        var profileId = Guid.NewGuid();
        var otherProfileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var firstAssetId = Guid.NewGuid();
        var replayAssetId = Guid.NewGuid();
        var bookmark = new MediaEngine.Contracts.Playback.AudiobookBookmarkDto
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            WorkId = workId,
            AssetId = replayAssetId,
            PositionSeconds = 45,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetAudiobookBookmarksWithOutcomeAsync), _ =>
                Task.FromResult(AudiobookBookmarkOperationResult<IReadOnlyList<MediaEngine.Contracts.Playback.AudiobookBookmarkDto>>
                    .Succeeded([bookmark]))));
        Services.AddSingleton<IEngineApiClient>(api);

        var playback = Services.GetRequiredService<PlaybackSessionController>();
        var sourceItem = new ListenQueueItem
        {
            WorkId = workId,
            AudiobookWorkId = workId,
            AssetId = firstAssetId,
            MediaType = "Audiobooks",
            Title = "A Test Audiobook",
            StreamUrl = "https://media.invalid/source-a.mp3",
            Chapters =
            [
                new PlaybackChapterDto
                {
                    AssetId = firstAssetId,
                    Index = 0,
                    Title = "Current chapter",
                    StartSeconds = 0,
                    EndSeconds = 120,
                },
                new PlaybackChapterDto
                {
                    AssetId = replayAssetId,
                    Index = 0,
                    Title = "Saved chapter",
                    StartSeconds = 0,
                    EndSeconds = 120,
                },
            ],
        };
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [sourceItem],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            IsPlaying = false,
        });
        var sessionLeaseId = Assert.IsType<Guid>(playback.AudiobookBookSessionLeaseId);
        var ownerGeneration = playback.AudiobookBookSessionGeneration;
        var actions = Services.GetRequiredService<AudiobookBookmarkActionService>();
        var currentContext = new AudiobookBookmarkActionContext(Guid.NewGuid(), profileId, workId,
            sessionLeaseId, ownerGeneration, firstAssetId);
        var subjectContext = currentContext with { DialogId = Guid.NewGuid(), WorkId = Guid.NewGuid() };
        var profileContext = currentContext with { DialogId = Guid.NewGuid(), ProfileId = otherProfileId };
        foreach (var context in new[] { currentContext, subjectContext, profileContext })
            await actions.OpenAsync(context);
        Assert.True(actions.TryCapture(currentContext, firstAssetId, new HashSet<Guid> { firstAssetId, replayAssetId },
            10, 120, null, out _));
        await actions.LoadSavedAsync(currentContext, new HashSet<Guid> { firstAssetId, replayAssetId });

        var layoutOwner = new ReplayInvalidatingBookmarkActions(actions, playback, currentContext,
            sourceItem with { AssetId = replayAssetId, StreamUrl = "https://media.invalid/source-b.mp3" });
        var sheetModule = JSInterop.SetupModule("./js/playback-tool-sheet.js");
        sheetModule.SetupVoid("attachModal");
        sheetModule.SetupVoid("restoreFocus");
        var layout = Render<MainLayout>(parameters => parameters.Add(component => component.Body, builder =>
        {
            builder.OpenComponent<BookmarkDialogTestHost>(0);
            builder.AddAttribute(1, nameof(BookmarkDialogTestHost.Context), currentContext);
            builder.AddAttribute(2, nameof(BookmarkDialogTestHost.Actions), layoutOwner);
            builder.AddAttribute(3, nameof(BookmarkDialogTestHost.AuthorizedAssetIds),
                new HashSet<Guid> { firstAssetId, replayAssetId });
            builder.AddAttribute(4, nameof(BookmarkDialogTestHost.WorkTitle), "A Test Audiobook");
            builder.CloseComponent();
        }));

        typeof(MainLayout).GetField("_activeProfileId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(layout.Instance, profileId);

        await layout.Find("button.audiobook-bookmark-dialog__tab:not(.is-selected)").ClickAsync();
        await layout.Find("button.audiobook-bookmark-dialog__replay").ClickAsync();

        Assert.Equal("Playback changed. This captured bookmark is no longer active.", layoutOwner.InvalidatedReplayMessage);
        Assert.Contains("Playback changed", actions.GetSnapshot(subjectContext.DialogId).Message ?? string.Empty);
        Assert.Contains("Playback changed", actions.GetSnapshot(profileContext.DialogId).Message ?? string.Empty);
        var closedContextSnapshot = await actions.GetSnapshotAsync(currentContext);
        Assert.Null(closedContextSnapshot.Draft);
        Assert.Equal("This bookmark dialog is no longer active.", closedContextSnapshot.Message);
        Assert.Empty(layout.FindAll(".audiobook-bookmark-dialog-layer"));
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "restoreFocus");
    }

    [Fact]
    public void MainLayout_CancelsStartupAndUnregistersObserversAfterDisposal()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor"));
        var disposeStart = source.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal);
        Assert.True(disposeStart >= 0);
        var dispose = source[disposeStart..];

        var afterRenderStart = source.IndexOf("protected override async Task OnAfterRenderAsync", StringComparison.Ordinal);
        var afterRender = source[afterRenderStart..disposeStart];
        Assert.Contains("if (_disposed) return;", afterRender, StringComparison.Ordinal);
        Assert.Contains("if (!firstRender) return;", afterRender, StringComparison.Ordinal);
        Assert.Contains("var startupToken = _authorityRefreshCancellation.Token;", source, StringComparison.Ordinal);
        Assert.Contains("ShouldStopStartup(startupToken)", source, StringComparison.Ordinal);
        Assert.Contains("UnregisterBrowserObserversDuringStartupAsync", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("tuvimaResponsive.registerDeviceClassObserver", StringComparison.Ordinal)
                    < source.IndexOf("await deviceInitialization;", StringComparison.Ordinal));
        Assert.True(dispose.IndexOf("_disposed = true;", StringComparison.Ordinal)
                    < dispose.IndexOf("_authorityRefreshCancellation.Cancel();", StringComparison.Ordinal));
        Assert.Contains("tuvimaResponsive.unregisterDeviceClassObserver", dispose, StringComparison.Ordinal);
        Assert.Contains("playbackTools.disconnectDock", dispose, StringComparison.Ordinal);
        Assert.Contains("unregisterCtrlK\", _deviceObserverOwner", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Orchestrator_RetriesLiveUpdatesWhenHealthyStatusFindsDisconnectedSignalR()
    {
        var source = File.ReadAllText(GetRepoFile(
            "src", "MediaEngine.Web", "Services", "Integration", "UIOrchestratorService.cs"));

        Assert.Contains("status.IsHealthy && !IsIntercomConnected", source);
        Assert.Contains("await StartSignalRAsync(ct);", source);
        Assert.Contains("HubConnectionState.Disconnected", source);
        Assert.Contains("A later call can retry", source);
    }

    [Fact]
    public async Task SettingsPage_RendersSharedSidebarAndAdminOverviewContent()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "system");
            builder.AddAttribute(5, nameof(Settings.Subsection), "overview");
            builder.CloseComponent();
        });

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Expand Library & Ingestion']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".media-section-shell"));
            Assert.Single(cut.FindAll(".media-section-shell__rail"));
            Assert.NotEmpty(cut.FindAll(".media-section-shell__rail-item"));
            var recentlyAdded = cut.FindAll(".media-section-shell__rail-item--child")
                .Single(item => item.TextContent.Contains("Recently Added", StringComparison.Ordinal));
            var reviewNotification = recentlyAdded.QuerySelector(".media-section-shell__rail-meta--notification");
            Assert.NotNull(reviewNotification);
            Assert.Equal("3", reviewNotification!.TextContent.Trim());
            Assert.Equal("3 items need review", reviewNotification.GetAttribute("aria-label"));
            Assert.Contains("System exceptions", cut.Markup);
            Assert.NotNull(cut.Find(".admin-library-summary").QuerySelector(".admin-overview-card--attention"));
            Assert.DoesNotContain("No active transcodes", cut.Markup);
            Assert.Contains("Recent Activity", cut.Markup);
        });
    }

    [Fact]
    public async Task Settings_AdminToPersonalTransition_LocksAndClearsActionsBeforeDeleteCompletes()
    {
        var profile = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profile, profile, true, true, 1, 1, true, true, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new MediaEngine.Contracts.Authentication.GrantAdminProtectionDto(true, "LockOnLeave", 30, 1, false, null), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], ["access.manage", "administrator.lock"]);
        var session = new DashboardSessionAccessor();
        session.Set("session", profile, profile, Guid.NewGuid(), authority);
        var handler = new DelayedLockHandler(authority, session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => session);

        var host = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0); builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1); builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "system");
            builder.AddAttribute(5, nameof(Settings.Subsection), "overview");
            builder.CloseComponent();
        });
        var cut = host.FindComponent<Settings>();
        cut.WaitForAssertion(() => Assert.True(session.ShouldLockOnLeave));
        cut.Render(parameters => parameters.Add(page => page.Section, "profile"));

        await handler.DeleteObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(handler.ActionsWereClearedBeforeDelete);
        Assert.True(handler.DeleteObserved.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Settings_DirectAdminRoute_InitializesCircuitAuthorityBeforeResolvingRoute()
    {
        var profile = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profile, profile, true, true, 1, 1, true, false, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new MediaEngine.Contracts.Authentication.GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], []);
        var session = new DashboardSessionAccessor();
        var handler = new DelayedLockHandler(authority, session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => session);

        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0); builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1); builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "access");
            builder.AddAttribute(5, nameof(Settings.Subsection), "users");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.True(handler.ValidationRequested);
            Assert.True(session.HasNavigation("settings.administration"));
            Assert.Single(cut.FindAll("input[aria-label='Administrator PIN']"));
            Assert.Empty(cut.FindAll("[data-access-section='users']"));
        });
    }

    [Fact]
    public void MainLayoutAndSettings_FirstAdminRoute_ShareTheInitialAuthorityValidation()
    {
        var profile = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profile, profile, true, true, 1, 1, true, false, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new MediaEngine.Contracts.Authentication.GrantAdminProtectionDto(false, "None", null, 1, false, null), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], []);
        var session = new DashboardSessionAccessor();
        var handler = new DelayedLockHandler(authority, session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => session);

        var cut = Render<MainLayout>(parameters => parameters.Add(layout => layout.Body, builder =>
        {
            builder.OpenComponent<Settings>(0);
            builder.AddAttribute(1, nameof(Settings.Section), "access");
            builder.AddAttribute(2, nameof(Settings.Subsection), "users");
            builder.CloseComponent();
        }));

        cut.WaitForAssertion(() =>
        {
            Assert.True(handler.ValidationRequested);
            Assert.True(session.HasNavigation("settings.administration"));
            Assert.Single(cut.FindAll("input[aria-label='Administrator PIN']"));
            Assert.Empty(cut.FindAll("[data-access-section='users']"));
        });
    }

    [Fact]
    public void SettingsPage_RendersUserOverviewWithoutAdminOverviewContent()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Change profile photo", cut.Markup);
            Assert.Empty(cut.FindAll(".settings-subsection-nav"));
            Assert.Empty(cut.FindAll(".user-overview-continue-card"));
            Assert.Single(cut.FindAll(".user-overview-history-card"));
            Assert.Single(cut.FindAll(".user-overview-taste-card"));
            Assert.Single(cut.FindAll(".user-overview-stat-strip"));
            Assert.DoesNotContain("At a Glance", cut.Markup);
            Assert.DoesNotContain("Your Statistics", cut.Markup);
            Assert.Empty(cut.FindAll(".recent-history"));
            Assert.DoesNotContain("Recently Completed", cut.Markup);
            Assert.DoesNotContain("Libraries Used", cut.Markup);
            Assert.DoesNotContain("Preferences at a Glance", cut.Markup);
            Assert.DoesNotContain("user-overview-name-field", cut.Markup);
            Assert.DoesNotContain("user-overview-editor", cut.Markup);
            Assert.DoesNotContain("Runtime status", cut.Markup);
            Assert.DoesNotContain("Heartbeat", cut.Markup);
            Assert.DoesNotContain("Stage 3 coverage", cut.Markup);
        });
    }

    [Fact]
    public void LegacySettingsReviewPage_RedirectsToRecentlyAddedReviewSection()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));
        Assert.Contains("\"/settings/recently-added?review=expanded\"", source, StringComparison.Ordinal);
        Assert.Contains("Section.Equals(\"review\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemOverview_CompositionSegmentsReflectEachCategoryShareOfTheWhole()
    {
        var apiClient = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ =>
                Task.FromResult<LibraryOverviewDto?>(new LibraryOverviewDto
                {
                    TotalItems = 4,
                    MediaTypeCounts = new Dictionary<string, int>
                    {
                        ["Books"] = 2,
                        ["Movies"] = 1,
                        ["TV"] = 1,
                    },
                })));
        Services.AddSingleton<IEngineApiClient>(apiClient);

        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<OverviewTab>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(6, cut.FindAll(".admin-library-media").Count);
            var segments = cut.FindAll(".admin-composition-bar__segment");
            Assert.Equal(3, segments.Count);
            Assert.Contains("width:50%", segments[0].GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("width:25%", segments[1].GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("width:25%", segments[2].GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("Books 2, 50%", cut.Find(".admin-composition-bar").GetAttribute("aria-label"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void PrivacyHistoryTab_DisablesUnavailableControlsAndExplainsThatNoDataChanges()
    {
        var cut = Render<PrivacyHistoryTab>();

        Assert.Contains("not available yet", cut.Markup);
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll("input"));

        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "PrivacyHistoryTab.razor"));
        Assert.DoesNotContain("OnClick=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackSettingsPage_RendersOnlyTheSelectedUserPlaybackPreferences()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "playback");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".mud-tabs"));
            Assert.Empty(cut.FindAll(".settings-subsection-nav"));
            Assert.True(cut.FindAll(".playback-settings-card").Count >= 5);
            Assert.NotEmpty(cut.FindAll(".settings-section-header"));
            Assert.NotEmpty(cut.FindAll(".playback-preference-row"));
            Assert.Contains("Resume &amp; Progress", cut.Markup);
            Assert.Contains("Watching", cut.Markup);
            Assert.Contains("Listening", cut.Markup);
            Assert.Contains("Reading", cut.Markup);
            Assert.Contains("Subtitles &amp; Languages", cut.Markup);
            Assert.DoesNotContain("Theme", cut.Markup);
            Assert.DoesNotContain("Sleep timer", cut.Markup);
            Assert.DoesNotContain("Preferred quality", cut.Markup);
            Assert.DoesNotContain("Saved locally", cut.Markup);
            Assert.DoesNotContain("Save changes", cut.Markup);
            Assert.DoesNotContain("Unsaved changes", cut.Markup);
            Assert.DoesNotContain("Web delivery", cut.Markup);
            Assert.DoesNotContain("TV delivery", cut.Markup);
            Assert.DoesNotContain("Mobile download profile", cut.Markup);
            Assert.DoesNotContain("Direct play", cut.Markup);
            Assert.DoesNotContain("HLS", cut.Markup);
            Assert.DoesNotContain("Transcoding", cut.Markup);
        });
    }

    [Fact]
    public void PlaybackTab_RendersOneUsefulPageWithoutSubsectionRouting()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<PlaybackTab>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("overview", cut.Find(".settings-playback-page").GetAttribute("data-playback-section"));
            Assert.Contains("Default playback speed", cut.Markup);
            Assert.Contains("Audiobook default speed", cut.Markup);
            Assert.Contains("Adjust text while reading", cut.Markup);
            Assert.Contains("Choose tracks in the player", cut.Markup);
        });
    }

    [Fact]
    public void UserOverviewTab_RendersIdentityActivityHistoryAndTasteTogether()
    {
        var stub = (EngineApiClientStub)(object)Services.GetRequiredService<IEngineApiClient>();
        stub.SetHandler(nameof(IEngineApiClient.GetProfileReactionsAsync), _ =>
            Task.FromResult<IReadOnlyList<MediaEngine.Contracts.ProfileState.ProfileReactionDto>>(
            [
                new(MediaEngine.Domain.Enums.ProfileEntityKind.Book, Guid.NewGuid(), MediaEngine.Domain.Enums.ProfileReactionKind.Like, DateTimeOffset.UtcNow),
                new(MediaEngine.Domain.Enums.ProfileEntityKind.Comic, Guid.NewGuid(), MediaEngine.Domain.Enums.ProfileReactionKind.Love, DateTimeOffset.UtcNow),
                new(MediaEngine.Domain.Enums.ProfileEntityKind.Movie, Guid.NewGuid(), MediaEngine.Domain.Enums.ProfileReactionKind.Dislike, DateTimeOffset.UtcNow),
                new(MediaEngine.Domain.Enums.ProfileEntityKind.Song, Guid.NewGuid(), MediaEngine.Domain.Enums.ProfileReactionKind.Love, DateTimeOffset.UtcNow),
            ]));
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<UserOverviewTab>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("overview", cut.Find(".user-overview-grid").GetAttribute("data-profile-section"));
            Assert.Contains("Activity summary", cut.Markup);
            Assert.Contains("Continue where you left off", cut.Markup);
            Assert.Contains("Taste", cut.Markup);
            Assert.Equal("2", cut.Find("a[href='/for-me?view=favorites&area=read'] strong").TextContent);
            Assert.Equal("0", cut.Find("a[href='/for-me?view=favorites&area=watch'] strong").TextContent);
            Assert.Equal("1", cut.Find("a[href='/for-me?view=favorites&area=listen'] strong").TextContent);
            Assert.Empty(cut.FindAll("[aria-label='Edit display name']"));
            Assert.Contains("Edit profile", cut.Markup);
            Assert.DoesNotContain("Manage settings", cut.Markup);
            Assert.DoesNotContain("Appearance", cut.Markup);
            Assert.DoesNotContain("Accent color", cut.Markup);
        });
    }

    [Fact]
    public void PlaybackTab_UsesSharedControlsAndRealApiSavePath()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "PlaybackTab.razor"));

        Assert.DoesNotContain("<AppTabs", source);
        Assert.DoesNotContain("settings-playback-tabs", source);
        Assert.Contains("data-playback-section=\"overview\"", source);
        Assert.Contains("Size=\"AppControlSize.Compact\"", source);
        Assert.DoesNotContain("settings-field--compact", source);
        Assert.Contains("playback-preference-row", source);
        Assert.Contains("UpdateAndSaveAsync", source);
        Assert.Contains("Default playback speed", source);
        Assert.Contains("Audiobook default speed", source);
        Assert.DoesNotContain("DetectShortIntroChapters", source);
        Assert.DoesNotContain("ShortIntroMaxSeconds", source);
        Assert.DoesNotContain("ShortIntroLabel", source);
        Assert.DoesNotContain("NormalizeIntroLabel", source);
        Assert.Contains("<AppSelect", source);
        Assert.Contains("<AppIntSelect", source);
        Assert.Contains("Orchestrator.SavePlaybackSettingsAsync", source);
        Assert.DoesNotContain("Reading.Theme", source);
        Assert.DoesNotContain("ThemeOptions", source);
        Assert.DoesNotContain("Saved locally", source);
        Assert.DoesNotContain("Save changes", source);
        Assert.DoesNotContain("Unsaved changes", source);
        Assert.DoesNotContain("Task.Delay", source);
        Assert.DoesNotContain("MudToggleGroup", source);
        Assert.DoesNotContain("Web delivery", source);
        Assert.DoesNotContain("TV delivery", source);
        Assert.DoesNotContain("Mobile download profile", source);
    }

    [Fact]
    public void DeliverySettings_HidesUnpersistedControls()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "PlaybackDeliverySettingsTab.razor"));

        Assert.Contains("Variant Storage", source);
        Assert.Contains("Diagnostics", source);
        Assert.DoesNotContain("Allow direct play", source);
        Assert.DoesNotContain("Subtitle extraction", source);
        Assert.DoesNotContain("TODO: Persist direct play settings", source);
        Assert.DoesNotContain("TODO: Persist subtitle and audio delivery settings", source);
    }

    [Fact]
    public void AiFeatures_UsesEngineBackedConfigSavePath()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "AiFeaturesTab.razor"));

        Assert.Contains("Save feature flags", source);
        Assert.Contains("SaveAiConfigAsync", source);
        Assert.Contains("GetAiConfigAsync", source);
        Assert.Contains("GetAiModelStatusesAsync", source);
        Assert.Contains("Planned", source);
        Assert.DoesNotContain("Toggle persistence is not wired to the Engine yet", source);
    }

    [Fact]
    public void ModelsTab_UsesEngineBackedLifecycleActions()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "ModelsTab.razor"));

        Assert.Contains("GetAiModelStatusesAsync", source);
        Assert.Contains("StartAiModelDownloadAsync", source);
        Assert.Contains("CancelAiModelDownloadAsync", source);
        Assert.Contains("LoadAiModelAsync", source);
        Assert.Contains("UnloadAiModelAsync", source);
        Assert.Contains("result.Problem?.ToUserMessage()", source);
        Assert.Contains("RunBenchmarkAsync", source);
        Assert.Contains("Essential · Qwen3 0.6B Q8", source);
        Assert.Contains("Advanced · Qwen3 4B Q4", source);
        Assert.DoesNotContain("SimulateDownload", source);
        Assert.DoesNotContain("Task.Delay", source);
        Assert.DoesNotContain("ChangeStatus", source);
    }

    [Fact]
    public void LocalAiLimits_AreMarkedNotConnectedAndModelsAreNotDuplicated()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "LocalAiSettingsTab.razor"));

        Assert.Contains("GetAiConfigAsync", source);
        Assert.Contains("SaveAiConfigAsync", source);
        Assert.Contains("_status?.IsReady != true", source);
        Assert.DoesNotContain("Local AI is partially available", source);
        Assert.Single(Regex.Matches(source, "<ModelsTab />"));
        Assert.DoesNotContain("TODO: Persist AI limits", source);
    }

    [Fact]
    public void MetadataSettings_DoesNotFallBackToHardcodedDefaultsAsConfiguredState()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "MetadataSettingsPage.razor"))
                     + File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Services", "Integration", "MetadataSettingsStateService.cs"));

        Assert.Contains("No sample provider state is being shown", source);
        Assert.Contains("_loadError", source);
        Assert.Contains("GetPipelinesAsync", source);
        Assert.DoesNotContain("Load sample chain", source);
        Assert.DoesNotContain("using dashboard defaults", source);
        Assert.DoesNotContain("DefaultAssignments", source);
    }

    [Fact]
    public void SettingsNavigation_LabelsPartialAndPlannedAdminSections()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));

        var nav = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Models", "ViewDTOs", "SettingsNav.cs"));

        Assert.Contains("SettingsSection.LocalAi", nav);
        Assert.Contains("SettingsSection.Delivery", nav);
        Assert.Contains("SettingsSection.Plugins", nav);
        Assert.Contains("SettingsSection.Access", nav);
        Assert.Contains("SettingsStatusKind.Partial", nav);
    }

    [Fact]
    public void Source_ExposesCurrentDashboardRoutes()
    {
        var routeSources = Directory
            .EnumerateFiles(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages"), "*.razor", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains(routeSources, source => source.Contains("@page \"/\"", StringComparison.Ordinal));
        Assert.Contains(routeSources, source => source.Contains("@page \"/read", StringComparison.Ordinal));
        Assert.Contains(routeSources, source => source.Contains("@page \"/watch", StringComparison.Ordinal));
        Assert.Contains(routeSources, source => source.Contains("@page \"/listen", StringComparison.Ordinal));
        Assert.Contains(routeSources, source => source.Contains("@page \"/collections", StringComparison.Ordinal));
        Assert.Contains(routeSources, source => source.Contains("@page \"/search", StringComparison.Ordinal));
        Assert.Contains(routeSources, source => source.Contains("@page \"/settings", StringComparison.Ordinal));
    }

    [Fact]
    public void CollectionsPage_RendersSharedSectionShellAndCatalogOverview()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<CollectionsPage>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".mud-table"));
            Assert.Single(cut.FindAll(".media-section-shell"));
            Assert.Single(cut.FindAll(".media-section-shell__rail"));
            Assert.Single(cut.FindAll(".library-section-header"));
            Assert.Single(cut.FindAll(".surface-navigation-bar"));
            Assert.NotEmpty(cut.FindAll(".collections-overview"));
            Assert.Empty(cut.FindAll(".cinematic-hero-carousel"));
            Assert.NotEmpty(cut.FindAll(".surface-tab-bar"));
            Assert.NotEmpty(cut.FindAll(".media-group-tile"));
            Assert.Empty(cut.FindAll(".media-tile"));
            Assert.Empty(cut.FindAll(".collections-hub__tabs"));
            Assert.Empty(cut.FindAll(".collections-hub-tab"));
            Assert.Empty(cut.FindAll(".collection-hub-section"));
            Assert.Empty(cut.FindAll(".collection-hub-card"));
            Assert.Empty(cut.FindAll(".collection-hub-row"));
            Assert.Empty(cut.FindAll(".collection-inspector"));
            Assert.Contains("Dune Universe", cut.Markup);
            Assert.Contains("Middle-earth", cut.Markup);
            Assert.Contains("Weekend Picks", cut.Markup);
            Assert.Contains(">Automatic<", cut.Markup);
            Assert.Contains(">Curated<", cut.Markup);
            Assert.Contains(">Shelves<", cut.Markup);
            Assert.Contains(">People<", cut.Markup);
            Assert.Contains("Broader worlds Tuvima Library found by connecting trusted relationships", cut.Markup);
            Assert.DoesNotContain(">Playlists<", cut.Markup);
            Assert.DoesNotContain("CROSS-MEDIA COLLECTIONS", cut.Markup);
            Assert.DoesNotContain("WATCH COLLECTIONS", cut.Markup);
            Assert.DoesNotContain("LISTEN COLLECTIONS", cut.Markup);
            Assert.DoesNotContain("READ COLLECTIONS", cut.Markup);
            Assert.DoesNotContain("collections-hub__browse-types", cut.Markup);
        });
    }

    [Fact]
    public void CollectionsShelves_RendersStructuralSeriesAndWorkingLaneRoutes()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<CollectionsPage>(3);
            builder.AddAttribute(4, nameof(CollectionsPage.Section), "shelves");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Dune", cut.Markup);
            Assert.Contains("The Dark Knight", cut.Markup);
            Assert.DoesNotContain("Octavia Butler", cut.Markup);
            var viewAllRoutes = cut.FindAll(".media-tile-shelf-link")
                .Select(link => link.GetAttribute("href"))
                .ToList();
            Assert.Contains("/collections/shelves?lane=Read", viewAllRoutes);
            Assert.Contains("/collections/shelves?lane=Watch", viewAllRoutes);
        });
    }

    [Fact]
    public void CollectionsPeople_RendersPagedCanonicalContributorList()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<MudDialogProvider>(1);
            builder.CloseComponent();
            builder.OpenComponent<MudSnackbarProvider>(2);
            builder.CloseComponent();
            builder.OpenComponent<CollectionsPage>(3);
            builder.AddAttribute(4, nameof(CollectionsPage.Section), "people");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".people-catalog-list"));
            Assert.Single(cut.FindAll(".people-catalog-row"));
            Assert.Contains("Octavia Butler", cut.Markup);
            Assert.Contains("3 Read", cut.Markup);
            Assert.Contains("3 owned", cut.Markup);
            Assert.Contains("Search people or roles", cut.Markup);
            Assert.Contains(">Role<", cut.Markup);
            Assert.Contains(">Lane<", cut.Markup);
            Assert.Empty(cut.FindAll(".media-tile"));
            Assert.Empty(cut.FindAll(".media-group-tile"));
        });
    }

    [Fact]
    public void LibraryConfigurableTable_RendersMudTableShellAndMudActions()
    {
        var cut = Render<LibraryConfigurableTable>(parameters => parameters
            .Add(component => component.Columns, LibraryColumnDefinitions.GetColumnsByTab("books"))
            .Add(component => component.Items, [CreateSampleLibraryItem()])
            .Add(component => component.Loading, false)
            .Add(component => component.GroupBy, "type")
            .Add(component => component.SelectedItems, new HashSet<Guid>()));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".mud-simple-table"));
            Assert.Contains("The Hobbit", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".mud-button-root"));
            var groupToggle = cut.Find("button.vmt-group-toggle");
            Assert.Equal("true", groupToggle.GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public void ListenNavigationSection_RendersCurrentRouteAsSemanticLink()
    {
        var cut = Render<ListenNavigationSection>(parameters => parameters
            .Add(component => component.Label, "Library")
            .Add(component => component.Items,
            [
                new ListenNavigationItem("Home", "/listen", Icons.Material.Outlined.Home),
                new ListenNavigationItem("Music", "/listen/music", Icons.Material.Outlined.MusicNote),
            ])
            .Add(component => component.IsRouteActive, route => route == "/listen/music"));

        var links = cut.FindAll("a.listen-rail__item");
        Assert.Collection(
            links,
            link => Assert.Null(link.GetAttribute("aria-current")),
            link => Assert.Equal("page", link.GetAttribute("aria-current")));
    }

    [Fact]
    public void ListenBrowsePage_RendersSharedPersistentRailWithoutRightPlayer()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/music?browse=songs");

        var cut = RenderListenBrowsePageWithProviders("music");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".media-section-shell"));
            Assert.Single(cut.FindAll(".media-section-shell__rail"));
            Assert.Single(cut.FindAll(".media-section-shell__content"));
            Assert.Single(cut.FindAll(".browse-shell"));
            Assert.Empty(cut.FindAll(".listen-now-panel"));
            Assert.Contains(">Albums<", cut.Markup);
            Assert.Contains(">Artists<", cut.Markup);
            Assert.Contains(">Songs<", cut.Markup);
            Assert.Contains(">Playlists<", cut.Markup);
            Assert.Contains("Search songs, artists, or albums...", cut.Markup);
        });
    }

    [Fact]
    public void ListenBrowsePage_RestoresSearchTextFromTheQueryString()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/music?browse=songs&q=Abbey%20Road");

        var cut = RenderListenBrowsePageWithProviders("music");

        cut.WaitForAssertion(() =>
        {
            var search = cut.Find("input[placeholder='Search songs, artists, or albums...']");
            Assert.Equal("Abbey Road", search.GetAttribute("value"));
        });
    }

    [Fact]
    public void ListenPage_MainHubRendersInsidePermanentListenShell()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen");

        var cut = RenderListenBrowsePageWithProviders();

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".media-hub--listen"));
            Assert.Single(cut.FindAll("[data-media-hub='true']"));
            Assert.Empty(cut.FindAll(".media-lane-header__identity"));
            Assert.Contains(">Discover<", cut.Markup);
            Assert.Contains(">Music<", cut.Markup);
            Assert.Contains(">Audiobooks<", cut.Markup);
            Assert.Single(cut.FindAll(".media-hub__shelves"));
            Assert.Single(cut.FindAll(".media-section-shell__rail"));
            Assert.Empty(cut.FindAll(".listen-now-panel"));
            Assert.Single(cut.FindAll(".media-section-shell"));
        });
    }

    [Fact]
    public void ListenBrowsePage_RendersQuickAccessMusicAndPlaylistShortcutsInOrder()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/music?browse=songs");

        var cut = RenderListenBrowsePageWithProviders("music");

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            var library = markup.IndexOf(">Library<", StringComparison.Ordinal);
            var listenNext = markup.IndexOf(">Listen next<", StringComparison.Ordinal);
            var music = markup.IndexOf(">Music<", listenNext + 1, StringComparison.Ordinal);
            var playlists = markup.IndexOf(">Playlists<", StringComparison.Ordinal);

            Assert.True(library >= 0, "Library section should render.");
            Assert.True(listenNext > library, "Quick access should render below Library.");
            Assert.True(music > listenNext, "Music shortcuts should render below quick access.");
            Assert.True(playlists > music, "Playlists should render below Music.");
            Assert.Contains(">Audiobooks<", markup);
            Assert.Contains(">Albums<", markup);
            Assert.Contains(">Artists<", markup);
            Assert.Contains(">Songs<", markup);
            Assert.Contains(">All Playlists<", markup);
            Assert.Contains(">Recently Played<", markup);
            Assert.Contains(">Recently Added<", markup);
            Assert.Contains("Drag to reorder", markup);
            Assert.Empty(cut.FindAll(".listen-now-panel"));
        });
    }

    [Fact]
    public void ListenBrowsePage_AudiobooksUsesSharedBrowseShellWithAudiobookModes()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/audiobooks");

        var cut = RenderListenBrowsePageWithProviders("audiobooks");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".media-section-shell"));
            Assert.Single(cut.FindAll(".browse-shell"));
            Assert.Contains("Search audiobooks", cut.Markup);
            Assert.Contains(">Audiobooks<", cut.Markup);
            Assert.Contains("Series", cut.Markup);
            Assert.Contains("Authors", cut.Markup);
            Assert.Contains("Narrators", cut.Markup);
            Assert.Contains("Timeline", cut.Markup);
            Assert.Contains("In progress", cut.Markup);
            Assert.Contains("Not started", cut.Markup);
            Assert.Contains("Finished", cut.Markup);
            Assert.Contains("Cards", cut.Markup);
            Assert.Contains("List", cut.Markup);
            Assert.Contains("Continue Listening", cut.Markup);
            Assert.Empty(cut.FindAll(".audiobooks-dashboard"));
            Assert.Empty(cut.FindAll(".listen-now-panel"));
        });
    }

    [Fact]
    public void ListenBrowsePage_AudiobookSeriesModeUpdatesUrlBackedState()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/audiobooks");

        var cut = RenderListenBrowsePageWithProviders("audiobooks");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".browse-shell"));
            Assert.Contains("Series", cut.Markup);
        });

        var seriesButton = cut.FindAll(".app-browse-mode__option")
            .Single(button => button.TextContent.Contains("Series", StringComparison.OrdinalIgnoreCase));
        seriesButton.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.EndsWith("/listen/audiobooks?browse=series", navigationManager.Uri, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(">Series<", cut.Markup);
        });
    }

    [Fact]
    public void ListenBrowsePage_LeftRailAudiobooksUsesRealRoute()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/music");

        var cut = RenderListenBrowsePageWithProviders("music");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".media-section-shell"));
            Assert.Contains("Audiobooks", cut.Markup);
        });

        var audiobooksLink = cut.FindAll("a.media-section-shell__rail-item[href='/listen/audiobooks']")
            .Single(link => link.TextContent.Contains("Audiobooks", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("/listen/audiobooks", audiobooksLink.GetAttribute("href"));
        Assert.Empty(cut.FindAll("button.media-section-shell__rail-item"));
    }

    [Fact]
    public void ListenPage_RouteParametersReloadListenStateWithRaceGuard()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor.cs"));
        var markup = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor"));
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor.css"));

        Assert.Contains("protected override async Task OnParametersSetAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LocationChanged", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OnListenLocationChanged", source, StringComparison.Ordinal);
        Assert.Contains("private int _listenLoadVersion;", source, StringComparison.Ordinal);
        Assert.Contains("if (!IsCurrentLoad(loadVersion))", source, StringComparison.Ordinal);
        Assert.Contains("private void NavigateRailLink(string route)", source, StringComparison.Ordinal);
        Assert.Contains("NavigateTo(route, forceLoad: true)", source, StringComparison.Ordinal);
        Assert.Contains("target=\"_top\"", markup, StringComparison.Ordinal);
        Assert.Contains("NavigateRailLink(item.Route)", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Icons.Material.Outlined.DownloadDone", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Icons.Material.Outlined.RadioButtonUnchecked", markup, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-page--audiobooks .listen-rail", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-page--audiobooks ::deep .listen-now-panel", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-page--audiobooks {\r\n    grid-template-columns", css, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-page--audiobooks {\n    grid-template-columns", css, StringComparison.Ordinal);
    }

    private sealed class AlwaysUnlockedAdministratorSurfaceAccessService : IAdministratorSurfaceAccessService
    {
        public Task<bool> EnsureUnlockedAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class SettingsIdentityClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }


    private sealed class DelayedLockHandler(MediaEngine.Contracts.Authentication.DashboardAuthorityResponse authority, DashboardSessionAccessor session) : HttpMessageHandler
    {
        public TaskCompletionSource DeleteObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ActionsWereClearedBeforeDelete { get; private set; }
        public bool ValidationRequested { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/access/admin-unlock")
            {
                DeleteObserved.SetResult();
                ActionsWereClearedBeforeDelete = !session.HasAction("access.manage");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.RequestUri!.AbsolutePath == "/auth/session/validate")
            {
                ValidationRequested = true;
                var response = new MediaEngine.Contracts.Authentication.SessionValidationResponse
                {
                    SessionId = Guid.NewGuid(),
                    AccountId = authority.AccountId,
                    ActiveProfileId = authority.ActiveProfileId,
                    DisplayName = "Profile",
                    Authority = authority,
                    AuthenticationMethod = "test",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }
    }

    [Fact]
    public void PlaylistCreation_StaysInPlaceAndOverflowMenusOwnIndependentState()
    {
        var legacySource = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor.cs"));
        var legacyMarkup = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor"));
        var browseSource = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenBrowsePage.razor"));
        var menuSource = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Shared", "AppOverflowMenu.razor"));
        var globalCss = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "app.css"));

        Assert.DoesNotContain("Nav.NavigateTo($\"/listen/music/playlists/{createdId:D}\")", legacySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Nav.NavigateTo($\"/listen/music/playlists/{playlistId:D}\")", browseSource, StringComparison.Ordinal);
        Assert.True(legacyMarkup.Split("<AppOverflowMenu", StringSplitOptions.None).Length - 1 >= 3);
        Assert.DoesNotContain("_playlistActionsMenuOpen", legacySource, StringComparison.Ordinal);
        Assert.DoesNotContain("_playlistActionsCollectionId", legacySource, StringComparison.Ordinal);
        Assert.Contains("@onclick:stopPropagation=\"true\"", menuSource, StringComparison.Ordinal);
        Assert.Contains("AppControlSize.Compact", menuSource, StringComparison.Ordinal);
        Assert.Contains("min-width: 8.75rem;", globalCss, StringComparison.Ordinal);
        Assert.Contains("min-height: 2rem !important;", globalCss, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenPlayer_RemainsInPersistentBottomHostOnListenRoutes()
    {
        var listenPageSource = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "ListenPage.razor"));
        var hostSource = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Listen", "ListenNowPlayingBar.razor"));
        var hostCss = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Listen", "ListenNowPlayingBar.razor.css"));

        Assert.DoesNotContain("<ListenNowPlayingPanel", listenPageSource, StringComparison.Ordinal);
        Assert.Contains("TransportCommandRequested", hostSource, StringComparison.Ordinal);
        Assert.Contains("ReportHeartbeatAsync", hostSource, StringComparison.Ordinal);
        Assert.Contains("listen-player-shell--listen-route", hostSource, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-player-shell--listen-route {", hostCss, StringComparison.Ordinal);
        Assert.Contains("position: fixed", hostCss, StringComparison.Ordinal);
        Assert.Contains("inset-inline: max(16px, var(--tl-safe-area-left))", hostCss, StringComparison.Ordinal);
        Assert.Contains("bottom: max(16px, var(--tl-safe-area-bottom))", hostCss, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--playback-player-radius)", hostCss, StringComparison.Ordinal);
    }

    [Fact]
    public void CinematicHero_RendersScopedRootSoListenHomeSidebarStaysVisible()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Cinematic", "CinematicHeroCarousel.razor"));

        Assert.Contains("<section class=\"@CarouselClass\"", source, StringComparison.Ordinal);
        Assert.Contains("<CinematicHeroSurface", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenBrowsePage_MusicEntryUsesSharedSongFirstBrowse()
    {
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo("/listen/music");

        var cut = RenderListenBrowsePageWithProviders("music");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Search songs, artists, or albums", cut.Markup);
            Assert.Single(cut.FindAll(".browse-shell__filter-surface"));
            Assert.Single(cut.FindAll(".browse-shell__search"));
            Assert.Contains(">Albums<", cut.Markup);
            Assert.Contains(">Artists<", cut.Markup);
            Assert.Contains(">Songs<", cut.Markup);
            Assert.Contains(">Playlists<", cut.Markup);
            Assert.DoesNotContain("Tile size", cut.Markup);
            Assert.Empty(cut.FindAll(".listen-now-panel"));
        });

        var markup = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Browse", "MediaBrowseShell.razor"));
        Assert.Contains("<MediaTileGrid Items=\"@_cards\"", markup, StringComparison.Ordinal);
        Assert.Contains("ShowCompactCaptions=\"true\"", markup, StringComparison.Ordinal);
        Assert.Contains("TileSizePx=\"@TileSizePx\"", markup, StringComparison.Ordinal);
        Assert.Contains("\"Music\" => 136", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionEditor_UsesSharedLargeEditorWorkspaceAndKeepsPlaylistArtworkPicker()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Collections", "CollectionEditorShell.razor"));
        var ruleBuilder = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Collections", "CollectionRuleBuilder.razor"));
        var sharedRuleBuilder = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Rules", "SharedRuleBuilder.razor"));
        var valuePicker = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Collections", "CollectionRuleValuePicker.razor"));

        Assert.Contains("<MediaEditorSurface", source);
        Assert.Contains("sme-shell collection-editor-workspace", source);
        Assert.Contains("sme-section-nav collection-editor-rail", source);
        Assert.Contains("collection-editor-header-actions", source);
        Assert.DoesNotContain("<AppDialogShell", source);
        Assert.DoesNotContain("collection-editor-dialog", source);
        Assert.Contains("IsPlaylistDialog", source);
        Assert.Contains("app-artwork-picker", source);
        Assert.Contains("app-artwork-picker__edit", source);
        Assert.Contains("collection-editor-field", source);
        Assert.Equal(1, source.Split("OnClick=\"SaveCollection\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("<CollectionRuleBuilder", source);
        Assert.Contains("<SharedRuleBuilder", ruleBuilder);
        Assert.Contains("<CollectionRuleValuePicker", sharedRuleBuilder);
        Assert.Contains("Select all shown", valuePicker);
        Assert.Contains("GetCollectionFieldValuesAsync", valuePicker);
        Assert.Contains("GetCollectionEntityFieldValuesAsync", valuePicker);
        Assert.Contains("collection-editor-item-list--members", source);
        Assert.DoesNotContain("Choose file", source);
        Assert.DoesNotContain("listen-create-modal", source);
    }

    [Fact]
    public void CollectionEditor_SmartPlaylistMode_RendersSimplifiedMusicRuleControls()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Collections", "CollectionEditorShell.razor"));
        var ruleBuilder = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Collections", "CollectionRuleBuilder.razor"));
        var sharedRuleBuilder = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Rules", "SharedRuleBuilder.razor"));

        Assert.Contains("InitialMembershipMode", source);
        Assert.Contains("Create a playlist, then choose manual or smart membership.", source);
        Assert.DoesNotContain("app-icon-picker__item", source);
        Assert.DoesNotContain("Show in My Profile and Search", source);
        Assert.DoesNotContain("<MudSelectItem T=\"string\" Value=\"@(\"media_type\")\">Media Type</MudSelectItem>", source);
        Assert.Contains("RenderRulesSection", source);
        Assert.Contains("CollectionRuleCatalog.Instance", ruleBuilder);
        Assert.Contains("Add group", sharedRuleBuilder);
        Assert.Contains("collection-rule-builder__group", sharedRuleBuilder);
        Assert.Contains("collection-rule-builder__row", sharedRuleBuilder);
        Assert.Contains("app-dialog-select", sharedRuleBuilder);
    }

    [Fact]
    public void SharedDialogCss_DefinesCompactFieldsDarkSelectsAndHiddenArtworkInput()
    {
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "app.css"));

        Assert.Contains(".app-dialog-field", css);
        Assert.Contains("var(--tl-control-height-sm)", css);
        Assert.Contains(".tl-form", css);
        Assert.Contains(".tl-field-label", css);
        Assert.Contains(".tl-filter-bar", css);
        Assert.Contains(".tl-action-bar", css);
        Assert.Contains(".mud-popover .mud-paper", css);
        Assert.Contains(".app-artwork-picker__input", css);
        Assert.Contains("opacity: 0", css);
    }

    [Fact]
    public void AppCss_KeepsMudDropdownRowsBorderless()
    {
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "app.css"));

        Assert.Contains(".mud-menu .mud-list-item", css);
        Assert.Contains("border: 0 !important;", css);
        Assert.Contains("box-shadow: none !important;", css);

        var rowRuleBodies = Regex.Matches(css, @"(?s)\.mud-(?:list-item|menu-item)[^{]*\{(?<body>.*?)\}")
            .Select(match => match.Groups["body"].Value.ToLowerInvariant())
            .ToArray();

        Assert.All(rowRuleBodies, body =>
        {
            Assert.DoesNotContain("border: 1px", body);
            Assert.DoesNotContain("box-shadow: var(", body);
            Assert.DoesNotContain("box-shadow: 0 ", body);
            Assert.DoesNotContain("box-shadow: inset", body);
        });
    }

    [Fact]
    public void DesignTokens_DefineSharedControlAndFormSizing()
    {
        var tokens = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));

        Assert.Contains("--tl-control-height-sm: 36px;", tokens);
        Assert.Contains("--tl-control-height-md: 42px;", tokens);
        Assert.Contains("--tl-control-height-lg: 48px;", tokens);
        Assert.Contains("--tl-control-radius: 10px;", tokens);
        Assert.Contains("--tl-form-row-gap: 16px;", tokens);
        Assert.Contains("--tl-form-section-gap: 24px;", tokens);
        Assert.Contains("--tl-card-padding: 20px;", tokens);
        Assert.Contains("--tl-text-faint:", tokens);
        Assert.Contains("--tl-font-mono:", tokens);
    }

    [Fact]
    public void AppCss_UsesSingleAliasRootAndSharedFoundation()
    {
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "app.css"));

        Assert.Single(Regex.Matches(css, @"(?m)^:root\s*\{"));
        Assert.Contains(".tl-setting-row", css);
        Assert.Contains(".tl-card--flush", css);
        Assert.Contains(".tl-empty-state", css);
        Assert.Contains(".search-result-row", css);
        Assert.Contains("border-right: 1px solid var(--tl-divider)", css);
        Assert.DoesNotContain("Legacy settings shared UI components", css);
    }

    [Fact]
    public void WebUiSource_DoesNotContainLegacySettingsClassesOrMojibake()
    {
        var root = GetRepoFile("src", "MediaEngine.Web");
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                           || file.EndsWith(".resx", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);

            Assert.DoesNotMatch(@"class=""[^""]*\bst-page\b", source);
            Assert.DoesNotMatch(@"class=""[^""]*\bst-card\b", source);
            Assert.DoesNotMatch(@"class=""[^""]*\bst-toggle-row\b", source);
            Assert.DoesNotContain("Ã‚", source);
            Assert.DoesNotContain("Ã¢", source);
            Assert.DoesNotContain("Ã", source);
            Assert.DoesNotContain("Â", source);
            Assert.DoesNotContain("â€¦", source);
            Assert.DoesNotContain("â€”", source);
            Assert.DoesNotContain("â€“", source);
            Assert.DoesNotContain("â†", source);
            Assert.DoesNotContain("â€", source);
            Assert.DoesNotContain("â”", source);
            Assert.DoesNotContain("ï¿½", source);
            Assert.DoesNotContain("�", source);
        }
    }

    [Fact]
    public void UiConsistencyAudit_DocumentsMigrationPlan()
    {
        var audit = File.ReadAllText(GetRepoFile("docs", "ui", "ui-consistency-audit.md"));

        Assert.Contains("Inconsistent Form And Input Styling", audit);
        Assert.Contains("Inline Styles", audit);
        Assert.Contains("Duplicate Card And Panel Patterns", audit);
        Assert.Contains("Recommended Priority Order", audit);
        Assert.Contains("tl-filter-bar", audit);
    }

    private static string GetRepoFile(params string[] segments) =>
        Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private IRenderedComponent<IComponent> RenderListenBrowsePageWithProviders(string? tab = null) => Render(builder =>
    {
        builder.OpenComponent<MudPopoverProvider>(0);
        builder.CloseComponent();
        builder.OpenComponent<MudDialogProvider>(1);
        builder.CloseComponent();
        builder.OpenComponent<MudSnackbarProvider>(2);
        builder.CloseComponent();
        builder.OpenComponent<ListenBrowsePage>(3);
        if (!string.IsNullOrWhiteSpace(tab))
        {
            builder.AddAttribute(4, nameof(ListenBrowsePage.Tab), tab);
        }
        builder.CloseComponent();
    });

    [Fact]
    public void SearchPage_RendersMudSearchResultsWithoutRawCards()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetUniversalSearchAsync), _ =>
            {
                var result = new MediaEngine.Contracts.Display.UniversalSearchResultDto(
                    Guid.Parse("30000000-0000-0000-0000-000000000101"),
                    "book",
                    "Book",
                    "Dune",
                    "Frank Herbert",
                    "Frank Herbert",
                    "1965",
                    null,
                    null,
                    "/book/30000000-0000-0000-0000-000000000101",
                    "Read",
                    "Exact title match",
                    1)
                {
                    Facts = ["4.500000", "Dune Chronicles"],
                };
                return Task.FromResult<MediaEngine.Contracts.Display.UniversalSearchResponseDto?>(
                    new("dune", result,
                    [new("books", "Books", [result], 1, "/read/books?q=dune")], 1));
            });
        }));

        var navigationManager = Services.GetRequiredService<NavigationManager>();
        navigationManager.NavigateTo(navigationManager.GetUriWithQueryParameter("q", "dune"));

        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<SearchPage>(1);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Dune", cut.Markup);
            Assert.Contains("Frank Herbert", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".universal-results"));
            Assert.NotEmpty(cut.FindAll(".universal-results__card"));
            Assert.NotEmpty(cut.FindAll(".universal-results__preview"));
            Assert.NotEmpty(cut.FindAll(".universal-results__card-art.is-portrait"));
            Assert.NotEmpty(cut.FindAll(".universal-results__preview-details"));
            Assert.Contains("★ 4.5", cut.Markup);
            Assert.Empty(cut.FindAll(".universal-results__preview-description"));
        });
    }

    private static LibraryItemViewModel CreateSampleLibraryItem() => new()
    {
        EntityId = Guid.Parse("20000000-0000-0000-0000-000000000001"),
        Title = "The Hobbit",
        Author = "J.R.R. Tolkien",
        MediaType = "Books",
        Status = "Verified",
        RetailMatch = "open_library",
        WikidataMatch = "linked",
        WikidataQid = "Q15228",
        CreatedAt = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero),
        FileName = "the-hobbit.epub",
        FilePath = "C:\\Library\\Books\\The Hobbit.epub",
        FileSizeBytes = 1024 * 1024 * 5,
        IsReadyForLibrary = true,
        LibraryVisibility = "visible",
        Confidence = 0.92,
    };

    private sealed class BookmarkDialogTestHost : ComponentBase
    {
        [Parameter, EditorRequired] public AudiobookBookmarkActionContext Context { get; set; } = default!;
        [Parameter, EditorRequired] public IAudiobookBookmarkActions Actions { get; set; } = default!;
        [Parameter, EditorRequired] public IReadOnlySet<Guid> AuthorizedAssetIds { get; set; } = default!;
        [Parameter] public string WorkTitle { get; set; } = string.Empty;

        private bool _open = true;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (!_open) return;
            builder.OpenComponent<AudiobookBookmarkDialog>(0);
            builder.AddAttribute(1, nameof(AudiobookBookmarkDialog.Context), Context);
            builder.AddAttribute(2, nameof(AudiobookBookmarkDialog.Actions), Actions);
            builder.AddAttribute(3, nameof(AudiobookBookmarkDialog.AuthorizedAssetIds), AuthorizedAssetIds);
            builder.AddAttribute(4, nameof(AudiobookBookmarkDialog.WorkTitle), WorkTitle);
            builder.AddAttribute(5, nameof(AudiobookBookmarkDialog.IsMobile), true);
            builder.AddAttribute(6, nameof(AudiobookBookmarkDialog.OnClose), EventCallback.Factory.Create(this, CloseAsync));
            builder.CloseComponent();
        }

        private Task CloseAsync()
        {
            _open = false;
            return InvokeAsync(StateHasChanged);
        }
    }

    private sealed class ReplayInvalidatingBookmarkActions(
        AudiobookBookmarkActionService inner,
        PlaybackSessionController playback,
        AudiobookBookmarkActionContext replayContext,
        ListenQueueItem replayedItem) : IAudiobookBookmarkActions
    {
        public event Action<Guid>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public string? InvalidatedReplayMessage { get; private set; }

        public Task OpenAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default) => inner.OpenAsync(context, ct);
        public Task<AudiobookBookmarkActionSnapshot> GetSnapshotAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default) => inner.GetSnapshotAsync(context, ct);
        public Task<AudiobookBookmarkOperationResult<IReadOnlyList<AudiobookBookmarkDto>>> LoadSavedAsync(
            AudiobookBookmarkActionContext context, IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default) =>
            inner.LoadSavedAsync(context, authorizedAssetIds, ct);
        public Task<AudiobookBookmarkOperationResult<AudiobookBookmarkDto>> SaveAsync(
            AudiobookBookmarkActionContext context, long draftGeneration, IReadOnlySet<Guid> authorizedAssetIds,
            string? note, CancellationToken ct = default) => inner.SaveAsync(context, draftGeneration, authorizedAssetIds, note, ct);
        public Task<AudiobookBookmarkOperationResult<bool>> PreviewCapturedDraftAsync(
            AudiobookBookmarkActionContext context, long draftGeneration, CancellationToken ct = default) =>
            inner.PreviewCapturedDraftAsync(context, draftGeneration, ct);

        public async Task<AudiobookBookmarkReplayResult> ReplayAsync(AudiobookBookmarkActionContext context,
            Guid bookmarkId, IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default)
        {
            var result = await inner.ReplayAsync(context, bookmarkId, authorizedAssetIds, ct);
            if (context == replayContext && result.Outcome == AudiobookBookmarkOperationOutcome.Success)
            {
                playback.RestoreState(new ListenPlaybackSnapshot
                {
                    Queue = [replayedItem],
                    CurrentIndex = 0,
                    Experience = PlayerExperienceModes.Audiobook,
                    IsPlaying = false,
                });
                InvalidatedReplayMessage = inner.GetSnapshot(context.DialogId).Message;
            }
            return result;
        }

        public Task<bool> RequestDeleteAsync(AudiobookBookmarkActionContext context, Guid bookmarkId,
            IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default) =>
            inner.RequestDeleteAsync(context, bookmarkId, authorizedAssetIds, ct);
        public Task<AudiobookBookmarkOperationResult<bool>> ConfirmDeleteAsync(AudiobookBookmarkActionContext context,
            IReadOnlySet<Guid> authorizedAssetIds, CancellationToken ct = default) => inner.ConfirmDeleteAsync(context, authorizedAssetIds, ct);
        public Task CancelDeleteAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default) => inner.CancelDeleteAsync(context, ct);
        public Task CloseAsync(AudiobookBookmarkActionContext context, CancellationToken ct = default) => inner.CloseAsync(context, ct);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MediaEngine.Web.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string EnvironmentName { get; set; } = "Testing";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    }
}
