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

namespace MediaEngine.Web.Tests;

public sealed partial class UiShellRenderTests : AsyncBunitContext
{
    public UiShellRenderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<MediaEngine.Web.Services.Theming.DeviceContextSnapshot>("detectDeviceSnapshot")
            .SetResult(new MediaEngine.Web.Services.Theming.DeviceContextSnapshot("web", "desktop", false));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Engine:BaseUrl"] = "http://localhost:61495",
            })
            .Build();

        var apiClient = EngineApiClientStub.CreateDefault();

        Services.AddLocalization();
        Services.AddLogging();
        Services.AddNativeUiServices();
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
        Services.AddScoped<MediaEngine.Web.Services.MediaTiles.BrowseLibrariesState>();
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
        Services.AddSingleton<IAdministratorSurfaceAccessService>(new AlwaysUnlockedAdministratorSurfaceAccessService());
        var profileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profileId, profileId, true, true, 1, 1, true, true, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profileId, profileId, "Test User", null,
                true, true, true, new MediaEngine.Contracts.Authentication.ProfileAdminProtectionResponse(
                    false, "None", null, 1), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], ["access.manage"]);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", profileId, profileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new DelayedLockHandler(authority, session)));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.AccountNoticeState>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.SettingsAttentionState>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.DevelopmentToolsState>();
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
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
        playback.RestoreState(new()
        {
            ProfileId = profile,
            Queue = [new() { WorkId = book, AudiobookWorkId = book,
            AssetId = asset, MediaType = "Audiobooks", Title = "Book", StreamUrl = "/stream/book", Duration = "10:00" }],
            CurrentIndex = 0,
            CurrentTimeSeconds = 42,
            DurationSeconds = 600,
            PlaybackRate = 1.25,
            PlaybackRequestVersion = 9,
            IsPopupOpen = true,
            SleepTimerState = new()
            {
                ProfileId = profile,
                WorkId = book,
                BoundAssetId = asset,
                PlaybackRequestVersion = 9,
                Mode = AudiobookSleepTimerModes.Timer,
                ChosenMinutes = 30,
                DeadlineUtc = deadline,
                TimerSessionId = Guid.NewGuid(),
                TimerGeneration = 12
            }
        });
        var prepared = playback.CreateSnapshot();
        Assert.Equal(deadline, prepared.SleepTimerState.DeadlineUtc);
        var layout = Render<MainLayout>(parameters => parameters.Add(component => component.Body, builder => builder.AddContent(0, "Current page")));
        await layout.InvokeAsync(() => { playback.RestoreState(prepared); playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying); playback.UpdateTransportState(currentTimeSeconds: 42, isPlaying: true); });
        var before = playback.CreateSnapshot();
        var lease = playback.AudiobookBookSessionLeaseId;
        var transportCommands = 0;
        playback.TransportCommandRequested += _ => { transportCommands++; return Task.CompletedTask; };
        var owner = Services.GetRequiredService<ListenPlaybackCommandOwner>();
        var command = new ListenPlaybackCommandDto
        {
            Action = ListenPlaybackPresentationActions.NavigateIdentity,
            CommandId = Guid.NewGuid(),
            SenderId = popupSender ? Guid.NewGuid() : owner.RecipientId,
            RecipientId = owner.RecipientId,
            ProfileId = profile,
            WorkId = book,
            ExpectedAssetId = asset,
            ExpectedPlaybackRequestVersion = before.PlaybackRequestVersion,
            IdentityKind = "audiobook",
            IdentityId = book
        };
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

    [Fact]
    public void MainLayout_RendersNativeShellAndBody()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section id=\"test-body\">Body content</section>")));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".tl-appbar"));
            Assert.Equal(5, cut.FindAll(".layout-shell__nav-link").Count);
            Assert.Contains(cut.FindAll(".layout-shell__nav-link"),
                link => string.Equals(link.GetAttribute("href"), "/view", StringComparison.Ordinal));
            Assert.Equal("Tuvima", cut.Find(".layout-shell__brand-wordmark").TextContent);
            Assert.Empty(cut.FindAll(".layout-shell__mobile-menu"));
            Assert.Contains("Body content", cut.Markup);
            // Desktop uses the top navigation: no phone dock and no hamburger.
            Assert.Empty(cut.FindAll(".layout-shell__intent-dock-link"));
            Assert.Empty(cut.FindAll(".shell-menu-button"));
            Assert.DoesNotContain("Search your library", cut.Markup);
            Assert.Empty(cut.FindAll(".layout-shell__search-shell"));
            Assert.Single(cut.FindAll(".layout-shell__search-action"));
            Assert.Single(cut.FindAll(".top-nav-account-menu__trigger"));
            Assert.Single(cut.FindAll(".layout-shell__my-list"));
            var activity = Assert.Single(cut.FindAll(".system-activity-indicator"));
            Assert.Equal("Idle. Library is up to date", activity.GetAttribute("aria-label"));
            Assert.Contains("is-idle", activity.ClassList);
            Assert.Empty(cut.FindAll(".system-activity-indicator__ring"));
            Assert.Empty(cut.FindAll(".system-activity-indicator__count"));
            Assert.Empty(cut.FindAll(".layout-shell__review-button"));
            Assert.Empty(cut.FindAll(".layout-shell__avatar-trigger"));
        });
    }

    private static ProfileViewModel Profile(string name) =>
        new(Guid.NewGuid(), name, "#7c4dff", "Profile", DateTimeOffset.UtcNow);

    [Fact]
    public void TopNavAccountMenu_WithOneProfileShowsAccountIdentityWithoutProfileList()
    {
        var shy = Profile("Shy");
        var cut = Render<MediaEngine.Web.Components.Navigation.TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, "Shy")
            .Add(menu => menu.AccountLabel, "shy@example.test")
            .Add(menu => menu.ActiveProfileId, shy.Id)
            .Add(menu => menu.Profiles, new[] { shy })
            .Add(menu => menu.CanManageProfiles, true));

        cut.Find(".top-nav-account-menu__trigger").Click();

        Assert.Equal("shy@example.test", cut.Find(".top-nav-account-menu__header strong").TextContent);
        Assert.Empty(cut.FindAll(".profile-switcher__item"));
        Assert.DoesNotContain("Who's watching?", cut.Markup);
        Assert.Contains(cut.FindAll("a[role=menuitem]"), link =>
            link.GetAttribute("href") == "/settings/profiles" && link.TextContent.Contains("Manage profiles"));
    }

    [Fact]
    public void TopNavAccountMenu_WithSeveralProfilesListsThemAndFallsBackToProfileNameHeader()
    {
        var shy = Profile("Shy");
        var kid = Profile("Kid");
        var cut = Render<MediaEngine.Web.Components.Navigation.TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, "Shy")
            .Add(menu => menu.ActiveProfileId, shy.Id)
            .Add(menu => menu.Profiles, new[] { shy, kid }));

        cut.Find(".top-nav-account-menu__trigger").Click();

        Assert.Equal("Shy", cut.Find(".top-nav-account-menu__header strong").TextContent);
        Assert.NotEmpty(cut.FindAll(".profile-switcher__item"));
        Assert.Contains("Who's watching?", cut.Markup);
        Assert.DoesNotContain("Manage profiles", cut.Markup);
        Assert.Empty(cut.FindAll(".top-nav-account-menu__attention-dot"));
    }

    [Fact]
    public void TopNavAccountMenu_ThisComputerOnlyShowsTheSecureCardAndNoAvatarDotOrMenuItem()
    {
        var cut = Render<MediaEngine.Web.Components.Navigation.TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, "Shy")
            .Add(menu => menu.ShowSecureAccount, true));

        Assert.Empty(cut.FindAll(".top-nav-account-menu__attention-dot"));
        cut.Find(".top-nav-account-menu__trigger").Click();

        Assert.Empty(cut.FindAll(".top-nav-account-menu__attention-dot"));
        Assert.DoesNotContain(cut.FindAll("a[role=menuitem]"), link => link.GetAttribute("href") == "/settings/security");
        Assert.Contains(cut.FindAll(".top-nav-account-menu__secure-card a"), link =>
            link.GetAttribute("href") == "/settings/security" && link.TextContent.Contains("Secure account"));
    }

    [Fact]
    public void HeroActionRow_MapsCheckIconForSelectedMyList()
    {
        Assert.Equal(AppMaterialIcons.Outlined.Check, MediaEngine.Web.Components.Details.HeroActionRow.IconFor(
            new MediaEngine.Contracts.Details.DetailAction { Key = "my-list", Icon = "check", IsSelected = true }));
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
        Assert.Contains("RefreshSystemActivityAsync", activityState);
        Assert.Contains("LastStateChangeRequiresSnapshotRefresh", activityState);
        Assert.Contains("IngestionQueueProgress", activityState);
        Assert.Contains("LiveIngestionActivityTtl", activityState);
        Assert.DoesNotContain("BatchProgressReceivedAt", activityState);
        Assert.DoesNotContain("IngestionProgressReceivedAt", activityState);
        Assert.Contains("<TopNavAccountMenu", source);
        Assert.Contains("<SystemActivityIndicator", source);
        Assert.Contains("ShellActivityKind.Ingestion or ShellActivityKind.Enrichment", activityIndicator);
        Assert.Contains("system-activity-indicator__ring is-animated", activityIndicator);
        Assert.Contains("@keyframes system-activity-ring-spin", activityIndicatorCss);
        Assert.Contains("layout-shell__brand-wordmark", source);
        Assert.DoesNotContain("<AppLogo", source);
        Assert.Contains("Nav_Search", source);
        Assert.Contains("OpenSearch", source);
        Assert.DoesNotContain("<MudTextField", source);
        Assert.DoesNotContain("<AppTextField", source);
        Assert.Contains("TopBar_MyList", source);
        Assert.DoesNotContain("NotificationsNone", source);
        Assert.DoesNotContain("ToggleProfileMenu", source);
        Assert.Contains("@if (CanViewReview)", accountSource);
        Assert.Contains("TopBar_NeedsReview", accountSource);
        Assert.Contains("ShowSignOut", accountSource);
        Assert.Contains("href=\"/settings/review\"", accountSource);
        Assert.Contains("href=\"/settings\"", accountSource);
        Assert.DoesNotContain("<MudMenu", accountSource);
        Assert.DoesNotContain("SettingsSelected", accountSource);
        Assert.DoesNotContain("ReviewSelected", accountSource);
        Assert.DoesNotContain("NotificationsNone", accountSource);
        Assert.Contains("top-nav-account-menu__trigger-copy", accountSource);
        Assert.Contains("href=\"@MyProfileHref\"", accountSource);
        Assert.Contains("MyProfileHref = \"/settings/overview\"", accountSource);
        Assert.Contains("ManageProfilesHref = \"/settings/profiles\"", accountSource);
        // Switching people now happens on the full-screen "Who's using Tuvima?" page.
        Assert.Contains("ProfilePickerRoute.Path", accountSource);
        Assert.DoesNotContain("Switch profile?", accountSource);
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
        {
            await actions.OpenAsync(context);
        }

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
    public async Task SettingsPage_RendersSharedSidebarAndTheSystemSummary()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "system");
            builder.CloseComponent();
        });

        await cut.InvokeAsync(() => cut.Find("button[aria-label='Expand Library and Ingestion']").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".media-section-shell"));
            Assert.Single(cut.FindAll(".media-section-shell__rail"));
            Assert.NotEmpty(cut.FindAll(".media-section-shell__tree-row"));
            // Recently Added is no longer a menu row: Library and Ingestion is shaded amber, with no count bubble.
            var libraryAndIngestion = cut.FindAll(".media-section-shell__tree-link")
                .First(item => item.TextContent.Contains("Library and Ingestion", StringComparison.Ordinal));
            var reviewNotification = libraryAndIngestion.QuerySelector(".media-section-shell__rail-meta--notification");
            Assert.Null(reviewNotification);
            // System opens on the resources summary; the old library overview now lives under Library and Ingestion.
            Assert.Single(cut.FindAll("[data-settings-system-summary]"));
            Assert.DoesNotContain("System exceptions", cut.Markup);
            Assert.Empty(cut.FindAll(".admin-library-summary"));
        });
    }

    // The Needs Review count reaches the menu from the shared attention state a moment after the page first draws, and
    // the first Settings render in a test process also pays for loading and compiling it: several seconds (measured at 7 with other work
    // running on the machine), against 0.1 second for the next one. So these waits use the context's ceiling, not a guess of their own.

    [Fact]
    public void SettingsPage_ForAServerAdministrator_ShowsTheNeedsReviewLineAboveTheBreadcrumb_InTheFramesNoticeSlot()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "system");
            builder.AddAttribute(5, nameof(Settings.Subsection), "overview");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            var notices = cut.Find("[data-settings-frame-slot='notices']");
            Assert.Contains("3 items need review", notices.TextContent, StringComparison.Ordinal);
            var markup = cut.Markup;
            Assert.True(markup.IndexOf("data-settings-frame-slot=\"notices\"", StringComparison.Ordinal)
                < markup.IndexOf("aria-label=\"Breadcrumb\"", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Settings_AdminToPersonalTransition_LocksAndClearsActionsBeforeDeleteCompletes()
    {
        var profile = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profile, profile, true, true, 1, 1, true, true, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new MediaEngine.Contracts.Authentication.ProfileAdminProtectionResponse(true, "LockOnLeave", 30, 1), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], ["access.manage", "administrator.lock"]);
        var session = new DashboardSessionAccessor();
        session.Set("session", profile, profile, Guid.NewGuid(), authority);
        var handler = new DelayedLockHandler(authority, session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.AccountNoticeState>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.SettingsAttentionState>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        Services.AddScoped(_ => session);

        var host = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2); builder.CloseComponent();
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

    [Theory]
    [InlineData("/watch")]
    [InlineData("/")]
    [InlineData("/read")]
    public async Task Settings_WhenATopBarLinkOrTheLogoLeavesIt_DoesNotDragThePersonBackWhenSomethingNudgesIt(string destination)
    {
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/settings/overview");

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2); builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "overview");
            builder.CloseComponent();
        });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".media-section-shell")), TimeSpan.FromSeconds(30));

        // The router has moved on, but Settings is still on screen for a moment and gets nudged (a profile or authority change).
        nav.NavigateTo(destination);
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        var field = typeof(UIOrchestratorService).GetField("OnProfileChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        var handlers = (Action?)field!.GetValue(orchestrator);
        Assert.NotNull(handlers);
        await cut.InvokeAsync(() => handlers!.Invoke());
        await Task.Delay(1000);

        Assert.DoesNotContain("settings", nav.ToBaseRelativePath(nav.Uri), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Settings_DirectAdminRoute_InitializesCircuitAuthorityBeforeResolvingRoute()
    {
        var profile = Guid.NewGuid();
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profile, profile, true, true, 1, 1, true, false, null, 1,
            [new MediaEngine.Contracts.Authentication.AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new MediaEngine.Contracts.Authentication.ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], []);
        var session = new DashboardSessionAccessor();
        var handler = new DelayedLockHandler(authority, session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.AccountNoticeState>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.SettingsAttentionState>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
        Services.AddScoped(_ => session);

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0); builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1); builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2); builder.CloseComponent();
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
                new MediaEngine.Contracts.Authentication.ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)],
            ["settings.administration"], []);
        var session = new DashboardSessionAccessor();
        var handler = new DelayedLockHandler(authority, session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.AccountNoticeState>();
        Services.AddScoped<MediaEngine.Web.Services.Navigation.SettingsAttentionState>();
        Services.AddScoped<MediaEngine.Web.Services.Settings.LibraryKindsState>();
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
    public void SettingsPage_OpensThePersonalOverview_WithoutAdminOverviewContent()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            // The bare /settings address opens the person's own Overview (even for an administrator), not the people list or a card of stats.
            Assert.NotEmpty(cut.FindAll("[data-settings-overview]"));
            Assert.Empty(cut.FindAll(".profiles-tab"));
            Assert.Empty(cut.FindAll(".profiles-tab__card"));
            Assert.Empty(cut.FindAll(".profiles-tab__stats"));
            Assert.Empty(cut.FindAll(".settings-subsection-nav"));
            Assert.DoesNotContain("At a Glance", cut.Markup);
            Assert.DoesNotContain("Your Statistics", cut.Markup);
            Assert.Empty(cut.FindAll(".recent-history"));
            Assert.DoesNotContain("Recently Completed", cut.Markup);
            Assert.DoesNotContain("Libraries Used", cut.Markup);
            Assert.DoesNotContain("Preferences at a Glance", cut.Markup);
            Assert.DoesNotContain("Runtime status", cut.Markup);
            Assert.DoesNotContain("Heartbeat", cut.Markup);
            Assert.DoesNotContain("Stage 3 coverage", cut.Markup);
        });
    }

    [Fact]
    public async Task SettingsPage_WhoseRoutedSectionLagsTheAddress_DoesNotRedirectBackToTheOldPage()
    {
        // The router has already moved the address to Profiles, but this page still holds the Overview parameters (an access
        // re-check can run in that gap). Redirecting from the stale parameters used to bounce the page between the two.
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/settings/profiles");

        Render<Settings>(parameters => parameters.Add(page => page.Section, "overview"));
        await Task.Delay(1500);

        Assert.Equal("http://localhost/settings/profiles", navigation.Uri);
    }

    // The Libraries levels are real addresses: a library's own page must not be redirected to the overview or /not-found.
    [Theory]
    [InlineData("/settings/libraries/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", null)]
    [InlineData("/settings/libraries/import", "import", null)]
    [InlineData("/settings/libraries/view/photos", "view", "photos")]
    public async Task SettingsPage_OnALibrariesLevelAddress_StaysOnThatAddress(string address, string subsection, string? detail)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(address);

        var cut = Render<Settings>(parameters =>
        {
            parameters.Add(page => page.Section, "libraries").Add(page => page.Subsection, subsection);
            if (detail is not null)
            {
                parameters.Add(page => page.Detail, detail);
            }
        });
        await Task.Delay(1500);

        Assert.Equal($"http://localhost{address}", navigation.Uri);
        Assert.NotEmpty(cut.FindAll(".libraries"));
    }

    // The tab title names a library and its area only when the page shows them: the older group, type and Vault addresses redirect, so they read as Libraries.
    [Theory]
    [InlineData("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", null, "Movies - Watch - Library")]
    [InlineData("view", "photos", "Photos - View - Library")]
    [InlineData("import", null, "Import folder - Library")]
    [InlineData("watch", null, "Libraries - Library")]
    [InlineData("watch", "movies", "Libraries - Library")]
    [InlineData("vault", null, "Libraries - Library")]
    public void SettingsPage_OnALibrariesAddress_TitlesTheTabFromTheLibraryAndAreaThatAreShown(string subsection, string? detail, string expectedTitle)
    {
        var cut = RenderLibraries(subsection, detail);

        // A library's name arrives with the libraries, after the first render.
        cut.WaitForAssertion(() => Assert.Equal(expectedTitle, (string)typeof(Settings).GetProperty("BrowserTitle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!));
    }

    // The earlier group address is redirected by the Libraries page to the matching tab of the overview.
    [Fact]
    public void SettingsPage_OnAnEarlierGroupAddress_RedirectsToThatAreasTab()
    {
        RenderLibraries("watch", null);

        Assert.EndsWith("/settings/libraries?area=watch", Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    // The Settings frame carries each Libraries level: one breadcrumb, one title, and no second heading or breadcrumb in the page.
    [Theory]
    [InlineData(null, null, "Libraries", "Libraries", true)]
    [InlineData("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", null, "Movies", "Libraries|Watch|Movies", false)]
    [InlineData("view", "photos", "Photos", "Libraries|View|Photos", false)]
    [InlineData("import", null, "Import folder", "Libraries|Import folder", false)]
    public void SettingsPage_OnALibrariesAddress_ShowsTheLevelInItsFrameBreadcrumbAndTitle(string? subsection, string? detail, string title, string trail, bool hasSubtitle)
    {
        var cut = RenderLibraries(subsection, detail);

        var expected = trail.Split('|');
        // The frame redraws once the kinds have arrived, so wait for the trail rather than reading it once.
        cut.WaitForAssertion(() =>
        {
            var steps = cut.FindAll(".settings-breadcrumbs a, .settings-breadcrumbs span[aria-current]").Select(step => step.TextContent.Trim()).ToArray();
            Assert.Equal(expected, steps[^expected.Length..]);
        });
        Assert.Equal(title, cut.Find(".app-page-header__title").TextContent.Trim());
        Assert.Equal(hasSubtitle, cut.FindAll(".app-page-header p").Count > 0);
        if (subsection is not null)
        {
            Assert.Equal("/settings/libraries", cut.FindAll(".settings-breadcrumbs a").Last(link => link.TextContent.Trim() == "Libraries").GetAttribute("href"));
        }

        Assert.Empty(cut.FindAll(".libraries nav[aria-label=Breadcrumb]"));
        // The Import folder panel and View's settings are unchanged panels with headings of their own; every other level has none.
        if (subsection != "import" && detail != "photos")
        {
            Assert.Empty(cut.FindAll(".libraries h1, .libraries h2, .libraries h3"));
        }
    }

    private IRenderedComponent<Settings> RenderLibraries(string? subsection, string? detail)
    {
        var address = "/settings/libraries" + (subsection is null ? string.Empty : "/" + subsection) + (detail is null ? string.Empty : "/" + detail);
        Services.GetRequiredService<NavigationManager>().NavigateTo(address);

        var cut = Render<Settings>(parameters =>
        {
            parameters.Add(page => page.Section, "libraries");
            if (subsection is not null)
            {
                parameters.Add(page => page.Subsection, subsection);
            }

            if (detail is not null)
            {
                parameters.Add(page => page.Detail, detail);
            }
        });
        cut.WaitForElement(".libraries");
        // The title and breadcrumb name the group and kind, which arrive with the list of library kinds.
        cut.WaitForState(() => Services.GetRequiredService<MediaEngine.Web.Services.Settings.LibraryKindsState>().Loaded);
        return cut;
    }

    [Theory]
    [InlineData("/settings", null)]
    [InlineData("/settings/overview", "overview")]
    [InlineData("/settings/overview/apps", "overview")]
    [InlineData("/settings/playback", "playback")]
    [InlineData("/settings/profiles", "profiles")]
    public void SettingsPage_ForAKidsProfile_RedirectsToHomeAndDrawsNothing(string typedAddress, string? section)
    {
        var kidsApi = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow),
            })));
        Services.AddSingleton<IEngineApiClient>(kidsApi);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(typedAddress);

        var cut = Render<Settings>(parameters =>
        {
            if (section is not null)
            {
                parameters.Add(page => page.Section, section);
            }
        });

        cut.WaitForAssertion(() => Assert.Equal("http://localhost/", navigation.Uri));
        Assert.Empty(cut.FindAll(".settings-shell-content"));
        Assert.Empty(cut.FindAll(".profiles-tab__card"));
        Assert.Empty(cut.FindAll("[data-settings-overview]"));
        Assert.Empty(cut.FindAll("[data-settings-overview-apps]"));
        Assert.Empty(cut.FindAll(".settings-playback-page"));
    }

    [Fact]
    public void LegacyRecentlyAddedReviewLink_RedirectsToTheNeedsReviewPageInSource()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Pages", "Settings.razor"));
        Assert.Contains("SettingsNav.RouteFor(SettingsSection.Review)", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(ReviewState, \"expanded\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/settings/recently-added?review=expanded\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrarySummary_CompositionSegmentsReflectEachCategoryShareOfTheWhole()
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
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<LibrarySummary>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(7, cut.FindAll(".library-summary__media").Count);
            var segments = cut.FindAll(".library-summary__segment");
            Assert.Equal(3, segments.Count);
            Assert.Contains("width:50%", segments[0].GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("width:25%", segments[1].GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("width:25%", segments[2].GetAttribute("style"), StringComparison.Ordinal);
            Assert.Contains("Books 2, 50%", cut.Find(".library-summary__composition").GetAttribute("aria-label"), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void LibrarySummary_ShowsItsTilesWithoutWaitingForTheStorageChecks_AndAsksOnlyForTheStorageChecks()
    {
        // Bug B17: the tiles waited for the full readiness report, which scans the whole data store.
        var storageChecks = new TaskCompletionSource<MediaEngine.Contracts.System.StartupReadinessResponse?>();
        EngineApiClientStub? stub = null;
        var apiClient = EngineApiClientStub.Create(configured =>
        {
            stub = configured;
            configured.SetHandler(nameof(IEngineApiClient.GetLibraryOverviewAsync), _ =>
                Task.FromResult<LibraryOverviewDto?>(new LibraryOverviewDto
                {
                    TotalItems = 3,
                    MediaTypeCounts = new Dictionary<string, int> { ["Books"] = 2, ["Movies"] = 1 },
                }));
            configured.SetHandler(nameof(IEngineApiClient.GetStorageReadinessAsync), _ => storageChecks.Task);
        });
        Services.AddSingleton<IEngineApiClient>(apiClient);

        var cut = Render(builder =>
        {
            builder.OpenComponent<LibrarySummary>(0);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll(".library-summary__media").Count));
        Assert.Empty(cut.FindAll("[data-testid='library-summary-storage']"));

        storageChecks.SetResult(new MediaEngine.Contracts.System.StartupReadinessResponse
        {
            Status = "degraded",
            Checks =
            [
                new MediaEngine.Contracts.System.StartupReadinessCheckResponse
                {
                    Name = "watch_folders",
                    Category = "storage",
                    Status = "degraded",
                    Description = "A watch folder is missing.",
                },
            ],
        });

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='library-summary-storage'] .library-summary__issue")));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetStartupReadinessAsync), stub!.Calls);
    }

    [Fact]
    public void PlaybackSettingsPage_RendersOnlyTheSelectedUserPlaybackPreferences()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(3);
            builder.AddAttribute(4, nameof(Settings.Section), "playback");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".tl-tabs"));
            Assert.Empty(cut.FindAll(".settings-subsection-nav"));
            // Five playback cards plus the Spoilers card (U-WP11), which sits with the profile's own preferences.
            Assert.Equal(6, cut.FindAll(".playback-settings-card").Count);
            Assert.Contains("Spoilers", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".settings-section-header"));
            Assert.NotEmpty(cut.FindAll(".playback-preference-row"));
            Assert.Contains("Autoplay", cut.Markup);
            Assert.Contains("Resume", cut.Markup);
            Assert.DoesNotContain("Subtitles &amp; Languages", cut.Markup);
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
    public void PlaybackSettingsPage_RendersOneWatchingPageWithoutSubsectionRouting()
    {
        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<PlaybackSettingsPage>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("overview", cut.Find(".settings-playback-page").GetAttribute("data-playback-section"));
            Assert.Contains("Autoplay next episode", cut.Markup);
            Assert.DoesNotContain("Default playback speed", cut.Markup);
            Assert.DoesNotContain("Audiobook default speed", cut.Markup);
            Assert.DoesNotContain("Adjust text while reading", cut.Markup);
            Assert.DoesNotContain("Choose tracks in the player", cut.Markup);
        });
    }

    [Fact]
    public void PlaybackLoadingRendersItsSizedSkeletonBeforeTheRequestCompletes()
    {
        var pending = new TaskCompletionSource<UserPlaybackSettingsDto?>();
        var stub = (EngineApiClientStub)(object)Services.GetRequiredService<IEngineApiClient>();
        stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ => pending.Task);
        var cut = Render<PlaybackSettingsPage>();
        Assert.Equal("width:;height:220px", cut.Find(".app-skeleton").GetAttribute("style"));
        pending.SetResult(null);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".app-skeleton")));
    }

    [Fact]
    public void PlaybackSettingsPage_UsesSharedControlsAndRealApiSavePath()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "PlaybackSettingsPage.razor"));

        Assert.DoesNotContain("<AppTabs", source);
        Assert.DoesNotContain("settings-playback-tabs", source);
        Assert.Contains("data-playback-section=\"overview\"", source);
        Assert.Contains("Size=\"AppControlSize.Compact\"", source);
        Assert.DoesNotContain("settings-field--compact", source);
        Assert.Contains("playback-preference-row", source);
        Assert.Contains("UpdateAndSaveAsync", source);
        Assert.DoesNotContain("Default playback speed", source);
        Assert.DoesNotContain("Audiobook default speed", source);
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
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "PlaybackDeliverySettingsTab.razor"))
                     + File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "EncodeSettingsTab.razor"));

        Assert.Contains("Optimized copy storage", source);
        Assert.Contains("Diagnostics", source);
        Assert.DoesNotContain("Allow direct play", source);
        Assert.DoesNotContain("Subtitle extraction", source);
        Assert.DoesNotContain("TODO: Persist direct play settings", source);
        Assert.DoesNotContain("TODO: Persist subtitle and audio delivery settings", source);
    }

    [Fact]
    public void ModelsTab_UsesEngineBackedProfileAndBenchmarkActions()
    {
        var source = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "ModelsTab.razor"));

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

        Assert.DoesNotContain("_status?.IsReady", source);
        Assert.DoesNotContain("Local AI is partially available", source);
        // The runtime (resource profile, benchmark, limits) lives once, on Tools and requirements; Local AI keeps AI settings only.
        var runtime = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Settings", "LocalAiRuntimePanel.razor"));
        Assert.DoesNotContain("<ModelsTab />", source);
        Assert.Single(Regex.Matches(runtime, "<ModelsTab />"));
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
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<CollectionsPage>(3);
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".tl-table"));
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
            // A Universe with a Wikidata identity opens its own page; one without keeps the generic collection page.
            Assert.Contains("href=\"/universe/Q4242\"", cut.Markup);
            Assert.Contains("href=\"/details/collection/10000000-0000-0000-0000-000000000002\"", cut.Markup);
            Assert.Contains(">Automatic<", cut.Markup);
            Assert.Contains(">Curated<", cut.Markup);
            Assert.Contains(">Shelves<", cut.Markup);
            Assert.Contains(">People<", cut.Markup);
            Assert.Contains("Broader worlds Library found by connecting trusted relationships", cut.Markup);
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
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
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
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
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
    public void CollectionsPeople_ShowsThePeopleWithoutWaitingForTheCollectionCatalog()
    {
        // Bug B31: People waited for the collection catalog, which takes seconds on a large library and is not shown there.
        var catalog = new TaskCompletionSource<List<CollectionManagementCatalogViewModel>>();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionCatalogAsync), _ => catalog.Task)));

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<CollectionsPage>(3);
            builder.AddAttribute(4, nameof(CollectionsPage.Section), "people");
            builder.CloseComponent();
        });

        cut.WaitForAssertion(() => Assert.Contains("Octavia Butler", cut.Markup));
        Assert.False(catalog.Task.IsCompleted);

        catalog.SetResult([]);
        cut.WaitForAssertion(() => Assert.Contains("Octavia Butler", cut.Markup));
    }

    [Fact]
    public void Collections_ASectionChosenDuringTheFirstLoad_SaysLoadingUntilItsOwnDataArrives()
    {
        // A replaced load's cancelled catalog read comes back empty, later than the newer load; it must not mark the
        // collections as loaded.
        var catalogs = new List<TaskCompletionSource<List<CollectionManagementCatalogViewModel>>>();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionCatalogAsync), _ =>
            {
                var catalog = new TaskCompletionSource<List<CollectionManagementCatalogViewModel>>();
                catalogs.Add(catalog);
                return catalog.Task;
            })));

        var cut = Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<AppDialogHost>(1);
            builder.CloseComponent();
            builder.OpenComponent<AppToastHost>(2);
            builder.CloseComponent();
            builder.OpenComponent<CollectionsPage>(3);
            builder.AddAttribute(4, nameof(CollectionsPage.Section), "automatic");
            builder.CloseComponent();
        });
        var page = cut.FindComponent<CollectionsPage>();
        cut.WaitForAssertion(() => Assert.Single(catalogs));

        // People is chosen before the first catalog arrives: a second load starts, people first, and replaces the first.
        page.Render(parameters => parameters.Add(component => component.Section, "people"));
        cut.WaitForAssertion(() => Assert.Contains("Octavia Butler", cut.Markup));
        Assert.Equal(2, catalogs.Count);
        cut.InvokeAsync(() => catalogs[0].SetResult([]));

        // Back to a collection section while the second catalog is still out: it says Loading, not an empty grid.
        page.Render(parameters => parameters.Add(component => component.Section, "automatic"));
        cut.WaitForAssertion(() => Assert.Contains("Loading collections", cut.Markup));
    }

    [Fact]
    public void LibraryConfigurableTable_RendersNativeTableShellAndSharedActions()
    {
        var cut = Render<LibraryConfigurableTable>(parameters => parameters
            .Add(component => component.Columns, LibraryColumnDefinitions.GetColumnsByTab("books"))
            .Add(component => component.Items, [CreateSampleLibraryItem()])
            .Add(component => component.Loading, false)
            .Add(component => component.GroupBy, "type")
            .Add(component => component.SelectedItems, new HashSet<Guid>()));

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".app-table-shell"));
            Assert.Contains("The Hobbit", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".app-button"));
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
                new ListenNavigationItem("Home", "/listen", AppMaterialIcons.Outlined.Home),
                new ListenNavigationItem("Music", "/listen/music", AppMaterialIcons.Outlined.MusicNote),
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
        Assert.DoesNotContain("AppMaterialIcons.Outlined.DownloadDone", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("AppMaterialIcons.Outlined.RadioButtonUnchecked", markup, StringComparison.Ordinal);
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
            if (request.RequestUri!.AbsolutePath == "/access/self-service/notices")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new MediaEngine.Contracts.Authentication.AccountNoticesResponse(DismissedNotices)),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
        }

        public string[] DismissedNotices { get; set; } = [];
    }

    private sealed class ThisComputerOnlyStateProvider : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        private static readonly Microsoft.AspNetCore.Components.Authorization.AuthenticationState State = new(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "Owner"),
                new System.Security.Claims.Claim("tuvima:profile_id", "00000000-0000-0000-0000-000000000001"),
                new System.Security.Claims.Claim("tuvima:active_profile_id", "00000000-0000-0000-0000-000000000001"),
                new System.Security.Claims.Claim("tuvima:session_id", "00000000-0000-0000-0000-000000000002"),
                new System.Security.Claims.Claim("tuvima:authentication_method", MediaEngine.Web.Services.Integration.ThisComputerRequests.AuthenticationMethod),
            ], "Test")));

        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(State);
    }

    private IRenderedComponent<IComponent> RenderSettingsForAThisComputerOnlyAccount(string[] dismissedNotices)
    {
        var profileId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var authority = new MediaEngine.Contracts.Authentication.DashboardAuthorityResponse(
            profileId, profileId, true, true, 1, 1, false, false, null, 1, [], [], []);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", profileId, profileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(new ThisComputerOnlyStateProvider());
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(
            new DelayedLockHandler(authority, session) { DismissedNotices = dismissedNotices }));
        return Render(builder =>
        {
            builder.OpenComponent<AppPopoverHost>(0);
            builder.CloseComponent();
            builder.OpenComponent<Settings>(1);
            builder.CloseComponent();
        });
    }

    [Fact]
    public void SettingsMenu_ShowsSecurityRedWithItsReasonForAThisComputerOnlyAccountUntilNotNow()
    {
        var cut = RenderSettingsForAThisComputerOnlyAccount([]);

        cut.WaitForAssertion(() =>
        {
            var link = cut.Find("a.media-section-shell__tree-link[href='/settings/security']");
            Assert.Equal("No password", link.QuerySelector(".media-section-shell__tree-attention-label")!.TextContent.Trim());
            Assert.Contains("is-attention-blocked", link.ParentElement!.ClassList);
        });
    }

    [Fact]
    public void SettingsMenu_HasNoSecurityDotOnceTheAccountSaidNotNow()
    {
        var cut = RenderSettingsForAThisComputerOnlyAccount([MediaEngine.Contracts.Authentication.AccountNoticeKeys.SecureAccount]);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("a.media-section-shell__tree-link[href='/settings/security']")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".media-section-shell__attention-dot")));
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
        Assert.Contains("min-height: var(--tl-menu-item-height) !important;", globalCss, StringComparison.Ordinal);
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
        Assert.Contains("position:relative", hostCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns:minmax(0,1fr) 300px minmax(0,1fr)", hostCss, StringComparison.Ordinal);
        Assert.Contains("height:calc(104px", hostCss, StringComparison.Ordinal);
        Assert.Contains("border-radius:0", hostCss, StringComparison.Ordinal);
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
        Assert.DoesNotContain("<AppSelectItem T=\"string\" Value=\"@(\"media_type\")\">Media Type</AppSelectItem>", source);
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
        Assert.Contains(".app-select", css);
        Assert.Contains(".tl-popover .tl-list", css);
        Assert.Contains(".app-artwork-picker__input", css);
        Assert.Contains("opacity: 0", css);
    }

    [Fact]
    public void AppCss_KeepsNativeDropdownRowsBorderless()
    {
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "wwwroot", "app.css"));

        Assert.Contains(".tl-popover .tl-list-item", css);
        Assert.Contains("border: 0 !important;", css);
        Assert.Contains("box-shadow: none !important;", css);

        var rowRuleBodies = Regex.Matches(css, @"(?s)\.tl-(?:list-item|menu-item)[^{]*\{(?<body>.*?)\}")
            .Select(match => match.Groups["body"].Value.ToLowerInvariant())
            .ToArray();

        Assert.NotEmpty(rowRuleBodies);

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
        Assert.Contains(".tl-form", css);
        Assert.Contains(".tl-card--flush", css);
        Assert.Contains(".app-select", css);
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
        var audit = File.ReadAllText(GetRepoFile("engineering", "design", "ui-consistency-audit.md"));

        Assert.Contains("Inconsistent Form And Input Styling", audit);
        Assert.Contains("Inline Styles", audit);
        Assert.Contains("Duplicate Card And Panel Patterns", audit);
        Assert.Contains("Recommended Priority Order", audit);
        Assert.Contains("tl-filter-bar", audit);
    }

    [Fact]
    public async Task MainLayout_PhoneThenTabletSwapsTheDockAndHamburgerForTheTopNavigation()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();

        await cut.InvokeAsync(() => device.SwitchDeviceAsync(PhoneDevice));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".layout-shell__intent-dock-link").Count);
            Assert.Single(cut.FindAll(".layout-shell__menu-button"));
        });

        await cut.InvokeAsync(() => device.SwitchDeviceAsync(new DeviceContextSnapshot("web", "tablet", true)));
        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".layout-shell__intent-dock-link"));
            Assert.Empty(cut.FindAll(".layout-shell__menu-button"));
            Assert.Equal(5, cut.FindAll(".layout-shell__nav-link").Count);
        });
    }

    [Fact]
    public async Task MainLayout_TvLibrariesTabLinksToTheLastLibraryAndOpensTheSwitcherOnALibrarySurface()
    {
        // The Libraries tab belongs to the TV dock now; the phone dock is Home, Search and My Library.
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();
        var nav = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => device.SwitchDeviceAsync(TvDevice));

        AngleSharp.Dom.IElement Libraries() => cut.FindAll(".layout-shell__intent-dock-link")[1];

        // Off a library surface the tab is a plain link to the default library and opens nothing.
        cut.WaitForAssertion(() => Assert.Equal("/watch", Libraries().GetAttribute("href")));
        Assert.Null(Libraries().GetAttribute("aria-haspopup"));
        Libraries().Click();
        Assert.Empty(cut.FindAll(".library-switcher"));

        // Visiting a lane makes it the one the tab returns to, and the tab is active on it.
        await cut.InvokeAsync(() => nav.NavigateTo("/read"));
        await cut.InvokeAsync(() => nav.NavigateTo("/details/work/abc"));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("/read", Libraries().GetAttribute("href"));
            Assert.DoesNotContain("is-active", Libraries().ClassName);
        });

        // On a library surface the tab is active and tapping it opens the switcher with six rows.
        await cut.InvokeAsync(() => nav.NavigateTo("/listen"));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("is-active", Libraries().ClassName);
            Assert.Equal("dialog", Libraries().GetAttribute("aria-haspopup"));
            Assert.Equal("/listen", Libraries().GetAttribute("href"));
        });
        Libraries().Click();
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".library-switcher__row");
            Assert.Equal(["Watch", "Read", "Listen", "View", "Collections", "Recently added"], rows.Select(row => row.TextContent.Trim()).ToArray());
            Assert.Equal(["/watch", "/read", "/listen", "/view", "/collections", "/recently-added"], rows.Select(row => row.GetAttribute("href")!).ToArray());
            Assert.Equal("page", rows[2].GetAttribute("aria-current"));
        });

        // Choosing a row closes the sheet.
        cut.FindAll(".library-switcher__row")[1].Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".library-switcher")));
    }

    [Fact]
    public async Task MainLayout_SearchTabOpensTheOverlayInsteadOfNavigating()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();
        var nav = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => device.SwitchDeviceAsync(PhoneDevice));
        var before = nav.Uri;

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".layout-shell__intent-dock-link").Count));
        cut.FindAll(".layout-shell__intent-dock-link")[1].Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".universal-search-overlay")));
        Assert.Equal(before, nav.Uri);
    }

    [Fact]
    public async Task MainLayout_TvKidsLibrariesSwitcherListsOnlyWatchListenAndRead()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();
        var nav = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => device.SwitchDeviceAsync(TvDevice));
        typeof(MainLayout).GetField("_isKids", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, true);
        await cut.InvokeAsync(() => nav.NavigateTo("/watch"));

        cut.WaitForAssertion(() => Assert.Contains("is-active", cut.FindAll(".layout-shell__intent-dock-link")[1].ClassName));
        cut.FindAll(".layout-shell__intent-dock-link")[1].Click();

        cut.WaitForAssertion(() => Assert.Equal(["Watch", "Listen", "Read"],
            cut.FindAll(".library-switcher__row").Select(row => row.TextContent.Trim()).ToArray()));
    }

    [Fact]
    public async Task MainLayout_RemembersTheLastLibraryPerProfileInBrowserStorage()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();
        var nav = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true)));

        var profileId = Guid.NewGuid();
        typeof(MainLayout).GetField("_activeProfileId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, profileId);
        await cut.InvokeAsync(() => nav.NavigateTo("/view"));

        var key = MediaEngine.Web.Services.Navigation.ShellDock.LastLibraryStorageKey(profileId);
        cut.WaitForAssertion(() => Assert.Contains(
            JSInterop.Invocations["localStorage.setItem"],
            invocation => invocation.Arguments.Count == 2 && (string?)invocation.Arguments[0] == key && (string?)invocation.Arguments[1] == "/view"));
    }

    [Fact]
    public void DownloadsPage_SaysDownloadsLiveInTheLibraryApp()
    {
        var cut = Render<DownloadsPage>();
        Assert.Contains("Downloads live in the Library app", cut.Markup);
    }

    [Fact]
    public async Task UniversalSearchOverlay_IsFullScreenWithCancelOnPhonesOnly()
    {
        var overlay = Render<MediaEngine.Web.Components.Search.UniversalSearchOverlay>();
        await overlay.InvokeAsync(() => overlay.Instance.Open());
        Assert.Empty(overlay.FindAll(".universal-search-overlay--phone"));
        Assert.Empty(overlay.FindAll(".universal-search-overlay__cancel"));

        var device = Services.GetRequiredService<DeviceContextService>();
        await overlay.InvokeAsync(() => device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true)));
        Assert.Single(overlay.FindAll(".universal-search-overlay--phone"));
        Assert.Single(overlay.FindAll(".universal-search-overlay__cancel"));
        Assert.Equal("search", overlay.Find(".universal-search-overlay__input-shell input").GetAttribute("enterkeyhint"));
    }

    [Theory]
    [InlineData("television", "tv", false)]
    public async Task MainLayout_KeepsTvDock(string deviceClass, string formFactor, bool touch)
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();

        await cut.InvokeAsync(() => formFactor.Length == 0
            ? device.SwitchDeviceAsync(deviceClass)
            : device.SwitchDeviceAsync(new DeviceContextSnapshot(deviceClass, formFactor, touch)));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["/", "/watch", "/search", "/downloads", "/settings"],
                cut.FindAll(".layout-shell__intent-dock-link").Select(link => link.GetAttribute("href")!).ToArray());
            Assert.Contains("is-device-profile-dock", cut.Find(".layout-shell__intent-dock").ClassName);
            Assert.Empty(cut.FindAll(".shell-menu-button"));
        });
    }

    [Fact]
    public async Task MainLayout_KidsPhoneDockHasTheSameThreeTabs()
    {
        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        var device = Services.GetRequiredService<DeviceContextService>();

        await cut.InvokeAsync(() => device.SwitchDeviceAsync(new DeviceContextSnapshot("mobile", "phone", true)));
        typeof(MainLayout).GetField("_isKids", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, true);
        cut.Render();

        cut.WaitForAssertion(() => Assert.Equal(["/", "/search", "/my-library"],
            cut.FindAll(".layout-shell__intent-dock-link").Select(link => link.GetAttribute("href")!).ToArray()));
    }

    [Fact]
    public void PhoneDockAndAudioDockOccupyDifferentGridRows()
    {
        var css = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Shared", "MainLayout.razor.css")).Replace("\r\n", "\n");
        Assert.Contains(".playback-app-frame__audio-dock {\n    grid-row: 3;", css);
        Assert.Contains("html[data-form-factor=\"phone\"] .playback-app-frame .layout-shell__intent-dock {\n    grid-row: 4;", css);
        Assert.DoesNotContain("@media (max-width: 600px)", css);
    }

    [Fact]
    public void LegacyNavMenuComponentIsGone()
    {
        Assert.False(File.Exists(GetRepoFile("src", "MediaEngine.Web", "Shared", "NavMenu.razor")));
        Assert.False(File.Exists(GetRepoFile("src", "MediaEngine.Web", "Components", "Shared", "NavMenu.razor")));
    }

    private static string GetRepoFile(params string[] segments) =>
        Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private IRenderedComponent<IComponent> RenderListenBrowsePageWithProviders(string? tab = null) => Render(builder =>
    {
        builder.OpenComponent<AppPopoverHost>(0);
        builder.CloseComponent();
        builder.OpenComponent<AppDialogHost>(1);
        builder.CloseComponent();
        builder.OpenComponent<AppToastHost>(2);
        builder.CloseComponent();
        builder.OpenComponent<ListenBrowsePage>(3);
        if (!string.IsNullOrWhiteSpace(tab))
        {
            builder.AddAttribute(4, nameof(ListenBrowsePage.Tab), tab);
        }
        builder.CloseComponent();
    });

    [Fact]
    public void SearchPage_RendersNativeSearchResultsWithoutRawCards()
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
                    "/details/work/30000000-0000-0000-0000-000000000101",
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
            builder.OpenComponent<AppPopoverHost>(0);
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
            if (!_open)
            {
                return;
            }

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
