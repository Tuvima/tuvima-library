using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Artwork tool of the universal editor (spec WP6, D8, D9; v7 D9): the picker's tabs for each kind of item, a picture chosen, uploaded,
/// pasted, taken from a provider or from the Library is the item's Preferred one at once through the Engine's edit session (nothing is staged
/// for Review, and there is no Undo), a picture change that fails says so on one line with Try again and the previous picture stays, and
/// "Remove artwork" asks first.
/// </summary>
public sealed class ArtworkPickerTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid FirstPicture = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SecondPicture = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid ProviderPicture = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(string Name, object?[]? Args)> _writes = [];
    private readonly List<EditSessionRequest> _requests = [];
    private Func<EditSessionRequest, LibraryBatchEditOutcome<EditSessionResult>?>? _answer;
    private TaskCompletionSource? _gate;
    private TaskCompletionSource? _uploadGate;
    private MediaEditorContextDto? _contextOverride;
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

    // The requests that wrote (the check before each save is a preview and writes nothing).
    private List<EditSessionRequest> Saves => _requests.Where(request => !request.PreviewOnly).ToList();

    // What the Engine's saved pictures read after a choice went through: the chosen version is Preferred, the others keep their order.
    private void MakeCurrent(Guid asset) =>
        _workspace = _workspace with { Variants = _workspace.Variants.Select(variant => variant with { IsPreferred = variant.ArtworkAssetId == asset }).ToList() };

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
    public async Task FetchAllStills_AppliesAStillForEveryEpisodeAtOnce_AndStagesNothing()
    {
        var context = SeasonContext();
        context.Scopes[0].ArtworkSlots = ["SeasonPoster", "EpisodeStill"];
        var harness = await StartAsync(context);
        Guid[] episodes = [Guid.NewGuid(), Guid.NewGuid()];
        harness.Picker.EpisodeSource = () => Task.FromResult<IReadOnlyList<Guid>>(episodes);

        Assert.True(harness.Picker.CanFetchAllStills);
        var staged = await harness.Picker.FetchAllStillsAsync();

        Assert.Equal(2, staged);
        var save = Assert.Single(Saves);
        Assert.Equal(2, save.Changes.Count(change => change.Kind == EditSessionChangeKinds.Artwork && change.Slot == "EpisodeStill"));
        Assert.Contains("2 episodes", harness.Picker.Message, StringComparison.Ordinal);
        Assert.True(harness.Session.Pending.IsEmpty);
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
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(_contextOverride ?? context));
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
                return StoreUploadAsync();
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
                return AnswerEditSessionAsync((EditSessionRequest)args![0]!);
            });
        });

        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, context.MediaType, context.InitialScope, null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(
            session, mode, new EditSessionSaver(api), () => [new EditTrailItem(MovieId, "Dune: Part One", "Movie")], () => Task.FromResult(true));
        Assert.True(await mode.EnterAsync());
        var preview = new ArtworkEditPreview();
        return new Harness(mode, session, flow, preview, new ArtworkPickerSession(api, session, fields, preview, ArtworkChangeSavers.For(api)));
    }

    // The upload is stored once the test lets it (a header switch or a role change can happen while it is on its way).
    private async Task<ArtworkAssetDto?> StoreUploadAsync()
    {
        if (_uploadGate is { } gate)
        {
            await gate.Task;
        }

        _workspace = Workspace(("CoverArt", FirstPicture, true), ("CoverArt", SecondPicture, false));
        return Asset(SecondPicture);
    }

    // The Engine's edit session: it answers each change as saved (and moves the Preferred picture), unless the test says otherwise.
    private async Task<LibraryBatchEditOutcome<EditSessionResult>> AnswerEditSessionAsync(EditSessionRequest request)
    {
        _requests.Add(request);
        if (_gate is { } gate)
        {
            await gate.Task;
        }

        if (_answer?.Invoke(request) is { } answer)
        {
            return answer;
        }

        if (!request.PreviewOnly)
        {
            foreach (var change in request.Changes.Where(change => change.Kind == EditSessionChangeKinds.Artwork && Guid.TryParse(change.TargetId, out _)))
            {
                MakeCurrent(Guid.Parse(change.TargetId!));
            }
        }

        return LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
        {
            PreviewOnly = request.PreviewOnly,
            Valid = true,
            DataSaved = !request.PreviewOnly,
            Changes = request.Changes
                .Select(change => new EditSessionChangeResult { Id = change.Id, Kind = change.Kind, EntityId = change.EntityId, Status = EditSessionChangeStatuses.Ok })
                .ToList(),
        });
    }

    // The Engine refuses the picture change with this reason (the check fails, so nothing is written).
    private static LibraryBatchEditOutcome<EditSessionResult> Refuse(EditSessionRequest request, string reason) =>
        LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
        {
            PreviewOnly = request.PreviewOnly,
            Valid = false,
            Changes = request.Changes
                .Select(change => new EditSessionChangeResult { Id = change.Id, Kind = change.Kind, EntityId = change.EntityId, Status = EditSessionChangeStatuses.Invalid, Reason = reason })
                .ToList(),
        });

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
    public async Task ThePicker_IsANamedRegionOfTheWindow_AndNamesItsCarousel()
    {
        var (_, cut) = await OpenPickerAsync();

        var root = cut.Find(".tl-art-picker");
        Assert.Equal("region", root.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(root.GetAttribute("aria-label")));
        var carousel = cut.Find(".tl-art-picker__carousel");
        Assert.Equal("group", carousel.GetAttribute("role"));
        Assert.False(string.IsNullOrWhiteSpace(carousel.GetAttribute("aria-label")));
        Assert.All(cut.FindAll(".tl-art-picker__version"), version => Assert.False(string.IsNullOrWhiteSpace(version.GetAttribute("aria-label"))));
        Assert.All(cut.FindAll(".tl-art-picker__role"), card => Assert.False(string.IsNullOrWhiteSpace(card.GetAttribute("aria-label"))));
    }

    [Fact]
    public async Task AMovieHasATabForBackgroundPosterAndLogo()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Equal(["Background", "Poster", "Logo"], Roles(cut));
    }

    [Fact]
    public async Task ATab_SaysWhetherAPictureIsInUse_AndShowsHowManyPicturesThereAre()
    {
        var (_, cut) = await OpenPickerAsync();

        var poster = RoleCard(cut, "Poster");
        Assert.Contains("Current picture chosen", poster.GetAttribute("aria-label"));
        Assert.Contains("2 pictures", poster.GetAttribute("aria-label"));
        var logo = RoleCard(cut, "Logo");
        Assert.Contains("No artwork", logo.GetAttribute("aria-label"));
        Assert.Contains("0 pictures", logo.GetAttribute("aria-label"));
        Assert.Equal("true", poster.GetAttribute("aria-pressed"));
    }

    [Fact]
    public async Task ASeasonHasATabForItsSeasonPictures()
    {
        _workspace = Workspace(("SeasonPoster", FirstPicture, true));
        var (_, cut) = await OpenPickerAsync(SeasonContext());

        Assert.Equal(["Season poster", "Season thumbnail"], Roles(cut));
    }

    [Fact]
    public async Task AnEpisodeHasItsOwnPickerForItsStill()
    {
        _workspace = Workspace(("EpisodeStill", FirstPicture, true), ("EpisodeStill", SecondPicture, false));
        var context = SeasonContext();
        context.Scopes[0].ArtworkSlots = ["EpisodeStill"];
        var (harness, cut) = await OpenPickerAsync(context);

        Assert.Equal(["Episode still"], Roles(cut));
        Assert.Equal("EpisodeStill", harness.Picker.ActiveSlot);
        Assert.Contains("tl-art-picker__carousel--landscape", cut.Find(".tl-art-picker__carousel").ClassList);
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
    public async Task TheLargePreview_ShowsThePictureInUseAtTheRolesRealShape_WithACurrentBadge_AndOneSourceLine()
    {
        var (_, cut) = await OpenPickerAsync();

        var preview = cut.Find(".tl-art-picker__preview");
        Assert.Contains("tl-art-picker__preview--portrait", preview.ClassList);
        Assert.NotNull(preview.QuerySelector("img"));
        Assert.Equal("Current", preview.QuerySelector(".tl-art-picker__current")!.TextContent.Trim());
        Assert.Equal("tmdb · 1000 x 1500 · 1 of 2", cut.Find(".tl-art-picker__source-line").TextContent.Trim());
        // The small-preview pieces of v7 are gone: no zoom, no View full size button, no file facts, no reach line.
        Assert.Empty(cut.FindAll(".tl-art-picker__zoom, button.tl-art-picker__fullsize, .tl-art-picker__facts, .tl-art-picker__reach"));
    }

    [Fact]
    public async Task TheCarousel_ListsEveryPictureAtItsRealShape_WithACheckOnTheOneInUse_AndNoAddTile()
    {
        var (_, cut) = await OpenPickerAsync();

        var carousel = cut.Find(".tl-art-picker__carousel");
        Assert.Contains("tl-art-picker__carousel--portrait", carousel.ClassList);
        var items = carousel.QuerySelectorAll(".tl-art-picker__item").ToList();
        Assert.Equal(2, items.Count);
        Assert.NotNull(items[0].QuerySelector(".tl-art-picker__tick"));
        Assert.Null(items[1].QuerySelector(".tl-art-picker__tick"));
        Assert.All(items, item => Assert.Equal("lazy", item.QuerySelector("img")!.GetAttribute("loading")));
        Assert.Empty(cut.FindAll(".tl-art-picker__add-version"));
    }

    [Fact]
    public async Task WithNoPictureForTheRole_TheTabSaysNoArtworkYet_WithAddArtwork()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.FindAll(".tl-art-picker__role").Single(card => card.QuerySelector("strong")!.TextContent.Trim() == "Logo").Click();

        cut.WaitForAssertion(() => Assert.Contains("No artwork yet", cut.Find(".tl-art-picker__none").TextContent));
        Assert.Empty(cut.FindAll(".tl-art-picker__preview, .tl-art-picker__carousel"));
        cut.Find(".tl-art-picker__none button.tl-art-picker__add-empty").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__add-panel")));
    }

    // -- Clicking a picture applies it at once ---------------------------------------------------------------

    [Fact]
    public async Task ClickingAPicture_AppliesItAtOnce_AndMovesItToTheFront_WithNothingLeftForReview()
    {
        var (harness, cut) = await OpenPickerAsync();
        Assert.Equal("CoverArt", harness.Picker.ActiveSlot);
        Assert.Equal([FirstPicture.ToString("N"), SecondPicture.ToString("N")], VersionOrder(cut));

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal([SecondPicture.ToString("N"), FirstPicture.ToString("N")], VersionOrder(cut)));
        var change = Assert.Single(Assert.Single(Saves).Changes);
        Assert.Equal(EditSessionChangeKinds.Artwork, change.Kind);
        Assert.Equal("CoverArt", change.Slot);
        Assert.Equal(MovieId, change.EntityId);
        Assert.Equal(SecondPicture.ToString("D"), change.TargetId);
        // Review changes lists no picture lines: nothing from the tool is staged.
        Assert.True(harness.Session.Pending.IsEmpty);
        Assert.Equal("true", cut.FindAll(".tl-art-picker__version").First().GetAttribute("aria-pressed"));
        Assert.NotNull(cut.FindAll(".tl-art-picker__item").First().QuerySelector(".tl-art-picker__tick"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Set preferred", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AChangedPicture_HasNoUndo_AndTheEarlierPictureStaysAsAVersion()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal([SecondPicture.ToString("N"), FirstPicture.ToString("N")], VersionOrder(cut)));
        Assert.Empty(cut.FindAll("button.tl-art-picker__undo"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Undo", StringComparison.OrdinalIgnoreCase));
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
    public async Task ClickingTheVersionThatIsAlreadyPreferred_AsksTheEngineForNothing()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").First().Click();

        Assert.Empty(_requests);
        Assert.Equal([FirstPicture.ToString("N"), SecondPicture.ToString("N")], VersionOrder(cut));
    }

    [Fact]
    public async Task WhileAPictureIsApplying_ThePreviewShowsABusyState_ThatGoesWhenItIsDone()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, cut) = await OpenPickerAsync();
        Assert.Empty(cut.FindAll(".tl-art-picker__preview.is-busy"));

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__preview.is-busy")));
        Assert.Equal("true", cut.Find(".tl-art-picker__preview").GetAttribute("aria-busy"));
        Assert.NotEmpty(cut.FindAll(".tl-art-picker__busy"));

        _gate.SetResult();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-art-picker__preview.is-busy")));
        Assert.Equal([SecondPicture.ToString("N"), FirstPicture.ToString("N")], VersionOrder(cut));
    }

    [Fact]
    public async Task WhenTheEngineRefusesAPicture_OneLineSaysWhatFailed_ThePreviousPictureStays_AndTryAgainChangesIt()
    {
        _answer = request => Refuse(request, "You do not have permission to change this item.");
        var (harness, cut) = await OpenPickerAsync();

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.Contains("You do not have permission", cut.Find(".tl-art-picker__failure").TextContent));
        Assert.Contains("The picture was not changed.", cut.Find(".tl-art-picker__failure").TextContent);
        Assert.Equal([FirstPicture.ToString("N"), SecondPicture.ToString("N")], VersionOrder(cut));
        Assert.Empty(Saves);
        Assert.True(harness.Session.Pending.IsEmpty);

        _answer = null;
        cut.Find("button.tl-art-picker__try-again").Click();

        cut.WaitForAssertion(() => Assert.Equal([SecondPicture.ToString("N"), FirstPicture.ToString("N")], VersionOrder(cut)));
        Assert.Empty(cut.FindAll(".tl-art-picker__failure"));
        Assert.Equal(SecondPicture.ToString("D"), Assert.Single(Assert.Single(Saves).Changes).TargetId);
    }

    [Fact]
    public async Task WhenThePersonMayNotChangePictures_OneLineSaysSo_AndThePreviousPictureStays()
    {
        _answer = _ => LibraryBatchEditOutcome<EditSessionResult>.Failed(LibraryBatchEditOutcomeStatus.Forbidden, "Only an administrator can change pictures.");
        var (_, cut) = await OpenPickerAsync();

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.Contains("Only an administrator can change pictures.", cut.Find(".tl-art-picker__failure").TextContent));
        Assert.Equal([FirstPicture.ToString("N"), SecondPicture.ToString("N")], VersionOrder(cut));
    }

    [Fact]
    public async Task APictureThatWentThrough_RaisesAppliedOnce_AndLeavesNoFailureBehind()
    {
        var (harness, cut) = await OpenPickerAsync();
        var applied = 0;
        harness.Picker.Applied += () => applied++;

        cut.FindAll(".tl-art-picker__version:not(.tl-art-picker__add-version)").Last().Click();

        cut.WaitForAssertion(() => Assert.Equal(1, applied));
        Assert.Null(harness.Picker.Failure);
        Assert.False(harness.Picker.Applying);
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
    public async Task ChoosingAProviderPicture_AddsItAndMakesItPreferredAtOnce_AndClosesTheAddPanel()
    {
        var (harness, cut) = await OpenPickerAsync();
        OpenAddPanel(cut);
        cut.WaitForAssertion(() => Assert.Contains("1000 × 1500", cut.Find(".tl-art-picker__tile").TextContent));

        cut.Find(".tl-art-picker__tile").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-art-picker__add-panel")));
        Assert.Equal(
            [nameof(IEngineApiClient.ImportProviderArtworkAsync)],
            _writes.Where(write => write.Name != nameof(IEngineApiClient.RunEditSessionAsync)).Select(write => write.Name));
        Assert.Equal(ProviderPicture.ToString("D"), Assert.Single(Assert.Single(Saves).Changes).TargetId);
        Assert.True(harness.Session.Pending.IsEmpty);
        Assert.Equal(ProviderPicture.ToString("N"), VersionOrder(cut)[0]);
    }

    [Fact]
    public async Task AnUpload_IsStoredThenMadePreferredAtOnce_AndNothingIsStaged()
    {
        var (harness, _) = await OpenPickerAsync();

        Assert.True(await harness.Picker.UploadAsync(new FakeFile("poster.png", "image/png")));

        var upload = Assert.Single(_writes, write => write.Name == nameof(IEngineApiClient.UploadCanonicalArtworkAsync));
        Assert.Contains(false, upload.Args!);
        Assert.Equal(SecondPicture.ToString("D"), Assert.Single(Assert.Single(Saves).Changes).TargetId);
        Assert.True(harness.Session.Pending.IsEmpty);
        Assert.Null(harness.Picker.Failure);
    }

    [Fact]
    public async Task AnUpload_ThatFinishesAfterTheHeaderSwitchedToAnotherItem_IsStillChosenForTheItemItWasUploadedFor()
    {
        var otherItem = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000002");
        var (harness, _) = await OpenPickerAsync();
        _uploadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var uploading = harness.Picker.UploadAsync(new FakeFile("poster.png", "image/png"));
        Assert.Contains(_writes, write => write.Name == nameof(IEngineApiClient.UploadCanonicalArtworkAsync));
        var switched = EditingModeTestData.MovieContext();
        switched.LaunchEntityId = otherItem;
        switched.Scopes[0].FieldEntityId = otherItem;
        _contextOverride = switched;
        await harness.Mode.Fields.LoadAsync(otherItem, "Movies", "movie", null);
        Assert.Equal(otherItem, harness.Mode.Fields.ArtworkEntityId);
        _uploadGate.SetResult();

        Assert.True(await uploading);

        var change = Assert.Single(Assert.Single(Saves).Changes);
        Assert.Equal(MovieId, change.EntityId);
        Assert.Equal("CoverArt", change.Slot);
        Assert.Equal(SecondPicture.ToString("D"), change.TargetId);
    }

    [Fact]
    public async Task AnUpload_ThatFinishesAfterAnotherRoleWasOpened_IsStillChosenForTheRoleItWasUploadedFor_NotLeftUnchosen()
    {
        var (harness, _) = await OpenPickerAsync();
        _uploadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var uploading = harness.Picker.UploadAsync(new FakeFile("poster.png", "image/png"));
        await harness.Picker.SelectSlotAsync("Background");
        _uploadGate.SetResult();

        Assert.True(await uploading);

        var change = Assert.Single(Assert.Single(Saves).Changes);
        Assert.Equal("CoverArt", change.Slot);
        Assert.Equal(SecondPicture.ToString("D"), change.TargetId);
        Assert.Null(harness.Picker.Failure);
    }

    [Fact]
    public async Task AnUpload_ThatTheEngineRefusesToChoose_SaysSoAndKeepsThePreviousPicture()
    {
        _answer = request => Refuse(request, "That picture is no longer available.");
        var (harness, _) = await OpenPickerAsync();

        Assert.False(await harness.Picker.UploadAsync(new FakeFile("poster.png", "image/png")));

        Assert.Contains("That picture is no longer available.", harness.Picker.Failure);
        Assert.True(harness.Picker.CanRetryChange);
        Assert.Equal(FirstPicture, harness.Picker.PreferredAssetId("CoverArt"));
        Assert.Empty(Saves);
    }

    [Fact]
    public async Task AFileThatIsNotAPicture_IsTurnedAwayWithoutAskingTheEngine()
    {
        var (harness, _) = await OpenPickerAsync();

        Assert.False(await harness.Picker.UploadAsync(new FakeFile("notes.txt", "text/plain")));

        Assert.Equal("Choose a JPEG or PNG image.", harness.Picker.Failure);
        Assert.False(harness.Picker.CanRetryChange);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task APastedLink_NeedsAWebAddress_ThenIsStoredAndMadePreferredAtOnce()
    {
        var (harness, _) = await OpenPickerAsync();

        Assert.False(await harness.Picker.PasteLinkAsync("not a link"));
        Assert.NotNull(harness.Picker.Failure);
        Assert.Empty(_writes);

        Assert.True(await harness.Picker.PasteLinkAsync("https://example.test/poster.jpg"));
        var request = Assert.IsType<ArtworkFromUrlRequest>(Assert.Single(_writes, write => write.Name == nameof(IEngineApiClient.AddArtworkFromUrlAsync)).Args![2]);
        Assert.False(request.Preferred);
        Assert.Equal("CoverArt", request.SourceAssetType);
        Assert.Equal(SecondPicture.ToString("D"), Assert.Single(Assert.Single(Saves).Changes).TargetId);
        Assert.True(harness.Session.Pending.IsEmpty);
        Assert.Null(harness.Picker.Failure);
    }

    // -- Add artwork opens its sources in place; Provider first, then Upload, From URL and Library ----------------

    [Fact]
    public async Task AddArtwork_OpensTheProviderPictures_AndUploadAndFromUrlAreOneChoiceAway()
    {
        var (_, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__add").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__tile")));
        Assert.Equal(1, _providerAsked);
        PickSource(cut, "Upload");
        Assert.NotEmpty(cut.FindAll("input[type='file']"));
        Assert.Empty(cut.FindAll(".tl-art-picker__tile"));
        Assert.Empty(_writes);
        PickSource(cut, "From URL");
        cut.WaitForAssertion(() => Assert.Contains("Picture web address", cut.Find(".tl-art-picker__add-panel").TextContent));
    }

    [Fact]
    public async Task AddArtwork_FiltersTheProviderPicturesByLanguageAndSize()
    {
        _providerItems =
        [
            new("big", "TMDB", "/thumb/big", 2000, 3000, false, "eng"),
            new("french", "TMDB", "/thumb/french", 800, 1200, false, "fra"),
        ];
        var (_, cut) = await OpenPickerAsync();

        cut.Find("button.tl-art-picker__add").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__filters")));
        Assert.Contains("Language", cut.Find(".tl-art-picker__filters").TextContent);
        Assert.Contains("Size", cut.Find(".tl-art-picker__filters").TextContent);
    }

    private static void PickSource(IRenderedComponent<EditorArtworkTool> cut, string name) =>
        cut.FindAll(".tl-art-picker__sources .app-segmented-control__item").Single(item => item.TextContent.Trim() == name).Click();

    // -- The top row: pills with counts on the left, Add artwork and Refresh on the right ---------------------------

    [Fact]
    public async Task TheTopRow_HoldsThePillsThenAddArtworkAndARefreshIconButton_AboveThePreview()
    {
        var (_, cut) = await OpenPickerAsync();

        var top = cut.Find(".tl-art-picker__top");
        Assert.Equal(["tl-art-picker__tabs", "tl-art-picker__actions"], top.Children.Select(child => child.ClassList.First()));
        var actions = cut.Find(".tl-art-picker__actions");
        Assert.Equal("Add artwork", actions.QuerySelector("button.tl-art-picker__add")!.TextContent.Trim());
        var refresh = actions.QuerySelector("button.tl-art-picker__refresh")!;
        Assert.False(string.IsNullOrWhiteSpace(refresh.GetAttribute("aria-label")));
        Assert.NotNull(actions.QuerySelector("[data-playback-tooltip='Refresh']"));
        var html = cut.Markup;
        Assert.True(html.IndexOf("tl-art-picker__top", StringComparison.Ordinal) < html.IndexOf("tl-art-picker__preview", StringComparison.Ordinal));
        Assert.True(html.IndexOf("tl-art-picker__preview", StringComparison.Ordinal) < html.IndexOf("tl-art-picker__source-line", StringComparison.Ordinal));
        Assert.True(html.IndexOf("tl-art-picker__source-line", StringComparison.Ordinal) < html.IndexOf("tl-art-picker__carousel", StringComparison.Ordinal));
        // The old buttons of the small preview are gone.
        Assert.Empty(cut.FindAll("button.tl-art-picker__upload, button.tl-art-picker__url, button.tl-art-picker__library, button.tl-art-picker__prefer, button.tl-art-picker__delete"));
    }

    [Fact]
    public async Task ARoleTab_ShowsItsLabelAndCount()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Equal("0", RoleCard(cut, "Logo").QuerySelector(".tl-art-picker__tab-count")!.TextContent.Trim());
        Assert.Equal("2", RoleCard(cut, "Poster").QuerySelector(".tl-art-picker__tab-count")!.TextContent.Trim());
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

    // -- The "..." menu of a picture: Magnify and Delete only; Delete asks once; Hide for a picture in the person's own folder ------------

    // The menu is drawn by the picture's own AppRowMenu, inside the tool's markup.
    private static IReadOnlyList<string> MenuLabels(IRenderedComponent<EditorArtworkTool> cut) =>
        cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();

    private static void OpenMenu(IRenderedComponent<EditorArtworkTool> cut, int picture) =>
        cut.FindAll(".tl-art-picker__item")[picture].QuerySelector("button.app-row-menu__trigger")!.Click();

    [Fact]
    public async Task EachPicture_HasAMenuWithOnlyMagnifyAndDelete()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Equal(2, cut.FindAll(".tl-art-picker__item button.app-row-menu__trigger").Count);
        Assert.All(cut.FindAll(".tl-art-picker__item button.app-row-menu__trigger"), trigger => Assert.False(string.IsNullOrWhiteSpace(trigger.GetAttribute("aria-label"))));
        OpenMenu(cut, 0);

        cut.WaitForAssertion(() => Assert.Equal(["Magnify", "Delete…"], MenuLabels(cut)));
    }

    [Fact]
    public async Task OpeningAPicturesMenu_ChangesNothing()
    {
        var (_, cut) = await OpenPickerAsync();

        OpenMenu(cut, 1);

        Assert.Empty(_requests);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Magnify_OpensThatPictureFullSize_AndCloseBringsTheTabBack()
    {
        var (_, cut) = await OpenPickerAsync();

        Assert.Empty(cut.FindAll(".tl-art-picker__full-viewer"));
        OpenMenu(cut, 1);
        cut.WaitForAssertion(() => Assert.Contains("Magnify", MenuLabels(cut)));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Magnify").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-art-picker__full-viewer")));
        var full = Assert.Single(cut.FindAll(".tl-art-picker__full-viewer"));
        Assert.Equal("dialog", full.GetAttribute("role"));
        Assert.Contains("app-viewer", full.ClassList);
        var image = Assert.Single(full.QuerySelectorAll("img"));
        Assert.Contains(SecondPicture.ToString("N"), image.GetAttribute("src"));
        cut.Find(".tl-art-picker__full-viewer button.app-viewer__close").Click();
        Assert.Empty(cut.FindAll(".tl-art-picker__full-viewer"));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Delete_AsksOnce_ThenRemovesTheDownloadedPictureAtOnce_AndStagesNothing()
    {
        var host = Render<AppDialogHost>();
        var (harness, cut) = await OpenPickerAsync();

        OpenMenu(cut, 1);
        cut.WaitForAssertion(() => Assert.Contains("Delete…", MenuLabels(cut)));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Delete…").Click();

        host.WaitForElement(".app-confirm");
        Assert.Contains("Delete this poster?", host.Markup);
        Assert.Empty(_writes);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep").Click();
        cut.WaitForAssertion(() => Assert.Empty(_writes));

        OpenMenu(cut, 1);
        cut.WaitForAssertion(() => Assert.Contains("Delete…", MenuLabels(cut)));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Delete…").Click();
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Delete").Click();

        cut.WaitForAssertion(() => Assert.Equal([nameof(IEngineApiClient.RemoveArtworkLinkAsync)], _writes.Select(write => write.Name)));
        Assert.True(harness.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task APictureFromThePersonsOwnFolder_OffersHideInsteadOfDelete_AndHideAsksOnceThenTakesItOffTheItem()
    {
        _workspace = _workspace with
        {
            Variants = _workspace.Variants.Select(variant => variant.ArtworkAssetId == SecondPicture ? variant with { SourceProvider = "folder" } : variant).ToList(),
        };
        var host = Render<AppDialogHost>();
        var (_, cut) = await OpenPickerAsync();

        OpenMenu(cut, 1);

        cut.WaitForAssertion(() => Assert.Equal(["Magnify", "Hide"], MenuLabels(cut)));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Hide").Click();
        host.WaitForElement(".app-confirm");
        Assert.Contains("Hide this poster?", host.Markup);
        Assert.Contains("Library never deletes its file", host.Markup);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Hide").Click();

        cut.WaitForAssertion(() => Assert.Equal([nameof(IEngineApiClient.RemoveArtworkLinkAsync)], _writes.Select(write => write.Name)));
    }

    [Theory]
    [InlineData("tmdb", false)]
    [InlineData("user_upload", false)]
    [InlineData("embedded", true)]
    [InlineData("folder", true)]
    [InlineData("local_filesystem", true)]
    [InlineData(null, true)]
    public void OnlyAPictureInThePersonsOwnFolderIsHidden_NotDeleted(string? sourceProvider, bool inOwnFolder) =>
        Assert.Equal(inOwnFolder, ArtworkPickerRules.IsInOwnFolder(sourceProvider));

    // -- What the tool shows ----------------------------------------------------------------------------

    [Fact]
    public void TheTool_ShowsThePictureTheItemHasNow_AndNothingForAChoiceThatWaits()
    {
        var preview = new ArtworkEditPreview();
        Assert.False(preview.HasCurrent("Background"));
        Assert.Null(preview.ShownUrl("Background"));

        preview.SetCurrent("Background", "/art/current/large");
        Assert.True(preview.HasCurrent("Background"));
        Assert.Equal("/art/current/large", preview.ShownUrl("Background"));

        preview.SetCurrent("Background", null);
        Assert.False(preview.HasCurrent("Background"));
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
