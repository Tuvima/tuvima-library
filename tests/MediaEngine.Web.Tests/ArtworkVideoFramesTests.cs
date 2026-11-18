using Bunit;
using MediaEngine.Contracts.Artwork;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Frames from the video" in the Artwork tab (review mock-ups 2026-11-18, D2): a folder of videos, a video and an episode show a strip of
/// frames taken from the video under the picture, the frame in use is outlined, a default cover made from a frame says "From video frame",
/// and choosing a frame applies at once (no Undo, nothing staged for Review).
/// </summary>
public sealed class ArtworkVideoFramesTests : AsyncBunitContext
{
    private static readonly Guid VideoId = Guid.Parse("44444444-aaaa-bbbb-cccc-000000000004");
    private static readonly Guid EpisodeId = Guid.Parse("22222222-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid DefaultPicture = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ChosenPicture = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private static readonly VideoFramesDto Strip = new(
    [
        new VideoFrameDto("0", 10, "/engine-image/metadata/44444444-aaaa-bbbb-cccc-000000000004/video-frames/video/0", false, "video-frame:10000"),
        new VideoFrameDto("1", 20, "/engine-image/metadata/44444444-aaaa-bbbb-cccc-000000000004/video-frames/video/1", true, "video-frame:20000"),
        new VideoFrameDto("2", 3725, "/engine-image/metadata/44444444-aaaa-bbbb-cccc-000000000004/video-frames/video/2", false, "video-frame:3725000"),
    ]);

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<(Guid Entity, string Scope, string Frame, string Slot)> _used = [];
    private readonly List<EditSessionRequest> _saves = [];
    private IEngineApiClient _api = default!;
    private ArtworkEntityWorkspaceDto _workspace = default!;
    private VideoFramesDto _frames = Strip;
    private MediaEditorContextDto _context = default!;

    public ArtworkVideoFramesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        _api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(_context));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetEntityArtworkAsync), _ => Task.FromResult<ArtworkEntityWorkspaceDto?>(_workspace));
            stub.SetHandler(nameof(IEngineApiClient.GetVideoFramesAsync), _ => Task.FromResult<VideoFramesDto?>(_frames));
            stub.SetHandler(nameof(IEngineApiClient.UseVideoFrameAsync), args =>
            {
                _used.Add(((Guid)args![0]!, (string)args[1]!, (string)args[2]!, (string)args[3]!));
                return Task.FromResult<ArtworkAssetDto?>(
                    new ArtworkAssetDto(ChosenPicture, $"/art/{ChosenPicture:N}/large", $"/art/{ChosenPicture:N}/small", 640, 360, "Landscape", "generated", "video-frame:3725000", []));
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                if (!request.PreviewOnly)
                {
                    _saves.Add(request);
                }

                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
                {
                    PreviewOnly = request.PreviewOnly,
                    Valid = true,
                    DataSaved = !request.PreviewOnly,
                    Changes = request.Changes
                        .Select(change => new EditSessionChangeResult { Id = change.Id, Kind = change.Kind, EntityId = change.EntityId, Status = EditSessionChangeStatuses.Ok })
                        .ToList(),
                }));
            });
        });
        Services.AddSingleton(_api);
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
    }

    // The default cover: a frame the Engine made from the video, in use (so the strip outlines the frame it came from).
    private static ArtworkEntityWorkspaceDto DefaultCoverWorkspace(Guid entityId, string slot, string sourceKey) =>
        new(entityId, "Work",
        [
            new ArtworkEntityVariantDto(
                Guid.NewGuid(), DefaultPicture, ArtworkPickerRules.RoleFor(slot)!, null, slot, true, false,
                $"/art/{DefaultPicture:N}/large", $"/art/{DefaultPicture:N}/small", 640, 360, "Landscape", "generated", sourceKey),
        ]);

    private static MediaEditorContextDto VideoContext(Guid id, string mediaType, string scope, params string[] slots) => new()
    {
        LaunchEntityId = id,
        MediaType = mediaType,
        InitialScope = scope,
        AvailableTabs = ["details", "artwork"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = scope,
                Label = scope,
                FieldEntityId = id,
                DisplayTitle = "Lesson",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = [.. slots],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
            },
        ],
    };

    private async Task<(EditSessionState Session, IRenderedComponent<EditorArtworkTool> Cut)> OpenAsync(
        Guid id, string mediaType, string scope, string openSlot, string[] slots, string defaultSourceKey = "video-frame:20000")
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        _context = VideoContext(id, mediaType, scope, slots);
        _workspace = DefaultCoverWorkspace(id, openSlot, defaultSourceKey);
        var fields = new ItemFieldEditSession(_api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(id, mediaType, scope, null);
        var session = new EditSessionState(new PendingEditList());
        var flow = new EditSessionFlow(session, mode, new EditSessionSaver(_api), () => [new EditTrailItem(id, "Lesson", "Video")], () => Task.FromResult(true));
        Assert.True(await mode.EnterAsync());
        var picker = new ArtworkPickerSession(_api, session, fields, new ArtworkEditPreview(), ArtworkChangeSavers.For(_api));
        await flow.SelectToolAsync(EditTool.Artwork);
        var cut = Render<EditorArtworkTool>(parameters => parameters
            .Add(tool => tool.Mode, mode)
            .Add(tool => tool.Picker, picker));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-art-picker__version")));
        return (session, cut);
    }

    private Task<(EditSessionState Session, IRenderedComponent<EditorArtworkTool> Cut)> OpenVideoAsync() =>
        OpenAsync(VideoId, "Videos", "video", "CoverArt", ["CoverArt", "Background", "Logo"]);

    [Fact]
    public async Task AVideosCoverTab_ShowsAStripOfFramesFromTheVideo_WithTheOneInUseOutlined()
    {
        var (_, cut) = await OpenVideoAsync();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));
        Assert.Contains("Frames from the video", cut.Find(".tl-art-picker__frames").TextContent, StringComparison.Ordinal);
        var current = Assert.Single(cut.FindAll(".tl-art-picker__face--frame.is-current"));
        Assert.Equal("/engine-image/metadata/44444444-aaaa-bbbb-cccc-000000000004/video-frames/video/1", current.QuerySelector("img")!.GetAttribute("src"));
        Assert.Contains("Best", cut.FindAll(".tl-art-picker__frame")[1].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Best", cut.FindAll(".tl-art-picker__frame")[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultCoverMadeFromAFrame_IsLabelledFromVideoFrame()
    {
        var (_, cut) = await OpenVideoAsync();

        Assert.Contains("From video frame", cut.Find(".tl-art-picker__source-line").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFramesTime_IsShownAsMinutesAndSeconds_OrWithTheHourWhenLong()
    {
        var (_, cut) = await OpenVideoAsync();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame-time").Count));
        Assert.Equal(["0:10", "0:20", "1:02:05"], cut.FindAll(".tl-art-picker__frame-time").Select(time => time.TextContent.Trim()).ToArray());
    }

    [Fact]
    public async Task ChoosingAFrame_SavesItAsAPicture_AndAppliesItAtOnce_WithNothingStagedForReview()
    {
        var (session, cut) = await OpenVideoAsync();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));

        cut.FindAll("button.tl-art-picker__frame")[2].Click();

        cut.WaitForAssertion(() => Assert.Single(_saves));
        var used = Assert.Single(_used);
        Assert.Equal((VideoId, "video", "2", "CoverArt"), used);
        var change = Assert.Single(Assert.Single(_saves).Changes);
        Assert.Equal(EditSessionChangeKinds.Artwork, change.Kind);
        Assert.Equal("CoverArt", change.Slot);
        Assert.Equal(ChosenPicture.ToString("D"), change.TargetId);
        Assert.True(session.Pending.IsEmpty);
    }

    [Fact]
    public async Task ChoosingTheFrameAlreadyInUse_ChangesNothing()
    {
        var (_, cut) = await OpenVideoAsync();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));

        cut.FindAll("button.tl-art-picker__frame")[1].Click();

        Assert.Empty(_used);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task TheLogoTab_HasNoFrameStrip()
    {
        var (_, cut) = await OpenVideoAsync();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));

        cut.FindAll(".tl-art-picker__role").Single(tab => tab.TextContent.Contains("Logo", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-art-picker__frames")));
    }

    [Fact]
    public async Task TheBackgroundTab_HasTheSameFrameStrip_WithoutAskingTheEngineAgain()
    {
        var (_, cut) = await OpenVideoAsync();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));
        _workspace = DefaultCoverWorkspace(VideoId, "Background", "video-frame:20000");

        cut.FindAll(".tl-art-picker__role").Single(tab => tab.TextContent.Contains("Background", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));
        Assert.Equal(1, ((EngineApiClientStub)(object)_api).Calls.Count(call => call == nameof(IEngineApiClient.GetVideoFramesAsync)));
    }

    [Fact]
    public async Task AnEpisodesStill_HasTheFrameStrip()
    {
        var (_, cut) = await OpenAsync(EpisodeId, "TV", "episode", "EpisodeStill", ["EpisodeStill"]);

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".tl-art-picker__frame").Count));
    }

    [Fact]
    public async Task AFilm_NeverAsksTheEngineForFrames()
    {
        var (_, cut) = await OpenAsync(VideoId, "Movies", "item", "CoverArt", ["CoverArt", "Background", "Logo"]);

        Assert.Empty(cut.FindAll(".tl-art-picker__frames"));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetVideoFramesAsync), ((EngineApiClientStub)(object)_api).Calls);
    }

    [Fact]
    public async Task AVideoWithNoUsableFrames_SaysWhyUnderThePicture()
    {
        _frames = new VideoFramesDto([], "No frames could be taken from this video.");

        var (_, cut) = await OpenVideoAsync();

        cut.WaitForAssertion(() => Assert.Contains("No frames could be taken from this video.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".tl-art-picker__frame"));
    }

    [Fact]
    public async Task TheAddArtworkButton_StaysForAVideo_SoAFileOrAnOnlinePictureCanBeChosenToo()
    {
        var (_, cut) = await OpenVideoAsync();

        Assert.NotEmpty(cut.FindAll("button.tl-art-picker__add"));
    }
}
