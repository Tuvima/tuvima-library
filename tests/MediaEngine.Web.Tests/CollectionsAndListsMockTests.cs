using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Import;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Import;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Collections and lists match their mock (spec 4b): a save is confirmed by the saved collection glowing, never a toast; problems
/// show in place; the From a list step has two columns and three figures; the list takes one input; Order and limits is one row;
/// the footer puts Cancel or Back at the left and Next at the right.
/// </summary>
public sealed class CollectionsAndListsMockTests : AsyncBunitContext
{
    private static readonly Guid FirstWork = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private readonly List<ListRefreshResultDto?> _refreshAnswers = [];
    private ListImportPreviewDto _list = new();
    private IReadOnlyList<CollectionPackCreatedDto> _packsCreated = [];
    private bool _updateOk = true;
    private Guid _createdId = Guid.NewGuid();
    private int _previewItems = 7;

    public CollectionsAndListsMockTests()
    {
        Services.AddSingleton(TestSessions.For(kids: false, administrator: true));
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new NoPersonalMedia());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionAndReturnIdAsync), _ => Task.FromResult<Guid?>(_createdId));
            stub.SetHandler(nameof(IEngineApiClient.UpdateCollectionAsync), _ => Task.FromResult(_updateOk));
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), _ => Task.FromResult(new List<CollectionItemDto>()));
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionPacksAsync), _ => Task.FromResult<IReadOnlyList<CollectionPackSummaryDto>>(
            [
                new() { Id = "decades", Name = "Decades", WouldCreateCount = 2, CreatesSet = true },
            ]));
            stub.SetHandler(nameof(IEngineApiClient.CreateCollectionPacksAsync), _ => Task.FromResult<CollectionPackCreateResponse?>(
                new CollectionPackCreateResponse { Collections = _packsCreated.ToList() }));
            stub.SetHandler(nameof(IEngineApiClient.PreviewCollectionRulesAsync), _ => Task.FromResult<CollectionPreviewResult?>(new CollectionPreviewResult
            {
                Count = _previewItems,
                Items = Enumerable.Range(1, _previewItems)
                    .Select(number => new CollectionResolvedItemViewModel { EntityId = Guid.NewGuid(), Title = $"Film {number}", MediaType = "Movies", Year = "2020" })
                    .ToList(),
                MediaTypeCounts = new Dictionary<string, int> { ["Movies"] = _previewItems },
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionListSourceAsync), _ => Task.FromResult<CollectionListSourceDto?>(new CollectionListSourceDto
            {
                CollectionId = Guid.NewGuid(),
                SourceLabel = "Letterboxd export",
                SourceName = "top.csv",
                EntryCount = 10,
                OwnedCount = 8,
                UnmatchedCount = 2,
            }));
            stub.SetHandler(nameof(IEngineApiClient.RefreshCollectionListAsync), _ => Task.FromResult(_refreshAnswers.FirstOrDefault()));
            stub.SetHandler(nameof(IEngineApiClient.RecogniseImportAsync), _ => Task.FromResult<ImportPreviewDto?>(new ImportPreviewDto
            {
                Kind = ImportKinds.CollectionList,
                KindLabel = "Collection list",
                Recognised = true,
                List = _list,
            }));
            stub.SetHandler(nameof(IEngineApiClient.PreviewCollectionListAsync), _ => Task.FromResult<ListImportPreviewDto?>(_list));
        }));
    }

    private IReadOnlyList<AppToast> Toasts => Services.GetRequiredService<AppToastService>().Toasts;

    // ── Save feedback without toasts ───────────────────────────────────────────────

    [Fact]
    public async Task CreatingACollection_RaisesNoToast_AndMarksTheSavedCollectionToGlow()
    {
        var closed = new List<bool>();
        var request = SeededRequest("Weekend picks");
        var editor = RenderEditor(request, closed.Add);

        await Next(editor);
        await Next(editor);

        editor.WaitForAssertion(() => Assert.Equal([true], closed));
        Assert.Empty(Toasts);
        Assert.Equal([_createdId], request.SavedCollectionIds);
    }

    [Fact]
    public async Task UpdatingACollection_RaisesNoToast_AndMarksTheSavedCollectionToGlow()
    {
        var closed = new List<bool>();
        var collectionId = Guid.NewGuid();
        var request = new CollectionEditorLaunchRequest { EditingCollection = RuleDrivenCollection(collectionId) };
        var editor = RenderEditor(request, closed.Add);

        await editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());

        editor.WaitForAssertion(() => Assert.Equal([true], closed));
        Assert.Empty(Toasts);
        Assert.Equal([collectionId], request.SavedCollectionIds);
    }

    [Fact]
    public async Task ASaveThatTheEngineRefuses_IsToldInPlace_AndRaisesNoToast()
    {
        var request = new CollectionEditorLaunchRequest { EditingCollection = RuleDrivenCollection(Guid.NewGuid()) };
        _updateOk = false;
        var editor = RenderEditor(request);

        await editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());

        editor.WaitForAssertion(() => Assert.NotEmpty(editor.FindAll("[data-editor-notice]")));
        Assert.Empty(Toasts);
        Assert.Empty(request.SavedCollectionIds);
    }

    [Fact]
    public async Task ASaveWithNoName_ShowsTheProblemInPlace_AndRaisesNoToast()
    {
        var editor = RenderEditor(SeededRequest(null));
        await Next(editor);
        await Next(editor);

        editor.WaitForAssertion(() => Assert.Contains("name", editor.Find("[data-editor-notice]").TextContent, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Toasts);
        Assert.Equal("3", editor.Find(".collection-flow").GetAttribute("data-flow-step"));
    }

    [Fact]
    public async Task CreatingPacks_RaisesNoToast_AndMarksEachCreatedCollectionToGlow()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _packsCreated = [new CollectionPackCreatedDto { Id = first, Name = "1990s" }, new CollectionPackCreatedDto { Id = second, Name = "2000s" }];
        var closed = new List<bool>();
        var request = new CollectionEditorLaunchRequest { InitialOwnerKind = "Library", InitialAudience = "Everyone" };
        var editor = RenderEditor(request, closed.Add);

        await editor.InvokeAsync(() => editor.FindAll(".app-choice-card").First(card => card.TextContent.Contains("Start from a pack", StringComparison.Ordinal)).Click());
        await Next(editor);
        editor.WaitForAssertion(() => Assert.Single(editor.FindAll("[data-pack='decades'] input")));
        await editor.InvokeAsync(() => editor.Find("[data-pack='decades'] input").Change(true));
        await Next(editor);
        await Next(editor);

        editor.WaitForAssertion(() => Assert.Equal([true], closed));
        Assert.Empty(Toasts);
        Assert.Equal([first, second], request.SavedCollectionIds);
    }

    [Fact]
    public async Task APackThatMakesNothing_SaysSoInPlace_AndRaisesNoToast()
    {
        _packsCreated = [];
        var closed = new List<bool>();
        var editor = RenderEditor(new CollectionEditorLaunchRequest { InitialOwnerKind = "Library", InitialAudience = "Everyone" }, closed.Add);

        await editor.InvokeAsync(() => editor.FindAll(".app-choice-card").First(card => card.TextContent.Contains("Start from a pack", StringComparison.Ordinal)).Click());
        await Next(editor);
        editor.WaitForAssertion(() => Assert.Single(editor.FindAll("[data-pack='decades'] input")));
        await editor.InvokeAsync(() => editor.Find("[data-pack='decades'] input").Change(true));
        await Next(editor);
        await Next(editor);

        editor.WaitForAssertion(() => Assert.Contains("Nothing new was created.", editor.Find("[data-editor-notice]").TextContent));
        Assert.Empty(Toasts);
        Assert.Empty(closed);
    }

    [Fact]
    public async Task AListSectionCheck_ShowsItsResultInTheSection_AndRaisesNoToast()
    {
        _refreshAnswers.Add(new ListRefreshResultDto(0, 0, 9, 1));
        var cut = Render<CollectionListSection>(parameters => parameters.Add(section => section.CollectionId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-list-section]")));

        await cut.FindAll("button").First(button => button.TextContent.Contains("Match again", StringComparison.Ordinal)).ClickAsync();

        cut.WaitForAssertion(() => Assert.Contains("you own 9, 1 still unmatched", cut.Find("[data-list-section-status]").TextContent));
        Assert.Empty(Toasts);
    }

    [Fact]
    public async Task AListSectionCheckThatFails_IsToldInTheSection_AndRaisesNoToast()
    {
        _refreshAnswers.Add(null);
        var cut = Render<CollectionListSection>(parameters => parameters.Add(section => section.CollectionId, Guid.NewGuid()));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-list-section]")));

        await cut.FindAll("button").First(button => button.TextContent.Contains("Match again", StringComparison.Ordinal)).ClickAsync();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-list-section-status]")));
        Assert.Empty(Toasts);
    }

    [Fact]
    public void ATileForAJustSavedCollection_Glows_AndOtherTilesDoNot()
    {
        var saved = Guid.NewGuid();
        var other = Guid.NewGuid();
        var savedTile = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel { Id = saved, Name = "Saved", CollectionType = "Custom" });
        var otherTile = CollectionSurfaceTileComposer.FromCollection(new CollectionManagementCatalogViewModel { Id = other, Name = "Other", CollectionType = "Custom" });
        Services.AddNativeUiServices();
        IReadOnlySet<Guid> justSaved = new HashSet<Guid> { saved };

        var cut = Render<CascadingValue<IReadOnlySet<Guid>>>(parameters => parameters
            .Add(value => value.Name, "JustSavedCollectionIds")
            .Add(value => value.Value, justSaved)
            .AddChildContent<MediaTile>(tile => tile.Add(component => component.Item, savedTile))
            .AddChildContent<MediaTile>(tile => tile.Add(component => component.Item, otherTile)));

        var glowing = cut.FindAll("article.media-tile.tl-just-changed");
        Assert.Single(glowing);
        Assert.Equal("true", glowing[0].GetAttribute("data-new"));
    }

    [Fact]
    public void TheCollectionsPage_PassesTheSavedIdsToItsTiles_AndReloadsWhenSomethingWasSaved()
    {
        var root = FindRepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Collections", "CollectionsPage.razor"));

        Assert.Contains("Name=\"JustSavedCollectionIds\"", page, StringComparison.Ordinal);
        Assert.Contains("request.SavedCollectionIds", page, StringComparison.Ordinal);
    }

    // ── The flow's footer, name step and rules row ─────────────────────────────────

    [Fact]
    public async Task TheFlowFooter_PutsCancelOrBackAtTheLeftAndNextAtTheRight_WithNoSummaryLine()
    {
        var editor = RenderEditor(new CollectionEditorLaunchRequest { InitialOwnerKind = "Library", InitialAudience = "Everyone" });

        var buttons = editor.FindAll("[data-flow-footer] .collection-editor-footer-actions button");
        Assert.Equal("Cancel", buttons.First().TextContent.Trim());
        Assert.Equal("Next", buttons.Last().TextContent.Trim());
        Assert.Empty(editor.FindAll("[data-flow-footer] .collection-editor-footer-copy"));

        await Next(editor);

        buttons = editor.FindAll("[data-flow-footer] .collection-editor-footer-actions button");
        Assert.Equal("Back", buttons.First().TextContent.Trim());
        Assert.Equal("Next", buttons.Last().TextContent.Trim());
    }

    [Fact]
    public async Task TheNameStep_HasNameDescriptionAndTheOptionBoxesOnTheLeft_AndArtworkOnTheRight()
    {
        var editor = RenderEditor(SeededRequest("Weekend picks"));
        await Next(editor);
        await Next(editor);

        var left = editor.Find("[data-name-step] > .collection-editor-panel-stack:not([data-name-artwork])");
        var right = editor.Find("[data-name-artwork]");
        Assert.NotEmpty(left.QuerySelectorAll("input[placeholder='Name this collection']"));
        Assert.NotEmpty(left.QuerySelectorAll("textarea"));
        var options = left.QuerySelector("[data-name-options]")!.TextContent;
        Assert.Contains("Show on", options);
        Assert.Contains("Who can see it", options);
        Assert.Contains("More", options);
        Assert.Contains("Artwork", right.TextContent);
        Assert.DoesNotContain("Artwork", left.TextContent);
    }

    [Fact]
    public async Task OrderAndLimits_IsOneRow_WithTheOrderTheLimitAndTheAdvancedSwitch()
    {
        var editor = RenderEditor(new CollectionEditorLaunchRequest { InitialOwnerKind = "Library", InitialAudience = "Everyone" });
        await Next(editor);

        var section = editor.Find("[data-order-and-limits]");
        var rows = section.QuerySelectorAll(".collection-rule-sentence__order-row");
        Assert.Single(rows);
        Assert.Contains("Order", rows[0].TextContent);
        Assert.Contains("Show all", rows[0].TextContent);
        Assert.NotNull(rows[0].QuerySelector("input[role='switch']"));
        Assert.Contains("Advanced", rows[0].TextContent);
        Assert.DoesNotContain("At most", section.TextContent);
        Assert.Empty(editor.FindAll(".collection-rule-sentence__advanced-description"));
        // "Then by" is part of Advanced only.
        Assert.DoesNotContain("Add then by", section.TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowAllAppearsWheneverThereAreMoreResultsThanCoversShown_EvenWithSeven()
    {
        _previewItems = 7;
        var editor = RenderEditor(new CollectionEditorLaunchRequest { InitialOwnerKind = "Library", InitialAudience = "Everyone", InitialMediaTypes = ["Movies"] });
        await Next(editor);

        editor.WaitForAssertion(() => Assert.Contains("7 titles", editor.Find(".collection-flow__preview-big").TextContent), TimeSpan.FromSeconds(5));
        Assert.Equal(6, editor.FindAll(".collection-flow__cover").Count);
        Assert.Contains("Show all 7", editor.Find(".collection-flow__show-all").TextContent);
    }

    [Fact]
    public async Task ShowAllIsNotOffered_WhenEveryResultHasACover()
    {
        _previewItems = 5;
        var editor = RenderEditor(new CollectionEditorLaunchRequest { InitialOwnerKind = "Library", InitialAudience = "Everyone", InitialMediaTypes = ["Movies"] });
        await Next(editor);

        editor.WaitForAssertion(() => Assert.Contains("5 titles", editor.Find(".collection-flow__preview-big").TextContent), TimeSpan.FromSeconds(5));
        Assert.Empty(editor.FindAll(".collection-flow__show-all"));
    }

    // ── The From a list step ───────────────────────────────────────────────────────

    [Fact]
    public void TheListStep_HasTwoColumns_ThreeFigures_AndGreyedTilesForTitlesYouDoNotOwn()
    {
        _list = SampleList();
        var draft = new ListImportDraft();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, draft));

        cut.Find("textarea").Input("https://letterboxd.com/list/top");

        cut.WaitForAssertion(() => Assert.True(draft.HasList));
        Assert.NotNull(cut.Find(".list-step__main"));
        Assert.NotNull(cut.Find(".list-step__preview"));
        var figures = cut.FindAll("[data-list-figures] .list-step__figure");
        Assert.Equal(3, figures.Count);
        Assert.Contains("you own", figures[0].TextContent);
        Assert.Equal("8", figures[0].QuerySelector(".list-step__figure-n")!.TextContent.Trim());
        Assert.Contains("not in your library", figures[1].TextContent);
        Assert.Contains("couldn't match", figures[2].TextContent);
        Assert.Equal("2", figures[2].QuerySelector(".list-step__figure-n")!.TextContent.Trim());
        Assert.Equal(2, cut.FindAll("[data-list-ghost-tile]").Count);
        Assert.Contains("Film Two", string.Concat(cut.FindAll("[data-list-ghost-tile]").Select(tile => tile.TextContent)));
        Assert.Single(cut.FindAll("[data-list-owned-tile] img"));
        Assert.Contains("8 of 10", cut.Find("[data-list-preview]").TextContent);
    }

    [Fact]
    public void TheListStep_ListsWhatCouldNotBeMatchedBeforeKeepItInStep()
    {
        _list = SampleList();
        var cut = Render<CollectionListImportStep>(parameters => parameters.Add(step => step.Draft, new ListImportDraft()));

        cut.Find("textarea").Input("https://letterboxd.com/list/top");

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-list-keep-in-step]")));
        var markup = cut.Markup;
        Assert.True(markup.IndexOf("data-list-unmatched", StringComparison.Ordinal) < markup.IndexOf("data-list-keep-in-step", StringComparison.Ordinal));
        Assert.Contains("Keep it in step", cut.Find("[data-list-keep-in-step]").TextContent);
    }

    // ── The one input ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheListPanel_IsOneField_ThatTakesAFileAPastedLinkOrPastedText_WithNoReadButton()
    {
        var cut = Render<ImportPanel>(parameters => parameters.Add(panel => panel.ShowPaste, true));

        Assert.Single(cut.FindAll("textarea"));
        Assert.Equal("Drop a file or paste a link", cut.Find("textarea").GetAttribute("placeholder"));
        Assert.Single(cut.FindAll("input[type=file]"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Contains("Read this text", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(".import-panel__paste"));
    }

    [Fact]
    public async Task PressingEnterInTheField_ReadsWhatWasTyped()
    {
        _list = SampleList();
        var raised = new List<ImportRecognition>();
        var cut = Render<ImportPanel>(parameters => parameters
            .Add(panel => panel.ShowPaste, true)
            .Add(panel => panel.OnRecognised, EventCallback.Factory.Create<ImportRecognition>(this, raised.Add)));

        cut.Find("textarea").Input("a");
        await cut.Find("textarea").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        cut.WaitForAssertion(() => Assert.Single(raised));
        Assert.Equal("a", raised[0].Text);
    }

    [Fact]
    public void ATypedCharacter_DoesNotReadYet_ButAPasteDoes()
    {
        _list = SampleList();
        var raised = new List<ImportRecognition>();
        var cut = Render<ImportPanel>(parameters => parameters
            .Add(panel => panel.ShowPaste, true)
            .Add(panel => panel.OnRecognised, EventCallback.Factory.Create<ImportRecognition>(this, raised.Add)));

        cut.Find("textarea").Input("a");
        Assert.Empty(raised);

        cut.Find("textarea").Input("https://www.themoviedb.org/list/8136");
        cut.WaitForAssertion(() => Assert.Single(raised));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    private static ListImportPreviewDto SampleList() => new()
    {
        SourceKind = "letterboxd",
        SourceLabel = "Letterboxd export",
        SourceName = "top.csv",
        SuggestedName = "Top",
        EntryCount = 10,
        OwnedCount = 8,
        UnmatchedCount = 2,
        Entries =
        [
            new CollectionListEntryDto { Position = 3, Title = "Film One", Status = ListImportChoices.StatusUnmatched },
            new CollectionListEntryDto { Position = 7, Title = "Film Two", Status = ListImportChoices.StatusUnmatched },
        ],
        Owned = [new ListImportOwnedItemDto(Guid.NewGuid(), "Film Owned", "Movies", "/covers/owned.jpg")],
        MatchedWorkIds = [Guid.NewGuid()],
    };

    private static CollectionListItemViewModel RuleDrivenCollection(Guid id) => new()
    {
        Id = id, Name = "Existing", CollectionType = "Custom", Resolution = "query", MembershipMode = "Smart",
        OwnerKind = "Library", Audience = "Everyone",
        RuleJson = """{"version":1,"groups":[{"joinWithPrevious":"and","matchMode":"all","conditions":[{"field":"genre","op":"eq","value":"Horror"}]}]}""",
    };

    private static CollectionEditorLaunchRequest SeededRequest(string? title) => new()
    {
        InitialMembershipMode = "Manual",
        InitialTitle = title,
        InitialOwnerKind = "Library",
        InitialAudience = "Everyone",
        InitialItems = [new CollectionEditorSeedItem(FirstWork, "Dune", "Movies")],
    };

    private IRenderedComponent<CollectionEditorShell> RenderEditor(CollectionEditorLaunchRequest request, Action<bool>? closed = null) =>
        Render<CollectionEditorShell>(parameters =>
        {
            parameters.Add(shell => shell.Inline, true).Add(shell => shell.Request, request);
            if (closed is not null)
            {
                parameters.Add(shell => shell.Closed, EventCallback.Factory.Create<bool>(this, closed));
            }
        });

    private static Task Next(IRenderedComponent<CollectionEditorShell> editor) =>
        editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class NoPersonalMedia : ICollectionPersonalMediaClient
    {
        public string? LastError => null;
        public Task<IReadOnlyList<CollectionGalleryReferenceDto>> GetEligibleGalleriesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionGalleryReferenceDto>>([]);
        public Task<IReadOnlyList<CollectionPersonalMediaSourceDto>> GetSourcesAsync(Guid collectionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionPersonalMediaSourceDto>>([]);
        public Task<CollectionPersonalMediaSourceDto?> AddSourceAsync(Guid collectionId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<CollectionPersonalMediaSourceDto?> UpdateSourceAsync(Guid collectionId, Guid sourceId, CollectionPersonalMediaSourceWriteRequest request, CancellationToken ct = default) => Task.FromResult<CollectionPersonalMediaSourceDto?>(null);
        public Task<bool> RemoveSourceAsync(Guid collectionId, Guid sourceId, CancellationToken ct = default) => Task.FromResult(false);
    }
}
