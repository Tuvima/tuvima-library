using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Detail pages show a hero skeleton on first load and keep the previous page (dimmed and inert)
/// while the next item loads, so navigating between items never flashes blank or exposes stale actions.
/// </summary>
public sealed class DetailRouteHostLoadingTests : AsyncBunitContext
{
    private const int EntityIdIndex = 1;

    private readonly Guid _profile = Guid.NewGuid();
    private readonly Dictionary<Guid, TaskCompletionSource<DetailPageViewModel?>> _pending = [];

    public DetailRouteHostLoadingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<AuthenticationStateProvider>(new ProfileAuth(_profile));
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(_profile, "One", "#aaa", "RestrictedProfile", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), args =>
            {
                var id = (Guid)args![EntityIdIndex]!;
                var source = new TaskCompletionSource<DetailPageViewModel?>();
                _pending[id] = source;
                return source.Task;
            });
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args => Task.FromResult<MediaEditorContextDto?>(new MediaEditorContextDto
            {
                LaunchEntityId = (Guid)args![0]!,
                MediaType = "Audiobooks",
                InitialScope = "audiobook",
                Scopes =
                [
                    new MediaEditorScopeDto
                    {
                        ScopeId = "audiobook",
                        Label = "Audiobook",
                        FieldEntityId = (Guid)args[0]!,
                        CanEditFields = true,
                        CanEditArtwork = true,
                        ArtworkSlots = ["CoverArt"],
                        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
                        {
                            CanonicalFields = [new MediaEditorScopedFieldValueDto { Key = "title", Value = "First album", ProviderName = "apple_api" }],
                            AllowedOverrideKeys = ["title"],
                        },
                    },
                ],
            }));
        }));
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(provider => new SavedItemService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSingleton(provider => new MediaReactionService(provider.GetRequiredService<IEngineApiClient>()));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor, NoPlaybackPreferences>();
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));
    }

    [Fact]
    public void FirstLoadShowsAccessibleHeroSkeletonInsteadOfTheOldFullPageSpinner()
    {
        var request = Request(Guid.NewGuid());
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, request));
        cut.WaitForState(() => _pending.Count == 1);

        var status = cut.Find(".tl-detail-skeleton[role='status']");
        Assert.Contains("Loading details", status.TextContent);
        Assert.NotEmpty(cut.FindAll(".tl-detail-skeleton__artwork"));
        Assert.NotEmpty(cut.FindAll(".tl-detail-skeleton__tab"));
        Assert.Empty(cut.FindAll(".tl-detail-route-shell"));
    }

    [Fact]
    public void LoadedDetailReplacesTheSkeleton()
    {
        var id = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(id)));
        cut.WaitForState(() => _pending.ContainsKey(id));

        _pending[id].SetResult(Model("First album"));

        cut.WaitForAssertion(() => Assert.Contains("First album", cut.Markup));
        Assert.Empty(cut.FindAll(".tl-detail-skeleton"));
        var shell = cut.Find(".tl-detail-route-shell");
        Assert.False(shell.HasAttribute("inert"));
        Assert.False(shell.HasAttribute("aria-busy"));
        Assert.Empty(cut.FindAll(".tl-detail-route-progress"));
    }

    [Fact]
    public void NavigatingKeepsThePreviousPageVisibleButInertUntilTheNextArrives()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(first)));
        cut.WaitForState(() => _pending.ContainsKey(first));
        _pending[first].SetResult(Model("First album"));
        cut.WaitForAssertion(() => Assert.Contains("First album", cut.Markup));

        cut.Render(p => p.Add(c => c.Request, Request(second)));
        cut.WaitForState(() => _pending.ContainsKey(second));

        Assert.Contains("First album", cut.Markup);
        Assert.Empty(cut.FindAll(".tl-detail-skeleton"));
        var shell = cut.Find(".tl-detail-route-shell");
        Assert.True(shell.HasAttribute("inert"), "The previous item's actions must not be live while the next item loads.");
        Assert.Equal("true", shell.GetAttribute("aria-busy"));
        Assert.Contains("tl-detail-route-shell--loading", shell.ClassName);
        Assert.Contains("Loading details", cut.Find(".tl-detail-route-progress[role='status']").TextContent);

        _pending[second].SetResult(Model("Second album"));

        cut.WaitForAssertion(() => Assert.Contains("Second album", cut.Markup));
        Assert.DoesNotContain("First album", cut.Markup);
        shell = cut.Find(".tl-detail-route-shell");
        Assert.False(shell.HasAttribute("inert"));
        Assert.False(shell.HasAttribute("aria-busy"));
        Assert.Empty(cut.FindAll(".tl-detail-route-progress"));
    }

    [Fact]
    public void FailedNavigationShowsTheNotFoundStateInsteadOfTheStalePage()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(first)));
        cut.WaitForState(() => _pending.ContainsKey(first));
        _pending[first].SetResult(Model("First album"));
        cut.WaitForAssertion(() => Assert.Contains("First album", cut.Markup));

        var next = Request(second);
        cut.Render(p => p.Add(c => c.Request, next));
        cut.WaitForState(() => _pending.ContainsKey(second));
        _pending[second].SetResult(null);

        cut.WaitForAssertion(() => Assert.Contains(next.NotFoundTitle, cut.Markup));
        Assert.DoesNotContain("First album", cut.Markup);
        Assert.Empty(cut.FindAll(".tl-detail-route-shell"));
        Assert.Empty(cut.FindAll(".tl-detail-skeleton"));
    }

    [Fact]
    public void FirstLoadThatFindsNothingShowsTheNotFoundState()
    {
        var id = Guid.NewGuid();
        var request = Request(id);
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, request));
        cut.WaitForState(() => _pending.ContainsKey(id));

        _pending[id].SetResult(null);

        cut.WaitForAssertion(() => Assert.Contains(request.NotFoundTitle, cut.Markup));
        Assert.Empty(cut.FindAll(".tl-detail-skeleton"));
    }

    [Fact]
    public void SkeletonSizesComeFromTheScopedStylesheetNotInlineStyles()
    {
        var id = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(id)));
        cut.WaitForState(() => _pending.ContainsKey(id));

        Assert.Empty(cut.FindAll(".tl-detail-skeleton [style]"));
    }

    // -- Editing Mode on a loaded page ---------------------------------------------------------

    private async Task UseDesktopWithAMouseAsync()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        Services.AddSingleton(device);
    }

    private static DetailPageViewModel EditableModel(Guid id, string title, bool allowEdit = true) => new()
    {
        Id = id.ToString("D"),
        EntityType = DetailEntityType.Work,
        Title = title,
        Facts = new DetailFactsViewModel { MediaKind = "Audiobook" },
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = allowEdit ? [new DetailAction { Key = "edit", Label = "Edit details", Icon = "edit" }] : [],
    };

    [Fact]
    public async Task ALoadedPageNeverStartsInEditingModeAndOnlyShowsEditToAnEditor()
    {
        await UseDesktopWithAMouseAsync();
        var id = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(id)));
        cut.WaitForState(() => _pending.ContainsKey(id));

        _pending[id].SetResult(EditableModel(id, "First album"));

        cut.WaitForAssertion(() => Assert.Equal("Edit", cut.Find("button.tl-editing-toggle").TextContent.Trim()));
        Assert.Empty(cut.FindAll(".tl-editing-bar__action"));
        Assert.Empty(cut.FindAll(".tl-editing-pencil"));
        Assert.Empty(cut.FindAll(".tl-editing-drawer"));
    }

    [Fact]
    public async Task APageWithoutTheEditActionShowsNoEditingControlAtAll()
    {
        await UseDesktopWithAMouseAsync();
        var id = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(id)));
        cut.WaitForState(() => _pending.ContainsKey(id));

        _pending[id].SetResult(EditableModel(id, "First album", allowEdit: false));

        cut.WaitForAssertion(() => Assert.Contains("First album", cut.Markup));
        Assert.Empty(cut.FindAll(".tl-editing-bar"));
        Assert.Empty(cut.FindAll(".tl-editing-toggle"));
    }

    [Fact]
    public async Task EditingModeEndsWhenTheNextItemOpens()
    {
        await UseDesktopWithAMouseAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(first)));
        cut.WaitForState(() => _pending.ContainsKey(first));
        _pending[first].SetResult(EditableModel(first, "First album"));
        cut.WaitForAssertion(() => Assert.Equal("Edit", cut.Find("button.tl-editing-toggle").TextContent.Trim()));

        cut.Find("button.tl-editing-toggle").Click();
        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find("button.tl-editing-toggle").TextContent.Trim()));
        Assert.NotEmpty(cut.FindAll(".tl-editing-bar__action"));

        cut.Render(p => p.Add(c => c.Request, Request(second)));
        cut.WaitForState(() => _pending.ContainsKey(second));
        _pending[second].SetResult(EditableModel(second, "Second album"));

        cut.WaitForAssertion(() => Assert.Contains("Second album", cut.Markup));
        Assert.Equal("Edit", cut.Find("button.tl-editing-toggle").TextContent.Trim());
        Assert.Empty(cut.FindAll(".tl-editing-bar__action"));
    }

    [Fact]
    public async Task TheMoreEntryThatOpenedTheModalNowTogglesEditingMode()
    {
        await UseDesktopWithAMouseAsync();
        var id = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(id)));
        cut.WaitForState(() => _pending.ContainsKey(id));
        _pending[id].SetResult(EditableModel(id, "First album"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-detail-secondary-actions button[aria-label='More actions']")));

        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        await cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Edit details")).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find("button.tl-editing-toggle").TextContent.Trim()));
        Assert.NotEmpty(cut.FindAll(".tl-editing-bar__action"));
        Assert.Empty(cut.FindAll(".sme-dialog"));
    }

    [Fact]
    public async Task APageOnATabletOffersNoEditingControlsEvenWithPermission()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true));
        Services.AddSingleton(device);
        var id = Guid.NewGuid();
        var cut = Render<DetailRouteHost>(p => p.Add(c => c.Request, Request(id)));
        cut.WaitForState(() => _pending.ContainsKey(id));

        _pending[id].SetResult(EditableModel(id, "First album"));

        cut.WaitForAssertion(() => Assert.Contains("First album", cut.Markup));
        Assert.Empty(cut.FindAll(".tl-editing-bar"));
        Assert.Empty(cut.FindAll(".tl-editing-toggle"));
        Assert.DoesNotContain("Edit details", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit on a computer", cut.Markup, StringComparison.Ordinal);
    }

    private static DetailRouteRequest Request(Guid id) => DetailRouteRequest.ForUnified("musicalbum", id, null);

    private static DetailPageViewModel Model(string title) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        EntityType = DetailEntityType.Work,
        Title = title,
        Facts = new DetailFactsViewModel { MediaKind = "Audiobook" },
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
    };

    private sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class ProfileAuth(Guid profile) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profile.ToString("D"))], "test"))));
    }
}
