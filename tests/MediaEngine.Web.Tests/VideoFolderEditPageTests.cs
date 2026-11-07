using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Editing a folder of videos (universal editor v5, WP1, bug 21, acceptance A2): Edit on a video opens the editor from the video's own page,
/// and the item dropdown moves between the sections and videos of the folder, every one ready under its own work id. The Engine is a
/// hand-written stub that answers like the real one now does (Series, Section and Video scopes); nothing is written anywhere.
/// </summary>
public sealed class VideoFolderEditPageTests : AsyncBunitContext
{
    private static readonly Guid SeriesId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid SectionId = Guid.Parse("c0000000-0000-0000-0000-000000000011");
    private static readonly Guid WarmUpId = Guid.Parse("c0000000-0000-0000-0000-000000000101");
    private static readonly Guid DribblingId = Guid.Parse("c0000000-0000-0000-0000-000000000102");

    private const string SeriesTitle = "Steph Curry MasterClass";
    private const string SectionTitle = "Ball Handling";

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _writes = [];
    private readonly IRenderedComponent<EditorPopover> _editor;

    public VideoFolderEditPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args =>
                Task.FromResult<MediaEditorContextDto?>(ContextFor((Guid)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorNavigatorAsync), args =>
                Task.FromResult(Navigator((Guid)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorOwnedChildrenAsync), args =>
                Task.FromResult(OwnedChildren((Guid)args![0]!)));
            stub.SetHandler(nameof(IEngineApiClient.GetItemExplicitAsync), _ => Task.FromResult<MediaEngine.Contracts.Items.ItemExplicitResponse?>(null));
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<LibraryItemHistoryDto>(), (string?)null)));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            foreach (var write in new[] { nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), nameof(IEngineApiClient.RunEditSessionAsync) })
            {
                var name = write;
                stub.SetHandler(name, args =>
                {
                    _writes.Add(name + (args is [EditSessionRequest { PreviewOnly: true }, ..] ? ":check" : string.Empty));
                    return Task.FromResult<object?>(null);
                });
            }
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Render<AppPopoverHost>();
        Render<AppDialogHost>();
        _editor = Render<EditorPopover>();
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    // -- What the Engine answers for a generated folder: a series, one section, two videos ----------------------------------------

    private static MediaEditorContextDto? ContextFor(Guid launch)
    {
        var scopes = new List<MediaEditorScopeDto>();
        string initial;
        if (launch == SeriesId)
        {
            scopes.Add(Scope("series", "Series", SeriesId, SeriesTitle, 0));
            initial = "series";
        }
        else if (launch == SectionId)
        {
            scopes.Add(Scope("series", "Series", SeriesId, SeriesTitle, 0));
            scopes.Add(Scope("section", "Section", SectionId, SectionTitle, 1));
            initial = "section";
        }
        else if (launch == WarmUpId || launch == DribblingId)
        {
            scopes.Add(Scope("series", "Series", SeriesId, SeriesTitle, 0));
            scopes.Add(Scope("section", "Section", SectionId, "Section", 1));
            scopes.Add(Scope("video", "Video", launch, launch == WarmUpId ? "Warm Up" : "Dribbling", 2));
            initial = "video";
        }
        else
        {
            return null;
        }

        return new MediaEditorContextDto
        {
            LaunchEntityId = launch,
            MediaType = "Videos",
            InitialScope = initial,
            AvailableTabs = ["details", "artwork", "links", "history"],
            Scopes = scopes,
        };
    }

    private static MediaEditorScopeDto Scope(string scopeId, string label, Guid entityId, string title, int order) => new()
    {
        ScopeId = scopeId,
        Label = label,
        Order = order,
        FieldEntityId = entityId,
        DisplayTitle = title,
        CanEditFields = true,
        CanEditArtwork = true,
        FieldSnapshot = new MediaEditorScopeFieldSnapshotDto
        {
            CanonicalFields = [new MediaEditorScopedFieldValueDto { Key = "title", Value = title }],
            AllowedOverrideKeys = ["title", "description", "genre", "custom_tags"],
        },
    };

    private static MediaEditorNavigatorDto? Navigator(Guid id)
    {
        if (id != SeriesId && id != SectionId && id != WarmUpId && id != DribblingId)
        {
            return null;
        }

        return new MediaEditorNavigatorDto
        {
            Enabled = true,
            MediaType = "Videos",
            ContainerEntityId = SeriesId,
            SelectedEntityId = id,
            Nodes =
            [
                new MediaEditorNavigatorNodeDto { NodeId = SeriesId, EntityId = SeriesId, NodeKind = "work", Title = SeriesTitle, Depth = 0, IsRoot = true, IsLeaf = false, IsOwned = true },
                new MediaEditorNavigatorNodeDto { NodeId = SectionId, EntityId = SectionId, ParentNodeId = SeriesId, NodeKind = "work", Title = SectionTitle, Depth = 1, IsLeaf = false, IsOwned = true },
            ],
        };
    }

    private static MediaEditorOwnedChildSearchDto OwnedChildren(Guid parent)
    {
        var items = parent == SectionId
            ? new List<MediaEditorOwnedChildDto> { File(WarmUpId, "Warm Up", "1"), File(DribblingId, "Dribbling", "2") }
            : [];
        return new MediaEditorOwnedChildSearchDto { ParentEntityId = parent, Page = 1, PageSize = 50, TotalCount = items.Count, Items = items };
    }

    private static MediaEditorOwnedChildDto File(Guid workId, string title, string number) => new()
    {
        WorkId = workId,
        AssetId = Guid.NewGuid(),
        Title = title,
        MatchedNumber = number,
        SourceFileName = title + ".mkv",
        SourceFilePath = "/videos/course/" + title + ".mkv",
        MatchState = "matched",
    };

    // -- The pages the Dashboard draws for a folder of videos ----------------------------------------------------------------------

    private static SequenceItemViewModel Video(Guid id, string title, bool current = false) => new()
    {
        Id = id.ToString("D"),
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        GroupKey = "season-1",
        IsOwned = true,
        IsCurrent = current,
    };

    private static SequencePlacementViewModel Placement(Guid? current) => new()
    {
        ContainerId = SeriesId.ToString("D"),
        ContainerTitle = SeriesTitle,
        ContainerLabel = "Folder",
        ItemLabel = "Video",
        Groups =
        [
            new SequenceGroupViewModel
            {
                Key = "season-1",
                Title = SectionTitle,
                EntityId = SectionId.ToString("D"),
                Items = [Video(WarmUpId, "Warm Up", current == WarmUpId), Video(DribblingId, "Dribbling", current == DribblingId)],
            },
        ],
    };

    private static DetailPageViewModel VideoPage(Guid id, string title) => new()
    {
        Id = id.ToString("D"),
        EntityType = DetailEntityType.TvEpisode,
        Title = title,
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        EditorTarget = new DetailEditorTarget { EntityId = id.ToString("D"), EntityKind = "Work" },
        SequencePlacement = Placement(id),
    };

    private static DetailPageViewModel FolderPage() => new()
    {
        Id = SeriesId.ToString("D"),
        EntityType = DetailEntityType.TvShow,
        Title = SeriesTitle,
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions = [new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" }],
        EditorTarget = new DetailEditorTarget { EntityId = SeriesId.ToString("D"), EntityKind = "Work" },
        SequencePlacement = Placement(null),
    };

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync(DetailPageViewModel model)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = Render<DetailPage>(parameters => parameters.Add(page => page.Model, model));
        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-editor-window__tabs [role='tab']")));
        return cut;
    }

    private EditSessionFlow Flow() => Services.GetRequiredService<EditorHost>().Flow;

    private void Choose(string name, string level)
    {
        _editor.WaitForAssertion(() => Assert.Contains(_editor.FindAll(".tl-rail__crumb"), crumb => LevelOf(crumb) == level));
        _editor.FindAll(".tl-rail__crumb").First(candidate => LevelOf(candidate) == level).Click();
        _editor.WaitForAssertion(() => Assert.Contains(_editor.FindAll(".tl-rail__option"), option => OptionName(option) == name));
        _editor.FindAll(".tl-rail__option").First(option => OptionName(option) == name).Click();
    }

    private static string LevelOf(AngleSharp.Dom.IElement crumb) => (crumb.GetAttribute("data-level") ?? string.Empty).Split(' ')[0];

    private static string OptionName(AngleSharp.Dom.IElement option) =>
        option.QuerySelector(".tl-rail__text > span")!.TextContent.Trim().Split(" · ").Last();

    // -- A2: a video of a Videos folder opens from its page and from the dropdown -------------------------------------------------

    [Fact]
    public async Task EditOnAVideoPage_OpensTheEditorReadyOnThatVideo()
    {
        await StartEditingAsync(VideoPage(WarmUpId, "Warm Up"));
        var flow = Flow();

        Assert.True(flow.Session.IsActive);
        Assert.Equal(WarmUpId, flow.Session.ActiveItem!.EntityId);
        var fields = flow.Mode().Fields;
        Assert.True(fields.Loaded);
        Assert.Equal(WarmUpId, fields.EntityId);
        Assert.Equal("video", fields.ScopeId);
        Assert.Equal("Warm Up", _editor.Find(".tl-editor-header__title").TextContent.Trim());
    }

    [Fact]
    public async Task FromAVideoPage_TheItemDropdownOpensTheOtherVideo_ReadyUnderItsOwnId()
    {
        await StartEditingAsync(VideoPage(WarmUpId, "Warm Up"));
        var flow = Flow();

        Choose("Dribbling", "Video");

        _editor.WaitForAssertion(() => Assert.Equal(DribblingId, flow.Session.ActiveItem!.EntityId));
        var fields = flow.Mode().Fields;
        Assert.True(fields.Loaded);
        Assert.Equal(DribblingId, fields.EntityId);
        Assert.Equal("video", fields.ScopeId);
        Assert.Null(flow.Message);
    }

    [Fact]
    public async Task FromTheFoldersPage_TheDropdownsOpenTheSectionAndThenAVideo_BothReadyUnderTheirOwnIds()
    {
        await StartEditingAsync(FolderPage());
        var flow = Flow();
        Assert.Equal(SeriesId, flow.Mode().Fields.EntityId);
        Assert.Equal("series", flow.Mode().Fields.ScopeId);

        Choose(SectionTitle, "Section");
        _editor.WaitForAssertion(() => Assert.Equal(SectionId, flow.Session.ActiveItem!.EntityId));
        Assert.Equal(SectionId, flow.Mode().Fields.EntityId);
        Assert.Equal("section", flow.Mode().Fields.ScopeId);

        Choose("Warm Up", "Video");
        _editor.WaitForAssertion(() => Assert.Equal(WarmUpId, flow.Session.ActiveItem!.EntityId));
        Assert.Equal(WarmUpId, flow.Mode().Fields.EntityId);
        Assert.Equal("video", flow.Mode().Fields.ScopeId);
        Assert.Null(flow.Message);
        Assert.DoesNotContain(_writes, write => !write.EndsWith(":check", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVideosEditor_NamesItsLevelsSectionAndVideo_NeverSeasonOrEpisode()
    {
        await StartEditingAsync(VideoPage(WarmUpId, "Warm Up"));

        _editor.WaitForAssertion(() => Assert.Equal(["Folder", "Section", "Video"], _editor.FindAll(".tl-rail__crumb[data-level]").Select(LevelOf)));
        var text = _editor.Markup;
        Assert.DoesNotContain("Season", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Episode", text, StringComparison.Ordinal);
    }
}
