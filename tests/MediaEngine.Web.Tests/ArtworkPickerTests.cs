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
    private int _providerAsked;
    private IReadOnlyList<ProviderArtworkCandidateDto>? _providerItems;
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
            $"/art/{p.Asset:N}/large", $"/art/{p.Asset:N}/small", 1000, 1500, "Portrait", "tmdb", null)
        {
            FileName = "poster.jpg",
            FileSizeBytes = 2_400_000,
            ContentType = "image/jpeg",
        }).ToList());

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
            stub.SetHandler(nameof(IEngineApiClient.DiscoverProviderArtworkAsync), _ =>
            {
                _providerAsked++;
                return Task.FromResult<ProviderArtworkDiscoveryDto?>(
                    _providerMessage is { } message
                        ? new ProviderArtworkDiscoveryDto([], message)
                        : new ProviderArtworkDiscoveryDto(_providerItems ?? [new ProviderArtworkCandidateDto("tmdb-1", "TMDB", "/thumb/tmdb-1", 1000, 1500, false)], null));
            });
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
            stub.SetHandler(nameof(IEngineApiClient.RemoveArtworkLinkAsync), args =>
            {
                _writes.Add((nameof(IEngineApiClient.RemoveArtworkLinkAsync), args));
                return Task.FromResult(true);
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
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__version")));
        return (harness, cut);
    }

    private static IReadOnlyList<string> Roles(IRenderedComponent<EditorArtworkTool> cut) =>
        cut.FindAll(".tl-art-picker__role strong").Select(role => role.TextContent.Trim()).ToList();

    private static AngleSharp.Dom.IElement RoleCard(IRenderedComponent<EditorArtworkTool> cut, string label) =>
        cut.FindAll(".tl-art-picker__role").Single(card => card.QuerySelector("strong")!.TextContent.Trim() == label);

    private static IReadOnlyList<string> VersionOrder(IRenderedComponent<EditorArtworkTool> cut) =>
        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)")
            .Select(version => version.QuerySelector("img")!.GetAttribute("src")!)
            .Select(src => src.Split('/')[2])
            .ToList();

    // Refresh asks the providers for pictures and opens them in place of the preview (the old "Add artwork" door).
    private static void OpenAddPanel(IRenderedComponent<EditorArtworkTool> cut) => cut.Find("button.tl-art-picker__refresh").Click();

    // -- Role cards ----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePicker_IsANamedRegionOfTheWindow_AndNamesItsVersionStrip()
    {
        var (_, cut) = await OpenPickerAsync();

        var root = cut.Find(".tl-art-picker");
        Assert.Equal("region", root.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(root.GetAttribute("aria-label")));
        var strip = cut.Find(".tl-art-picker__strip");
        Assert.Equal("group", strip.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(strip.GetAttribute("aria-label")));
        Assert.All(cut.FindAll(".tl-art-picker__version"), version => Assert.False(string.IsNullOrWhiteSpace(version.GetAttribute("aria-label"))));
        Assert.All(cut.FindAll(".tl-art-picker__role"), card => Assert.False(string.IsNullOrWhiteSpace(card.GetAttribute("aria-label"))));
    }

    [Fact]
    public async Task AMovieHasARoleCardForBackgroundPosterAndLogo()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Equal(["Background", "Poster", "Logo"], Roles(cut));
    }

    [Fact]
    public async Task ARoleTabSaysWhetherAVersionIsPreferred_AndShowsHowManyVersionsThereAre()
    {
        var (_, cut) = await OpenPickerAsync();

        var poster = RoleCard(cut, "Poster");
        Assert.Contains("Preferred", poster.GetAttribute("aria-label"));
        Assert.Contains("2 versions", poster.GetAttribute("aria-label"));
        var logo = RoleCard(cut, "Logo");
        Assert.Contains("No artwork", logo.GetAttribute("aria-label"));
        Assert.Contains("0 versions", logo.GetAttribute("aria-label"));
        Assert.Equal("true", poster.GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task ASeasonHasARoleCardForItsSeasonPictures()
    {
        _workspace = Workspace(("SeasonPoster", FirstPicture, true));
        var (_, cut) = await OpenPickerAsync(SeasonContext());

        Assert.Equal(["Season poster", "Season thumbnail"], Roles(cut));
    }

    [Theory]
    [InlineData("Movies", "Poster")]
    [InlineData("TV", "Poster")]
    [InlineData("Books", "Cover")]
    [InlineData("Music", "Cover")]
    public void TheCoverRoleIsCalledPosterOrCoverByKindOfMedia(string mediaType, string label) =>
        Assert.Equal(label, Assert.Single(ArtworkPickerRules.TabsFor(["CoverArt"], mediaType)).Label);

    [Fact]
    public void SlotsThePickerDoesNotEditAreLeftOut() =>
        Assert.Equal(["Background", "Logo"], ArtworkPickerRules.TabsFor(["Logo", "Banner", "Background"], "Movies").Select(tab => tab.Slot));

    [Fact]
    public void APersonsPhotoIsARoleCalledPhoto() =>
        Assert.Equal("Photo", Assert.Single(ArtworkPickerRules.TabsFor(["Headshot"], "person")).Label);

    [Theory]
    [InlineData("CoverArt", "Movies", ArtworkRoleShape.Portrait)]
    [InlineData("Background", "Movies", ArtworkRoleShape.Landscape)]
    [InlineData("EpisodeStill", "TV", ArtworkRoleShape.Landscape)]
    [InlineData("Logo", "Movies", ArtworkRoleShape.Wide)]
    [InlineData("CoverArt", "Music", ArtworkRoleShape.Square)]
    public void EachRoleKeepsItsRealShape(string slot, string mediaType, ArtworkRoleShape shape) =>
        Assert.Equal(shape, ArtworkPickerRules.ShapeFor(slot, mediaType));

    [Theory]
    [InlineData("EpisodeStill", "TV", null, "Only this episode's still changes.")]
    [InlineData("SeasonPoster", "TV", null, "Only this season's poster changes.")]
    [InlineData("CoverArt", "TV", null, "This show's poster; seasons without their own poster use it too.")]
    [InlineData("CoverArt", "Movies", null, "Only this film's poster changes.")]
    [InlineData("CoverArt", "Music", null, "Only this album's cover changes.")]
    [InlineData("Logo", "TV", null, "Only this show's logo changes.")]
    public void TheReachLineSaysWhatTheChangeReaches(string slot, string mediaType, string? subject, string expected) =>
        Assert.Equal(expected, ArtworkPickerRules.ReachFor(slot, mediaType, subject));

    [Fact]
    public async Task ThePreviewShowsTheReachLine_AndZoomAndFullScreenChangeIt()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Contains("Only this film's poster changes.", cut.Find(".tl-art-picker__reach").TextContent);
        Assert.Equal("100%", cut.Find(".tl-art-picker__zoom").TextContent.Trim());
        cut.Find("button[aria-label='Zoom in']").Click();
        Assert.Equal("150%", cut.Find(".tl-art-picker__zoom").TextContent.Trim());
        cut.Find("button[aria-label='Zoom out']").Click();
        Assert.Equal("100%", cut.Find(".tl-art-picker__zoom").TextContent.Trim());
        Assert.Empty(cut.FindAll(".tl-art-picker__preview.is-full"));
        cut.Find("button[aria-label='Full screen']").Click();
        Assert.Single(cut.FindAll(".tl-art-picker__preview.is-full"));
    }

    [Fact]
    public async Task TheFileFactsNameTheSizeTypeAndSourceOfThePreferredVersion()
    {
        var (_, cut) = await OpenPickerAsync();

        var facts = cut.Find(".tl-art-picker__facts").TextContent;
        Assert.Contains("poster.jpg", facts);
        Assert.Contains("1000 × 1500", facts);
        Assert.Contains("JPEG", facts);
        Assert.Contains("tmdb", facts);
        Assert.Contains("MB", facts);
    }

    // -- Clicking a version makes it Preferred; Save writes ---------------------------------------------------

    [Fact]
    public async Task ClickingAVersion_MakesItPreferredAndMovesItToTheFront_AndWritesNothing()
    {
        var (harness, cut) = await OpenPickerAsync();
        Assert.Equal("CoverArt", harness.Picker.ActiveSlot);
        Assert.Equal([FirstPicture.ToString("N"), SecondPicture.ToString("N")], VersionOrder(cut));

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal([SecondPicture.ToString("N"), FirstPicture.ToString("N")], VersionOrder(cut)));
        var change = Assert.Single(harness.Session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.Artwork, change.Kind);
        Assert.Equal("CoverArt", change.Slot);
        Assert.Equal(MovieId, change.EntityId);
        Assert.Equal(SecondPicture.ToString("D"), change.TargetId);
        Assert.Equal($"/art/{SecondPicture:N}/large", harness.Preview.Url("CoverArt"));
        Assert.Empty(_writes);
        Assert.Equal("true", cut.FindAll(".tl-art-picker__version").First().GetAttribute("aria-pressed"));
        Assert.Contains("Preferred", cut.FindAll(".tl-art-picker__version").First().TextContent);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Set preferred", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheOtherVersions_KeepTheOrderTheyWereAdded()
    {
        _workspace = Workspace(("CoverArt", FirstPicture, true), ("CoverArt", SecondPicture, false), ("CoverArt", ProviderPicture, false));
        var (_, cut) = await OpenPickerAsync();
        string Id(Guid asset) => asset.ToString("N");

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();
        cut.WaitForAssertion(() => Assert.Equal([Id(ProviderPicture), Id(FirstPicture), Id(SecondPicture)], VersionOrder(cut)));

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)")[2].Click();
        cut.WaitForAssertion(() => Assert.Equal([Id(SecondPicture), Id(FirstPicture), Id(ProviderPicture)], VersionOrder(cut)));
    }

    [Fact]
    public async Task ClickingTheSavedPreferredVersionAgain_TakesTheStagedChoiceBack()
    {
        var (harness, cut) = await OpenPickerAsync();
        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();
        cut.WaitForAssertion(() => Assert.False(harness.Session.Pending.IsEmpty));

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.True(harness.Session.Pending.IsEmpty));
        Assert.Equal([FirstPicture.ToString("N"), SecondPicture.ToString("N")], VersionOrder(cut));
    }

    [Fact]
    public async Task UndoThisChoice_TakesTheStagedChangeBackOut()
    {
        var (harness, cut) = await OpenPickerAsync();
        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();
        cut.WaitForAssertion(() => Assert.False(harness.Session.Pending.IsEmpty));

        cut.Find("button.tl-art-picker__undo").Click();

        cut.WaitForAssertion(() => Assert.True(harness.Session.Pending.IsEmpty));
        Assert.Null(harness.Preview.Url("CoverArt"));
    }

    [Fact]
    public async Task ChangingRoleWithoutAddingThePick_DropsThePreviewAndStagesNothing()
    {
        var (harness, cut) = await OpenPickerAsync();
        OpenAddPanel(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        cut.Find(".tl-art-picker__tile").Click();
        cut.WaitForAssertion(() => Assert.NotNull(harness.Preview.Url("CoverArt")));

        RoleCard(cut, "Logo").Click();

        cut.WaitForAssertion(() => Assert.Null(harness.Preview.Url("CoverArt")));
        Assert.True(harness.Session.Pending.IsEmpty);
        Assert.Empty(cut.FindAll(".tl-art-picker__add-panel"));
    }

    // -- Add artwork: Provider, Upload, Link --------------------------------------------------------------------

    [Fact]
    public async Task TheAddPanel_OpensProviderUploadFromUrlAndLibrary_InPlaceOfThePreview()
    {
        var (_, cut) = await OpenPickerAsync();

        OpenAddPanel(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__add-panel")));
        Assert.Empty(cut.FindAll(".tl-art-picker__preview"));
        Assert.Equal(["Provider", "Upload", "From URL", "Library"], cut.FindAll(".tl-art-picker__sources .app-segmented-control__item").Select(item => item.TextContent.Trim()));
        cut.Find("button.tl-art-picker__back").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__preview")));
    }

    [Fact]
    public async Task AnUnmatchedShow_OffersMatchAndIdentityBesideTheProviderMessage()
    {
        _providerMessage = ProviderArtworkDiscoveryDto.UnmatchedTvShowMessage;
        _workspace = Workspace(("SeasonPoster", FirstPicture, true));
        var (harness, cut) = await OpenPickerAsync(SeasonContext());
        OpenAddPanel(cut);
        cut.WaitForAssertion(() => Assert.Contains("not matched to TheTVDB", cut.Find(".tl-art-picker__hint").TextContent));

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Open Match and Identity").Click();

        cut.WaitForAssertion(() => Assert.Equal(EditingSection.Match, harness.Mode.OpenSectionKind));
    }

    [Fact]
    public async Task AProviderTile_ShowsItsSizeAndLanguage_AndASmallMarkBelowTheRolesUsefulSize()
    {
        _providerItems =
        [
            new("big", "TMDB", "/thumb/big", 2000, 3000, false, "eng"),
            new("little", "TMDB", "/thumb/little", 600, 900, false, "eng"),
        ];
        var (_, cut) = await OpenPickerAsync();

        OpenAddPanel(cut);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".tl-art-picker__tile").Count));
        var tiles = cut.FindAll(".tl-art-picker__tile");
        Assert.Contains("2000 × 3000", tiles[0].TextContent);
        Assert.Contains("English", tiles[0].TextContent);
        Assert.Empty(tiles[0].QuerySelectorAll(".tl-art-picker__small"));
        Assert.Contains("600 × 900", tiles[1].TextContent);
        Assert.Equal("Small", tiles[1].QuerySelector(".tl-art-picker__small")!.TextContent.Trim());
    }

    [Fact]
    public async Task APickedProviderPicture_IsOnlyAddedWhenAddVersionIsPressed_ThenBecomesPreferred()
    {
        var (harness, cut) = await OpenPickerAsync();
        OpenAddPanel(cut);
        cut.WaitForAssertion(() => Assert.Contains("1000 × 1500", cut.Find(".tl-art-picker__tile").TextContent));

        cut.Find(".tl-art-picker__tile").Click();
        cut.WaitForAssertion(() => Assert.Equal("/thumb/tmdb-1", harness.Preview.Url("CoverArt")));
        Assert.Empty(_writes);
        Assert.Contains("Only this film's poster changes.", cut.Find(".tl-art-picker__selected").TextContent);

        cut.Find("button.tl-art-picker__use").Click();

        cut.WaitForAssertion(() => Assert.Equal(ProviderPicture.ToString("D"), Assert.Single(harness.Session.Pending.Changes).TargetId));
        Assert.Equal([nameof(IEngineApiClient.ImportProviderArtworkAsync)], _writes.Select(write => write.Name));
    }

    [Fact]
    public async Task AddingAProviderPictureWithoutMakingItPreferred_StagesNothing()
    {
        var (harness, cut) = await OpenPickerAsync();
        OpenAddPanel(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        cut.Find(".tl-art-picker__tile").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__selected input[type='checkbox']")));
        cut.Find(".tl-art-picker__selected input[type='checkbox']").Change(false);

        cut.Find("button.tl-art-picker__use").Click();

        cut.WaitForAssertion(() => Assert.Contains(_writes, write => write.Name == nameof(IEngineApiClient.ImportProviderArtworkAsync)));
        Assert.True(harness.Session.Pending.IsEmpty);
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

    // -- Delete is the one action that happens at once, after asking ---------------------------------------------

    [Fact]
    public async Task DeleteThisVersion_AsksFirst_ThenRemovesItAtOnce()
    {
        var host = Render<AppDialogHost>();
        var (harness, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__delete").Click();

        host.WaitForElement(".app-confirm");
        Assert.Empty(_writes);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep").Click();
        cut.WaitForAssertion(() => Assert.Empty(_writes));

        cut.Find("button.tl-art-picker__delete").Click();
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Remove").Click();

        cut.WaitForAssertion(() => Assert.Equal([nameof(IEngineApiClient.RemoveArtworkLinkAsync)], _writes.Select(write => write.Name)));
        Assert.True(harness.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task Upload_OpensTheAddPanelOnUpload_WithoutAskingTheProviders()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__upload").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__add-panel")));
        Assert.NotEmpty(cut.FindAll("input[type='file']"));
        Assert.Empty(cut.FindAll(".tl-art-picker__tile"));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task FromUrl_OpensTheAddPanelOnTheWebAddressField()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__url").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__add-panel input")));
        Assert.Contains("Picture web address", cut.Find(".tl-art-picker__add-panel").TextContent);
    }

    // -- One row of actions ---------------------------------------------------------------------------------

    [Fact]
    public async Task TheActionsAreOneRowUnderTheRoleTabs_InTheOrderUploadFromUrlLibraryPreferredRemoveRefresh()
    {
        var (_, cut) = await OpenPickerAsync();

        var row = cut.Find(".tl-art-picker__actions");
        Assert.Equal(
            ["Upload", "From URL", "Choose from Library", "Preferred", "Remove", "Refresh"],
            row.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
        // The old stacked column of buttons is gone, and the row sits after the tabs and before the preview.
        Assert.Empty(cut.FindAll(".tl-art-picker__side"));
        Assert.Empty(cut.FindAll("button.tl-art-picker__add"));
        Assert.Empty(cut.FindAll("button.tl-art-picker__replace"));
        var html = cut.Markup;
        Assert.True(html.IndexOf("tl-art-picker__levels", StringComparison.Ordinal) < html.IndexOf("tl-art-picker__actions", StringComparison.Ordinal));
        Assert.True(html.IndexOf("tl-art-picker__actions", StringComparison.Ordinal) < html.IndexOf("tl-art-picker__preview", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARoleTab_ShowsItsLabelAndCount_WithoutTheOldCardWords()
    {
        var (_, cut) = await OpenPickerAsync();

        var logo = RoleCard(cut, "Logo");
        Assert.Equal("0", logo.QuerySelector(".tl-art-picker__tab-count")!.TextContent.Trim());
        Assert.DoesNotContain("versions", logo.TextContent);
        Assert.Equal("2", RoleCard(cut, "Poster").QuerySelector(".tl-art-picker__tab-count")!.TextContent.Trim());
    }

    [Fact]
    public async Task ThePreferredButton_SaysThePictureIsPreferred_AndRemoveIsOfferedWithIt()
    {
        var (_, cut) = await OpenPickerAsync();

        var preferred = cut.Find("button.tl-art-picker__prefer");
        Assert.Equal("true", preferred.GetAttribute("aria-pressed"));
        Assert.True(preferred.HasAttribute("disabled"));
        Assert.False(cut.Find("button.tl-art-picker__delete").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ARoleWithNoArtwork_CannotBeRemoved_AndCannotBePreferred()
    {
        var (_, cut) = await OpenPickerAsync();

        RoleCard(cut, "Logo").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Find("button.tl-art-picker__delete").HasAttribute("disabled")));
        Assert.True(cut.Find("button.tl-art-picker__prefer").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Refresh_AsksTheProvidersAgain_EvenWhenTheirPicturesAreAlreadyListed()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__refresh").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        Assert.Equal(1, _providerAsked);
        cut.Find("button.tl-art-picker__back").Click();
        cut.Find("button.tl-art-picker__refresh").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _providerAsked));
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
