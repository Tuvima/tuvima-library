using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Operations;
using MediaEngine.Web.Components.Artwork;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Artwork and Match and Identity sections of the editor (details overlay spec D7, D11 and D17), in an Editing Mode section
/// and in the Needs Review modal: artwork applies at once and refreshes the page behind it, Remove asks first in the shared
/// confirmation dialog, and Refresh metadata lives in Match and Identity for the people the Engine accepts for it.
/// </summary>
public sealed class ArtworkAndMatchSectionTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid PreferredLink = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private static readonly Guid PreferredAsset = Guid.Parse("22222222-0000-0000-0000-0000000000a1");
    private static readonly Guid OtherLink = Guid.Parse("22222222-0000-0000-0000-000000000002");
    private static readonly Guid OtherAsset = Guid.Parse("22222222-0000-0000-0000-0000000000a2");

    private readonly DashboardSessionAccessor _session = new();
    private readonly DeviceContextService _device;
    private readonly List<Guid> _removedLinks = [];
    private readonly List<ArtworkLinkRequest> _linked = [];
    private readonly List<(string ScopeId, string AssetType, string FileName)> _uploads = [];
    private readonly List<Guid> _deletedVariants = [];
    private readonly List<(string EntityType, Guid EntityId)> _refreshes = [];
    private readonly List<Dictionary<string, string>> _saves = [];
    private int _artworkChanged;
    private Func<Task<bool>> _upload = () => Task.FromResult(true);
    private string? _scheduleStatus;
    private Func<Task<EnrichmentRefreshQueuedResponse?>> _queueRefresh =
        () => Task.FromResult<EnrichmentRefreshQueuedResponse?>(new() { Message = "queued" });
    private string? _lastError;
    private readonly CapturingLoggerProvider _logs = new();

    public ArtworkAndMatchSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging(logging => logging.AddProvider(_logs));
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<LibraryItemHistoryDto>(), (string?)null)));
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult<ArtworkEntityWorkspaceDto?>(Workspace()));
            stub.SetHandler(nameof(IEngineApiClient.RemoveArtworkLinkAsync), args =>
            {
                _removedLinks.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.LinkArtworkAssetAsync), args =>
            {
                _linked.Add((ArtworkLinkRequest)args![2]!);
                return Task.FromResult<ArtworkEntityWorkspaceDto?>(Workspace());
            });
            stub.SetHandler(nameof(IEngineApiClient.UploadScopeArtworkVariantAsync), args =>
            {
                _uploads.Add(((string)args![1]!, (string)args[2]!, (string)args[4]!));
                return _upload();
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteArtworkAsync), args =>
            {
                _deletedVariants.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
            {
                _saves.Add((Dictionary<string, string>)args![1]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEntityEnrichmentRefreshScheduleAsync), _ =>
                Task.FromResult<EnrichmentRefreshScheduleDto?>(_scheduleStatus is null
                    ? null
                    : new EnrichmentRefreshScheduleDto { EntityType = "Work", EntityId = MovieId, Status = _scheduleStatus }));
            stub.SetHandler(nameof(IEngineApiClient.QueueEnrichmentRefreshNowAsync), args =>
            {
                _refreshes.Add(((string)args![0]!, (Guid)args[1]!));
                return _queueRefresh();
            });
            stub.SetHandler("get_LastError", _ => _lastError);
        });
        _device = new DeviceContextService(api);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_session);
        Services.AddSingleton(_device);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Services.AddScoped<MediaEditorLauncherService>();
        Render<AppPopoverHost>();
    }

    private static ArtworkEntityWorkspaceDto Workspace() => new(MovieId, "Work",
    [
        Variant(PreferredLink, PreferredAsset, preferred: true),
        Variant(OtherLink, OtherAsset, preferred: false),
    ])
    {
        SupportedRoles = [new ArtworkRoleDescriptorDto("Primary", "CoverArt", "cover", IsDefault: true)],
    };

    private static ArtworkEntityVariantDto Variant(Guid link, Guid asset, bool preferred) =>
        new(link, asset, "Primary", null, "CoverArt", preferred, false,
            $"/engine-image/stream/artwork/{asset:D}", $"/engine-image/stream/artwork/{asset:D}", 600, 900, "portrait", "tmdb", null);

    private void SignInAs(bool administrator)
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    private MediaEditorLaunchRequest Request(string? initialTab = null) => new()
    {
        EntityIds = [MovieId],
        LaunchEntityId = MovieId,
        MediaType = "Movies",
        InitialScope = "movie",
        InitialTab = initialTab,
        HeaderTitle = "Dune: Part One",
        OnArtworkChanged = () =>
        {
            _artworkChanged++;
            return Task.CompletedTask;
        },
    };

    private IRenderedComponent<EditorShell> RenderSection(string section)
    {
        var cut = Render<EditorShell>(parameters => parameters
            .Add(shell => shell.Request, Request(section))
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Section, section));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".sme-loading")));
        return cut;
    }

    private static void Choose(IRenderedComponent<AppDialogHost> host, string label)
    {
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == label).Click();
    }

    private static void ClickRemoveOnTheFirstImage(IRenderedComponent<EditorShell> cut) =>
        cut.FindAll("button[aria-label='Remove Cover artwork variant']").First().Click();

    // -- Artwork applies at once (D7) ------------------------------------------------------------

    [Fact]
    public void ChoosingAnotherSavedImageInASectionAppliesAtOnceAndRefreshesThePageWithNoSaveButton()
    {
        var cut = RenderSection("artwork");

        cut.Find("button[aria-label='Use this cover artwork as preferred']").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, _artworkChanged));
        var link = Assert.Single(_linked);
        Assert.Equal(OtherAsset, link.ArtworkAssetId);
        Assert.True(link.Preferred);
        Assert.Empty(cut.FindAll(".sme-footer"));
    }

    [Fact]
    public async Task APickedFileInASectionUploadsAtOnceAndRefreshesThePageWithNoSaveButton()
    {
        var cut = RenderSection("artwork");

        await cut.Instance.PickAsync("CoverArt", new FakeBrowserFile("cover.png"));

        var upload = Assert.Single(_uploads);
        Assert.Equal(("movie", "CoverArt", "cover.png"), upload);
        Assert.Equal(1, _artworkChanged);
        Assert.Empty(cut.FindAll(".sme-footer"));
    }

    [Fact]
    public async Task AnUploadStillOnItsWayInASectionShowsNoSaveButton()
    {
        var gate = new TaskCompletionSource<bool>();
        _upload = () => gate.Task;
        var cut = RenderSection("artwork");

        var picking = cut.Instance.PickAsync("CoverArt", new FakeBrowserFile("cover.png"));

        Assert.Empty(cut.FindAll(".sme-footer"));
        gate.SetResult(true);
        await picking;
        Assert.Empty(cut.FindAll(".sme-footer"));
        Assert.Equal(1, _artworkChanged);
    }

    [Fact]
    public async Task AFailedUploadInASectionSaysSoPlainlyAndRetryUploadsAgainWithoutASaveButton()
    {
        _upload = () => Task.FromResult(false);
        var cut = RenderSection("artwork");

        await cut.Instance.PickAsync("CoverArt", new FakeBrowserFile("cover.png"));

        var footer = cut.Find(".sme-footer");
        Assert.Contains("The image was not added, so nothing has changed yet.", footer.TextContent);
        Assert.Equal(["Retry upload"], footer.QuerySelectorAll("button").Select(button => button.TextContent.Trim()).ToArray());
        Assert.Equal(0, _artworkChanged);

        _upload = () => Task.FromResult(true);
        footer.QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".sme-footer")));
        Assert.Equal(2, _uploads.Count);
        Assert.Equal(1, _artworkChanged);
    }

    [Fact]
    public async Task ARetryAskedForTwiceWhileItRunsUploadsTheFileOnlyOnce()
    {
        _upload = () => Task.FromResult(false);
        var cut = RenderSection("artwork");
        await cut.Instance.PickAsync("CoverArt", new FakeBrowserFile("cover.png"));
        Assert.Single(_uploads);

        var gate = new TaskCompletionSource<bool>();
        _upload = () => gate.Task;
        var first = cut.Instance.RetryAsync();
        var second = cut.Instance.RetryAsync();
        await second;

        Assert.Equal(2, _uploads.Count);
        Assert.True(cut.Instance.RetryRunning);

        gate.SetResult(true);
        await first;

        Assert.Equal(2, _uploads.Count);
        Assert.False(cut.Instance.RetryRunning);
        Assert.Equal(1, _artworkChanged);
    }

    // -- Remove asks first, in the shared dialog (D7, D17) ---------------------------------------

    [Fact]
    public void RemovingASavedImageAsksFirstWithItsPreviewAndNothingIsRemovedYet()
    {
        var host = Render<AppDialogHost>();
        var cut = RenderSection("artwork");

        ClickRemoveOnTheFirstImage(cut);

        host.WaitForElement(".app-confirm");
        Assert.Equal("Remove this cover?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Contains("The image stays in the library.", host.Find(".app-confirm__message").TextContent);
        Assert.Contains(PreferredAsset.ToString("D"), host.Find(".artwork-remove-preview__image").GetAttribute("src"));
        var buttons = host.FindAll(".app-dialog-shell__footer button");
        Assert.Equal(["Keep", "Remove"], buttons.Select(button => button.TextContent.Trim()).ToArray());
        Assert.Contains("app-tone--error", buttons.Single(button => button.TextContent.Trim() == "Remove").ClassList);
        Assert.Empty(cut.FindAll(".artwork-workspace-core__delete-confirm"));
        Assert.Empty(_removedLinks);
    }

    [Fact]
    public void ChoosingRemoveRemovesTheImageAndRefreshesThePage()
    {
        var host = Render<AppDialogHost>();
        var cut = RenderSection("artwork");

        ClickRemoveOnTheFirstImage(cut);
        Choose(host, "Remove");

        cut.WaitForAssertion(() => Assert.Equal(PreferredLink, Assert.Single(_removedLinks)));
        cut.WaitForAssertion(() => Assert.Equal(1, _artworkChanged));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    [Theory]
    [InlineData("Keep")]
    [InlineData("Close dialog")]
    public async Task KeepingOrClosingTheRemoveQuestionLeavesTheImageAlone(string answer)
    {
        var host = Render<AppDialogHost>();
        var cut = RenderSection("artwork");

        ClickRemoveOnTheFirstImage(cut);
        host.WaitForElement(".app-confirm");
        if (answer == "Keep")
        {
            Choose(host, "Keep");
        }
        else
        {
            host.Find("button[aria-label='Close dialog']").Click();
        }

        cut.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        await Task.Yield();
        Assert.Empty(_removedLinks);
        Assert.Equal(0, _artworkChanged);
    }

    [Fact]
    public async Task TheEditorsOwnImageRemovalAsksFirstAndOnlyRemoveDeletesIt()
    {
        var host = Render<AppDialogHost>();
        var cut = RenderSection("artwork");
        var item = new ArtworkVariantDisplayItem(
            "variant", OtherAsset, "CoverArt", "/engine-image/stream/artwork/x", false, false, true, "Uploaded", null, null, null, null);

        var keeping = cut.Instance.RemoveAsync(item);
        Choose(host, "Keep");
        await keeping;
        Assert.Empty(_deletedVariants);

        var removing = cut.Instance.RemoveAsync(item);
        Assert.Equal("Remove this poster / cover?", host.WaitForElement(".app-dialog-shell__title").TextContent.Trim());
        Assert.Contains("deletes the uploaded image", host.Find(".app-confirm__message").TextContent);
        Choose(host, "Remove");
        await removing;

        Assert.Equal(OtherAsset, Assert.Single(_deletedVariants));
        Assert.Equal(1, _artworkChanged);
    }

    [Fact]
    public async Task RestoringAutomaticArtworkAsksInTheSharedDialogAndSaysWhatComesBack()
    {
        var host = Render<AppDialogHost>();

        var answer = ArtworkRemoveConfirmation.AskRestoreAutomaticAsync(Services.GetRequiredService<IAppDialogService>());
        host.WaitForElement(".app-confirm");

        Assert.Equal("Restore automatic artwork?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Contains("built from the titles you own", host.Find(".app-confirm__message").TextContent);
        var buttons = host.FindAll(".app-dialog-shell__footer button");
        Assert.Equal(["Keep custom covers", "Restore automatic artwork"], buttons.Select(button => button.TextContent.Trim()).ToArray());
        Assert.Contains("app-tone--error", buttons[1].ClassList);
        Choose(host, "Restore automatic artwork");
        Assert.True(await answer);
    }

    [Fact]
    public async Task KeepingTheCustomCoversAnswersNoToRestoringAutomaticArtwork()
    {
        var host = Render<AppDialogHost>();

        var answer = ArtworkRemoveConfirmation.AskRestoreAutomaticAsync(Services.GetRequiredService<IAppDialogService>());
        Choose(host, "Keep custom covers");

        Assert.False(await answer);
    }

    [Fact]
    public void TheEditorNoLongerAsksThroughTheMessageBoxOrAnInlineStrip()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var section = ReadSource("src/MediaEngine.Web/Components/MediaEditor/Sections/EditorArtworkSection.razor");
        var workspace = ReadSource("src/MediaEngine.Web/Components/Artwork/ArtworkWorkspace.razor");

        Assert.Contains("ArtworkRemoveConfirmation.AskRestoreAutomaticAsync", shell, StringComparison.Ordinal);
        Assert.Contains("ArtworkRemoveConfirmation.AskRemoveAsync", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowMessageBoxAsync", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-artwork-delete-confirm", section, StringComparison.Ordinal);
        Assert.Contains("ArtworkRemoveConfirmation.AskRemoveAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("delete-confirm", workspace, StringComparison.Ordinal);
    }

    // -- Refresh metadata lives in Match and Identity (D11) ---------------------------------------

    [Fact]
    public void RefreshMetadataIsShownInMatchAndIdentityToAnAdministrator()
    {
        SignInAs(administrator: true);

        var cut = RenderSection("links");

        var button = cut.Find(".sme-match-refresh button");
        Assert.Equal("Refresh metadata", button.TextContent.Trim());
        Assert.False(button.HasAttribute("disabled"));
    }

    [Fact]
    public void RefreshMetadataIsHiddenForSomeoneTheEngineWillNotAccept()
    {
        SignInAs(administrator: false);

        var cut = RenderSection("links");

        Assert.Empty(cut.FindAll(".sme-match-refresh"));
    }

    [Fact]
    public void RefreshMetadataIsHiddenWhenNobodyIsSignedIn()
    {
        var cut = RenderSection("links");

        Assert.Empty(cut.FindAll(".sme-match-refresh"));
    }

    [Fact]
    public void ChoosingRefreshMetadataCallsTheEngineOnceForTheCurrentItemAndSaysItStarted()
    {
        SignInAs(administrator: true);
        var cut = RenderSection("links");

        cut.Find(".sme-match-refresh button").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Refresh started. Details update when it finishes.",
            cut.Find(".sme-match-refresh [role='status']").TextContent.Trim()));
        Assert.Equal(("Work", MovieId), Assert.Single(_refreshes));
        Assert.Equal("Refresh metadata", cut.Find(".sme-match-refresh button").TextContent.Trim());
        Assert.Empty(cut.FindAll(".sme-match-refresh [role='alert']"));
    }

    [Fact]
    public async Task WhileTheRefreshRequestRunsTheButtonIsDisabledAndSaysRefreshing()
    {
        SignInAs(administrator: true);
        var gate = new TaskCompletionSource<EnrichmentRefreshQueuedResponse?>();
        _queueRefresh = () => gate.Task;
        var cut = RenderSection("links");

        cut.Find(".sme-match-refresh button").Click();

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find(".sme-match-refresh button");
            Assert.Equal("Refreshing…", button.TextContent.Trim());
            Assert.True(button.HasAttribute("disabled"));
        });
        Assert.Empty(cut.FindAll(".sme-match-refresh [role='status']"));

        gate.SetResult(new EnrichmentRefreshQueuedResponse { Message = "queued" });
        await Task.Yield();
        cut.WaitForAssertion(() => Assert.Equal("Refresh metadata", cut.Find(".sme-match-refresh button").TextContent.Trim()));
        Assert.Single(_refreshes);
    }

    [Fact]
    public void ARefusedRefreshShowsOnePlainLineAndKeepsTheEnginesRawErrorInTheLog()
    {
        SignInAs(administrator: true);
        _queueRefresh = () => Task.FromResult<EnrichmentRefreshQueuedResponse?>(null);
        _lastError = "HTTP 403: Unlock administrator settings first.";
        var cut = RenderSection("links");

        cut.Find(".sme-match-refresh button").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Refresh could not start. Try again later.", cut.Find(".sme-match-refresh [role='alert']").TextContent.Trim()));
        Assert.DoesNotContain("HTTP 403", cut.Find(".sme-match-refresh").TextContent);
        Assert.Empty(cut.FindAll(".sme-match-refresh [role='status']"));
        Assert.False(cut.Find(".sme-match-refresh button").HasAttribute("disabled"));
        Assert.Contains(_logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("HTTP 403: Unlock administrator settings first."));
    }

    [Fact]
    public void RefreshMetadataIsCheckedAgainWhenPressedSoAnAdministratorWhoLostTheRightCallsNothing()
    {
        SignInAs(administrator: true);
        var cut = RenderSection("links");
        var button = cut.Find(".sme-match-refresh button");

        SignInAs(administrator: false);
        button.Click();

        Assert.Empty(_refreshes);
        Assert.Empty(cut.FindAll(".sme-match-refresh [role='status']"));
    }

    [Fact]
    public void ARefreshThatCouldNotStartWithoutAnEngineMessageSaysTheSamePlainLine()
    {
        SignInAs(administrator: true);
        _queueRefresh = () => Task.FromResult<EnrichmentRefreshQueuedResponse?>(null);
        var cut = RenderSection("links");

        cut.Find(".sme-match-refresh button").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            "Refresh could not start. Try again later.", cut.Find(".sme-match-refresh [role='alert']").TextContent.Trim()));
    }

    [Fact]
    public void RefreshMetadataIsDisabledWithItsReasonWhileARefreshIsAlreadyWaiting()
    {
        SignInAs(administrator: true);
        _scheduleStatus = "Queued";
        var cut = RenderSection("links");

        var button = cut.Find(".sme-match-refresh button");
        Assert.True(button.HasAttribute("disabled"));
        Assert.Contains("A refresh is already waiting to run for this item.", cut.Find(".sme-match-refresh").TextContent);
        button.Click();
        Assert.Empty(_refreshes);
    }

    // -- Match and Identity heading, section and modal ----------------------------------------------

    [Fact]
    public void AnEditingSectionDoesNotRepeatTheMatchAndIdentityHeadingTheSlotAlreadyShows()
    {
        var cut = RenderSection("links");

        Assert.Empty(cut.FindAll(".sme-match-page-heading"));
        Assert.NotEmpty(cut.FindAll(".sme-match-tab"));
    }

    [Fact]
    public async Task TheNeedsReviewModalOpensTheSameSectionsAndKeepsItsOwnMatchHeading()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Render<AppDialogHost>();
        var launcher = Services.GetRequiredService<MediaEditorLauncherService>();

        var opened = launcher.TryOpenAsync(Request("links"));

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".sme-section-nav")));
        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".sme-loading")));
        var sections = host.FindAll(".sme-section-nav button").Select(button => button.TextContent.Trim()).ToArray();
        Assert.Equal(["Details", "Artwork", "Match & Identity", "History"], sections);
        Assert.Equal("Match & Identity", host.Find(".sme-match-page-heading").TextContent.Trim());
        Assert.False(opened.IsCompleted);
    }

    [Fact]
    public async Task TheNeedsReviewModalAsksTheSameRemoveQuestionForArtwork()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var host = Render<AppDialogHost>();
        var launcher = Services.GetRequiredService<MediaEditorLauncherService>();

        _ = launcher.TryOpenAsync(Request("artwork"));
        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll("button[aria-label='Remove Cover artwork variant']")));
        host.FindAll("button[aria-label='Remove Cover artwork variant']").First().Click();

        host.WaitForAssertion(() => Assert.NotEmpty(host.FindAll(".app-confirm")));
        Assert.Equal("Remove this cover?", host.Find(".app-confirm").Closest(".app-dialog-shell")!
            .QuerySelector(".app-dialog-shell__title")!.TextContent.Trim());
        Assert.Empty(_removedLinks);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Remove").Click();

        host.WaitForAssertion(() => Assert.Equal(PreferredLink, Assert.Single(_removedLinks)));
        host.WaitForAssertion(() => Assert.Equal(1, _artworkChanged));
    }

    [Fact]
    public async Task TheModalsFooterSaveStillSavesStagedDetailsChanges()
    {
        var cut = Render<EditorShell>(parameters => parameters.Add(shell => shell.Request, Request("details")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".sme-loading")));
        Assert.Empty(cut.FindAll(".sme-footer"));

        await cut.Instance.TypeAsync("custom_tags", "epic; classic");

        var save = cut.Find(".sme-footer .sme-footer-btn--primary");
        save.Click();
        cut.WaitForAssertion(() => Assert.Single(_saves));
        Assert.Equal("epic; classic", _saves[0]["custom_tags"]);
    }

    private static string ReadSource(
        string relativePath,
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }

    private sealed class EditorShell : SharedMediaEditorShell
    {
        public Task PickAsync(string assetType, IBrowserFile file) =>
            InvokeAsync(() => HandleArtworkSelectedAsync(assetType, new InputFileChangeEventArgs([file])));

        public Task RetryAsync() => InvokeAsync(RetryFailedArtworkUploadsAsync);

        public bool RetryRunning => IsArtworkRetryRunning;

        public Task RemoveAsync(ArtworkVariantDisplayItem item) =>
            InvokeAsync(() => DeleteArtworkVariantWithConfirmationAsync(item));

        public Task TypeAsync(string key, string value) =>
            InvokeAsync(() =>
            {
                OnFieldInput(key, value);
                StateHasChanged();
            });
    }

    private sealed class FakeBrowserFile(string name) : IBrowserFile
    {
        public string Name { get; } = name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => 4;
        public string ContentType => "image/png";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream([1, 2, 3, 4]);
    }
}
