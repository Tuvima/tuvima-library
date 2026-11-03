using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Artwork tool of the universal editor (spec WP6, D8, D9): the floating picker's tabs for each kind of item, picking only previews
/// and writes nothing, "Use this artwork" stages, "Remove artwork" asks first, and the temporary view for a season poster comes and goes
/// without making a season page.
/// </summary>
public sealed class ArtworkPickerTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid FirstPicture = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SecondPicture = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ProviderPicture = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(string Name, object?[]? Args)> _writes = [];
    private string? _providerMessage;
    private ArtworkEntityWorkspaceDto _workspace = default!;

    public ArtworkPickerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Render<AppPopoverHost>();
        _workspace = Workspace(("CoverArt", FirstPicture, true), ("CoverArt", SecondPicture, false));
    }

    private static ArtworkEntityWorkspaceDto Workspace(params (string Slot, Guid Asset, bool Current)[] pictures) =>
        new(MovieId, "Work", pictures.Select(p => new ArtworkEntityVariantDto(
            Guid.NewGuid(), p.Asset, p.Slot is "Background" or "SeasonThumb" ? "Background" : p.Slot == "Logo" ? "Logo" : "Primary", null, p.Slot, p.Current, false,
            $"/art/{p.Asset:N}/large", $"/art/{p.Asset:N}/small", 1000, 1500, "Portrait", "tmdb", null)).ToList());

    private static MediaEditorContextDto SeasonContext() => new()
    {
        LaunchEntityId = MovieId,
        MediaType = "TV",
        InitialScope = "season",
        AvailableTabs = ["details", "artwork"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = "season",
                Label = "Season 1",
                FieldEntityId = MovieId,
                DisplayTitle = "Season 1",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["SeasonPoster", "SeasonThumb"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
            },
        ],
    };

    [Fact]
    public async Task FetchAllStills_StagesAStillForEveryEpisode_AndWritesNothingUntilSave()
    {
        var context = SeasonContext();
        context.Scopes[0].ArtworkSlots = ["SeasonPoster", "EpisodeStill"];
        var harness = await StartAsync(context);
        Guid[] episodes = [Guid.NewGuid(), Guid.NewGuid()];
        harness.Picker.EpisodeSource = () => Task.FromResult<IReadOnlyList<Guid>>(episodes);

        Assert.True(harness.Picker.CanFetchAllStills);
        var staged = await harness.Picker.FetchAllStillsAsync();

        Assert.Equal(2, staged);
        Assert.Equal(2, harness.Session.Pending.Changes.Count(change => change.Kind == EditSessionChangeKinds.Artwork));
        Assert.Contains("2 episodes", harness.Picker.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_writes, write => write.Name == nameof(IEngineApiClient.RunEditSessionAsync));
    }

    [Fact]
    public async Task FetchAllStills_IsNotOfferedForAMovie()
    {
        var harness = await StartAsync();
        harness.Picker.EpisodeSource = () => Task.FromResult<IReadOnlyList<Guid>>([]);

        Assert.False(harness.Picker.CanFetchAllStills);
    }

    private sealed record Harness(
        EditingModeContext Mode,
        EditSessionState Session,
        EditSessionFlow Flow,
        ArtworkEditPreview Preview,
        ArtworkPickerSession Picker);

    private async Task<Harness> StartAsync(MediaEditorContextDto? context = null)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        context ??= EditingModeTestData.MovieContext();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(context));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult<ArtworkEntityWorkspaceDto?>(_workspace));
            stub.SetHandler(nameof(IEngineApiClient.DiscoverProviderArtworkAsync), _ => Task.FromResult<ProviderArtworkDiscoveryDto?>(
                _providerMessage is { } message
                    ? new ProviderArtworkDiscoveryDto([], message)
                    : new ProviderArtworkDiscoveryDto([new ProviderArtworkCandidateDto("tmdb-1", "TMDB", "/thumb/tmdb-1", 1000, 1500, false)], null)));
            stub.SetHandler(nameof(IEngineApiClient.ImportProviderArtworkAsync), args =>
            {
                _writes.Add((nameof(IEngineApiClient.ImportProviderArtworkAsync), args));
                _workspace = Workspace(("CoverArt", FirstPicture, true), ("CoverArt", ProviderPicture, false));
                return Task.FromResult<ProviderArtworkImportResultDto?>(
                    new ProviderArtworkImportResultDto([new ProviderArtworkImportItemDto("tmdb-1", ProviderPicture, null)]));
            });
            stub.SetHandler(nameof(IEngineApiClient.UploadCanonicalArtworkAsync), args =>
            {
                _writes.Add((nameof(IEngineApiClient.UploadCanonicalArtworkAsync), args));
                _workspace = Workspace(("CoverArt", FirstPicture, true), ("CoverArt", SecondPicture, false));
                return Task.FromResult<ArtworkAssetDto?>(Asset(SecondPicture));
            });
            stub.SetHandler(nameof(IEngineApiClient.AddArtworkFromUrlAsync), args =>
            {
                _writes.Add((nameof(IEngineApiClient.AddArtworkFromUrlAsync), args));
                return Task.FromResult<ArtworkAssetDto?>(Asset(SecondPicture));
            });
            stub.SetHandler(nameof(IEngineApiClient.LinkArtworkAssetAsync), args =>
            {
                _writes.Add((nameof(IEngineApiClient.LinkArtworkAssetAsync), args));
                return Task.FromResult<ArtworkEntityWorkspaceDto?>(_workspace);
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                _writes.Add((nameof(IEngineApiClient.RunEditSessionAsync), args));
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult { Valid = true }));
            });
        });

        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, context.MediaType, context.InitialScope, null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(
            session, mode, new EditSessionSaver(api), () => [new EditTrailItem(MovieId, "Dune: Part One", "Movie")], () => Task.FromResult(true));
        Assert.True(await mode.EnterAsync());
        var preview = new ArtworkEditPreview(session, fields);
        return new Harness(mode, session, flow, preview, new ArtworkPickerSession(api, session, fields, preview));
    }

    private static ArtworkAssetDto Asset(Guid id) => new(id, $"/art/{id:N}/large", $"/art/{id:N}/small", 1000, 1500, "Portrait", "user_upload", null, []);

    private async Task<(Harness Harness, IRenderedComponent<EditorArtworkTool> Cut)> OpenPickerAsync(MediaEditorContextDto? context = null)
    {
        var harness = await StartAsync(context);
        await harness.Flow.SelectToolAsync(EditTool.Artwork);
        var cut = Render<EditorArtworkTool>(parameters => parameters
            .Add(picker => picker.Mode, harness.Mode)
            .Add(picker => picker.Picker, harness.Picker));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        return (harness, cut);
    }

    private static IReadOnlyList<string> Tabs(IRenderedComponent<EditorArtworkTool> cut) =>
        cut.FindAll(".tl-art-picker__tabs .app-segmented-control__item").Select(tab => tab.TextContent.Trim()).ToList();

    // -- Tabs ----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePicker_IsANamedRegionOfTheWindow_AndNamesItsPictureGroups()
    {
        var (_, cut) = await OpenPickerAsync();

        var root = cut.Find(".tl-art-picker");
        Assert.Equal("region", root.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(root.GetAttribute("aria-label")));
        var grid = cut.Find(".tl-art-picker__grid");
        Assert.Equal("group", grid.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(grid.GetAttribute("aria-label")));
        Assert.All(cut.FindAll(".tl-art-picker__tile"), tile => Assert.False(string.IsNullOrWhiteSpace(tile.GetAttribute("aria-label"))));
    }

    [Fact]
    public async Task AMovieOffersBackgroundPosterAndLogo()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Equal(["Background", "Poster", "Logo"], Tabs(cut));
    }

    [Fact]
    public async Task ASeasonOffersItsSeasonPictures()
    {
        _workspace = Workspace(("SeasonPoster", FirstPicture, true));
        var (_, cut) = await OpenPickerAsync(SeasonContext());

        Assert.Equal(["Season poster", "Season thumbnail"], Tabs(cut));
    }

    [Theory]
    [InlineData("Movies", "Poster")]
    [InlineData("TV", "Poster")]
    [InlineData("Books", "Cover")]
    [InlineData("Music", "Cover")]
    public void TheCoverTabIsCalledPosterOrCoverByKindOfMedia(string mediaType, string label) =>
        Assert.Equal(label, Assert.Single(ArtworkPickerRules.TabsFor(["CoverArt"], mediaType)).Label);

    [Fact]
    public void SlotsThePickerDoesNotEditAreLeftOut() =>
        Assert.Equal(["Background", "Logo"], ArtworkPickerRules.TabsFor(["Logo", "Banner", "Background"], "Movies").Select(tab => tab.Slot));

    [Fact]
    public void APersonsPhotoIsAPickerTabCalledPhoto() =>
        Assert.Equal("Photo", Assert.Single(ArtworkPickerRules.TabsFor(["Headshot"], "person")).Label);

    // -- Picking previews, Use stages --------------------------------------------------------------

    [Fact]
    public async Task PickingASavedPicture_PreviewsItOnThePageAndWritesAndStagesNothing()
    {
        var (harness, cut) = await OpenPickerAsync();
        Assert.Equal("CoverArt", harness.Picker.ActiveSlot);

        cut.FindAll(".tl-art-picker__tile").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal($"/art/{SecondPicture:N}/large", harness.Preview.Url("CoverArt")));
        Assert.True(harness.Session.Pending.IsEmpty);
        Assert.Empty(_writes);
        Assert.Contains("Previewing on the page", cut.Find(".tl-art-picker__status").TextContent);
    }

    [Fact]
    public async Task UseThisArtwork_StagesTheChoiceAndStillWritesNothing()
    {
        var (harness, cut) = await OpenPickerAsync();
        cut.FindAll(".tl-art-picker__tile").Last().Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.tl-art-picker__use").HasAttribute("disabled")));

        cut.Find("button.tl-art-picker__use").Click();

        cut.WaitForAssertion(() =>
        {
            var change = Assert.Single(harness.Session.Pending.Changes);
            Assert.Equal(EditSessionChangeKinds.Artwork, change.Kind);
            Assert.Equal("CoverArt", change.Slot);
            Assert.Equal(MovieId, change.EntityId);
            Assert.Equal(SecondPicture.ToString("D"), change.TargetId);
        });
        Assert.Empty(_writes);
        Assert.Equal($"/art/{SecondPicture:N}/large", harness.Preview.Url("CoverArt"));
    }

    [Fact]
    public async Task UseThisArtwork_IsOffUntilAPictureIsPicked()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.True(cut.Find("button.tl-art-picker__use").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ChangingTabWithoutUsingThePick_DropsThePreviewAndStagesNothing()
    {
        var (harness, cut) = await OpenPickerAsync();
        cut.FindAll(".tl-art-picker__tile").Last().Click();
        cut.WaitForAssertion(() => Assert.NotNull(harness.Preview.Url("CoverArt")));

        cut.FindAll(".tl-art-picker__tabs .app-segmented-control__item").Single(tab => tab.TextContent.Trim() == "Logo").Click();

        cut.WaitForAssertion(() => Assert.Null(harness.Preview.Url("CoverArt")));
        Assert.True(harness.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task AnUnmatchedShow_OffersMatchAndIdentityBesideTheProviderMessage()
    {
        _providerMessage = ProviderArtworkDiscoveryDto.UnmatchedTvShowMessage;
        _workspace = Workspace(("SeasonPoster", FirstPicture, true));
        var (harness, cut) = await OpenPickerAsync(SeasonContext());
        cut.FindAll(".app-segmented-control__item").Single(item => item.TextContent.Trim() == "Providers").Click();
        cut.WaitForAssertion(() => Assert.Contains("not matched to TheTVDB", cut.Find(".tl-art-picker__hint").TextContent));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Open Match and Identity").Click();

        cut.WaitForAssertion(() => Assert.Equal(EditingSection.Match, harness.Mode.OpenSectionKind));
    }

    [Fact]
    public async Task APickedProviderPicture_IsOnlyAddedWhenItIsUsed()
    {
        var (harness, cut) = await OpenPickerAsync();
        cut.FindAll(".app-segmented-control__item").Single(item => item.TextContent.Trim() == "Providers").Click();
        cut.WaitForAssertion(() => Assert.Contains("TMDB", cut.Find(".tl-art-picker__tile").TextContent));

        cut.Find(".tl-art-picker__tile").Click();
        cut.WaitForAssertion(() => Assert.Equal("/thumb/tmdb-1", harness.Preview.Url("CoverArt")));
        Assert.Empty(_writes);

        cut.Find("button.tl-art-picker__use").Click();

        cut.WaitForAssertion(() => Assert.Equal(ProviderPicture.ToString("D"), Assert.Single(harness.Session.Pending.Changes).TargetId));
        Assert.Equal([nameof(IEngineApiClient.ImportProviderArtworkAsync)], _writes.Select(write => write.Name));
    }

    [Fact]
    public async Task AnUpload_IsStoredUnchosenAndPickedNotStaged()
    {
        var (harness, _) = await OpenPickerAsync();

        Assert.True(await harness.Picker.UploadAsync(new FakeFile("poster.png", "image/png")));

        var upload = Assert.Single(_writes);
        Assert.Equal(nameof(IEngineApiClient.UploadCanonicalArtworkAsync), upload.Name);
        Assert.Contains(false, upload.Args!);
        Assert.True(harness.Picker.HasPick);
        Assert.True(harness.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task APastedLink_IsStoredUnchosenAndNeedsAWebAddress()
    {
        var (harness, _) = await OpenPickerAsync();

        Assert.False(await harness.Picker.PasteLinkAsync("not a link"));
        Assert.NotNull(harness.Picker.Message);
        Assert.Empty(_writes);

        Assert.True(await harness.Picker.PasteLinkAsync("https://example.test/poster.jpg"));
        var request = Assert.IsType<ArtworkFromUrlRequest>(Assert.Single(_writes).Args![2]);
        Assert.False(request.Preferred);
        Assert.Equal("CoverArt", request.SourceAssetType);
        Assert.True(harness.Session.Pending.IsEmpty);
    }

    // -- Remove artwork asks first ---------------------------------------------------------------------

    [Fact]
    public async Task RemoveArtwork_AsksFirst_AndStagesNothingUntilTheyAgree()
    {
        var host = Render<AppDialogHost>();
        var (harness, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__remove").Click();

        host.WaitForElement(".app-confirm");
        Assert.True(harness.Session.Pending.IsEmpty);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep").Click();
        cut.WaitForAssertion(() => Assert.True(harness.Session.Pending.IsEmpty));

        cut.Find("button.tl-art-picker__remove").Click();
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Remove").Click();

        cut.WaitForAssertion(() =>
        {
            var change = Assert.Single(harness.Session.Pending.Changes);
            Assert.Equal(EditSessionArtworkTargets.None, change.TargetId);
            Assert.Equal("CoverArt", change.Slot);
        });
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task UndoThisChoice_TakesTheStagedChangeBackOut()
    {
        var (harness, cut) = await OpenPickerAsync();
        cut.FindAll(".tl-art-picker__tile").Last().Click();
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.tl-art-picker__use").HasAttribute("disabled")));
        cut.Find("button.tl-art-picker__use").Click();
        cut.WaitForAssertion(() => Assert.False(harness.Session.Pending.IsEmpty));

        cut.Find("button.tl-art-picker__undo").Click();

        cut.WaitForAssertion(() => Assert.True(harness.Session.Pending.IsEmpty));
        Assert.Null(harness.Preview.Url("CoverArt"));
    }

    // -- What the tool shows ----------------------------------------------------------------------------

    [Fact]
    public async Task APreviewedPicture_ShowsInTheTool_UntilItsChangeIsTakenBack()
    {
        var harness = await StartAsync();
        await harness.Picker.OpenAsync("Background");
        Assert.Null(harness.Preview.Url("Background"));

        harness.Preview.Pick("Background", SecondPicture.ToString("D"), "/preview/backdrop");
        Assert.Equal("/preview/backdrop", harness.Preview.Url("Background"));

        harness.Session.Pending.SetArtwork(MovieId, "Background", SecondPicture.ToString("D"));
        harness.Preview.RememberStaged("Background", SecondPicture.ToString("D"), "/preview/backdrop");
        Assert.False(harness.Preview.HasPick("Background"));
        Assert.Equal("/preview/backdrop", harness.Preview.Url("Background"));

        harness.Session.Pending.Remove(harness.Session.Pending.Changes.Single().Id);
        Assert.Null(harness.Preview.Url("Background"));
    }

    private sealed class FakeFile(string name, string contentType) : IBrowserFile
    {
        public string Name => name;

        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;

        public long Size => 4;

        public string ContentType => contentType;

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream([1, 2, 3, 4]);
    }
}
