using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The New collection flow (Collections and lists spec 4): four ways to fill it, the sentence rule builder, and the
/// places that open it with the right media type chips ticked.
/// </summary>
public sealed class NewCollectionFlowTests : AsyncBunitContext
{
    public NewCollectionFlowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<ICollectionPersonalMediaClient>(new NoPersonalMedia());
    }

    [Theory]
    [InlineData(CollectionEditorEntryPoints.Collections, new string[0])]
    [InlineData(CollectionEditorEntryPoints.Watch, new[] { "Movies", "TV" })]
    [InlineData(CollectionEditorEntryPoints.Read, new[] { "Books", "Comics" })]
    [InlineData(CollectionEditorEntryPoints.Listen, new[] { "Music" })]
    public void EachPlaceThatOpensTheFlow_TicksItsOwnMediaTypes(string area, string[] expected)
    {
        var request = CollectionEditorEntryPoints.For(area, Guid.NewGuid());

        Assert.Equal(expected, request.InitialMediaTypes);
        Assert.Equal(area == CollectionEditorEntryPoints.Listen ? ContainerEditorKind.Playlist : ContainerEditorKind.Collection, request.Kind);
    }

    [Fact]
    public void TheFlowOpensOnTheFourWays_AndStartFromAPackIsComingSoon()
    {
        var editor = RenderFlow(CollectionEditorEntryPoints.Collections);

        var ways = editor.FindAll(".app-choice-card");
        Assert.Equal(4, ways.Count);
        Assert.Equal(["Pick titles", "Set rules", "From a list", "Start from a pack"], ways.Select(way => way.QuerySelector("strong")!.TextContent));
        Assert.Single(editor.FindAll(".app-choice-card.is-disabled"));
        Assert.All(editor.FindAll(".app-choice-card.is-disabled"), way =>
        {
            Assert.True(way.HasAttribute("disabled"));
            Assert.Contains("Coming soon", way.TextContent);
        });
    }

    [Fact]
    public void OpeningFromWatch_TicksMoviesAndTv()
    {
        var editor = RenderFlow(CollectionEditorEntryPoints.Watch);

        var ticked = editor.FindAll(".app-toggle-chip.is-on").Select(chip => chip.TextContent.Trim()).ToList();
        Assert.Equal(["Movies", "TV"], ticked);
    }

    [Fact]
    public void OpeningFromListen_MakesAPlaylistAndTicksMusic()
    {
        var editor = RenderFlow(CollectionEditorEntryPoints.Listen);

        Assert.Equal(["Music"], editor.FindAll(".app-toggle-chip.is-on").Select(chip => chip.TextContent.Trim()));
        // A playlist holds things you listen to, so only those chips are offered (plus Everything).
        Assert.DoesNotContain(editor.FindAll(".app-toggle-chip"), chip => chip.TextContent.Contains("Comics"));
    }

    [Fact]
    public void OpeningFromCollections_TicksEverything()
    {
        var editor = RenderFlow(CollectionEditorEntryPoints.Collections);

        Assert.Equal(["Everything"], editor.FindAll(".app-toggle-chip.is-on").Select(chip => chip.TextContent.Trim()));
    }

    [Fact]
    public async Task NextOnSetRules_ShowsTheSentenceBuilderWithAPlainDefinitionAndNoGroupControls()
    {
        var editor = RenderFlow(CollectionEditorEntryPoints.Collections);

        await editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());

        editor.WaitForAssertion(() => Assert.NotNull(editor.Find(".collection-flow__preview")));
        Assert.DoesNotContain("Add group", editor.Markup);
        Assert.Empty(editor.FindAll(".collection-rule-builder__group"));
    }

    [Fact]
    public async Task ThePreviewPanelShowsTheCountPerTypeAndTwelveCoversWithShowAll()
    {
        var items = Enumerable.Range(1, 14)
            .Select(number => new CollectionResolvedItemViewModel { EntityId = Guid.NewGuid(), Title = $"Film {number}", MediaType = number <= 10 ? "Movies" : "TV", Year = "2020" })
            .ToList();
        var editor = RenderFlow(CollectionEditorEntryPoints.Watch, stub => stub.SetHandler(
            nameof(IEngineApiClient.PreviewCollectionRulesAsync),
            _ => Task.FromResult<CollectionPreviewResult?>(new CollectionPreviewResult
            {
                Count = 14,
                Items = items,
                MediaTypeCounts = new Dictionary<string, int> { ["Movies"] = 10, ["TV"] = 4 },
            })));

        await editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());

        editor.WaitForAssertion(() => Assert.Contains("14 titles", editor.Find(".collection-flow__preview-big").TextContent), TimeSpan.FromSeconds(5));
        Assert.Equal(12, editor.FindAll(".collection-flow__cover").Count);
        Assert.Equal(2, editor.FindAll(".collection-flow__pill").Count);
        Assert.Contains("Show all 14", editor.Markup);
    }

    [Fact]
    public async Task TheFlowMovesFromTheFirstStepToTheSecondWithNext()
    {
        var editor = RenderFlow(CollectionEditorEntryPoints.Listen);

        Assert.Equal("1", editor.Find(".collection-flow").GetAttribute("data-flow-step"));
        await editor.InvokeAsync(() => editor.FindAll(".collection-editor-footer-actions button").Last().Click());
        Assert.Equal("2", editor.Find(".collection-flow").GetAttribute("data-flow-step"));
    }

    [Fact]
    public async Task AStoredDefinitionWithTwoGroups_OpensInAdvancedWithGroupControls()
    {
        var editor = RenderStored("""
            {"version":1,"groups":[
              {"joinWithPrevious":"and","matchMode":"all","conditions":[{"field":"genre","op":"eq","value":"Horror"}]},
              {"joinWithPrevious":"or","matchMode":"all","conditions":[{"field":"genre","op":"eq","value":"Comedy"}]}]}
            """);

        await OpenContentsAsync(editor);

        editor.WaitForAssertion(() => Assert.Contains("Add group", editor.Markup));
    }

    [Fact]
    public async Task AStoredPlainDefinition_ShowsNoGroupControls()
    {
        var editor = RenderStored("""
            {"version":1,"groups":[
              {"joinWithPrevious":"and","matchMode":"all","conditions":[{"field":"genre","op":"eq","value":"Horror"}]}]}
            """);

        await OpenContentsAsync(editor);

        editor.WaitForAssertion(() => Assert.Contains("Horror", editor.Markup));
        Assert.DoesNotContain("Add group", editor.Markup);
    }

    [Fact]
    public void TheRailPlusOnWatchAndRead_OpensTheFlowRatherThanAMenu()
    {
        var group = MediaLaneConfigurationBuilder.BuildCollectionsGroup(CollectionEditorEntryPoints.Watch);

        Assert.Equal("Collections", group.Label);
        Assert.True(group.CreateOptions!.OpensCollectionFlow);
        Assert.Equal(new CollectionFlowCreateTarget("Watch"), group.CreateOptions.SmartTarget);
    }

    private IRenderedComponent<CollectionEditorShell> RenderStored(string ruleJson)
    {
        Services.AddSingleton(EngineApiClientStub.Create(_ => { }));
        return Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Request, new CollectionEditorLaunchRequest
            {
                EditingCollection = new CollectionListItemViewModel
                {
                    Id = Guid.NewGuid(),
                    Name = "Stored",
                    CollectionType = "Smart",
                    Resolution = "query",
                    MembershipMode = "Smart",
                    OwnerKind = "Library",
                    Audience = "Everyone",
                    RuleJson = ruleJson,
                },
            }));
    }

    private static Task OpenContentsAsync(IRenderedComponent<CollectionEditorShell> editor) =>
        editor.InvokeAsync(() => editor.FindAll(".collection-editor-rail button")
            .First(button => button.TextContent.Contains("Contents", StringComparison.Ordinal)).Click());

    private IRenderedComponent<CollectionEditorShell> RenderFlow(string area, Action<EngineApiClientStub>? configure = null)
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub => configure?.Invoke(stub)));
        var entry = CollectionEditorEntryPoints.For(area, Guid.NewGuid());
        var request = new CollectionEditorLaunchRequest
        {
            ActiveProfileId = entry.ActiveProfileId,
            Kind = entry.Kind,
            InitialPrimaryArea = entry.InitialPrimaryArea,
            InitialMediaTypes = entry.InitialMediaTypes,
            InitialOwnerKind = "Library",
            InitialAudience = "Everyone",
        };
        var editor = Render<CollectionEditorShell>(parameters => parameters
            .Add(shell => shell.Inline, true)
            .Add(shell => shell.Request, request));
        return editor;
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

public sealed class CollectionRuleSentenceTests
{
    private static CollectionRulePredicateViewModel Rule(string field, string op, string? value = null) =>
        new() { Field = field, Op = op, Value = value, DisplayValue = value };

    [Fact]
    public void AnEmptyDefinition_IsPlain()
    {
        Assert.True(CollectionRuleSentence.IsPlain(new CollectionRuleDefinitionViewModel()));
    }

    [Fact]
    public void ChipsRulesAndExceptions_AreOnePlainSentence()
    {
        var definition = new CollectionRuleDefinitionViewModel();
        CollectionRuleSentence.SetMediaTypes(definition, ["Movies", "TV"]);
        CollectionRuleSentence.IncludeGroup(definition).Conditions.Add(Rule("genre", "eq", "Horror"));
        CollectionRuleSentence.EnsureExceptionsGroup(definition).Conditions.Add(Rule("genre", "neq", "Comedy"));

        Assert.True(CollectionRuleSentence.IsPlain(definition));
        Assert.Equal(["media", string.Empty, "exceptions"], definition.Groups.Select(group => group.Id == CollectionRuleSentence.MediaGroupId || group.Id == CollectionRuleSentence.ExceptionsGroupId ? group.Id : string.Empty));
        Assert.Equal(["Movies", "TV"], CollectionRuleSentence.MediaTypes(definition));
    }

    [Fact]
    public void TwoRuleGroups_NeedAdvanced()
    {
        var definition = new CollectionRuleDefinitionViewModel
        {
            Groups =
            [
                new CollectionRuleGroupViewModel { Conditions = [Rule("genre", "eq", "Horror")] },
                new CollectionRuleGroupViewModel { JoinWithPrevious = "or", Conditions = [Rule("genre", "eq", "Comedy")] },
            ],
        };

        Assert.False(CollectionRuleSentence.IsPlain(definition));
    }

    [Fact]
    public void NoChipsMeansEverything_AndRemovesTheMediaGroup()
    {
        var definition = new CollectionRuleDefinitionViewModel();
        CollectionRuleSentence.SetMediaTypes(definition, ["Books"]);
        CollectionRuleSentence.SetMediaTypes(definition, []);

        Assert.Empty(definition.Groups);
        Assert.Empty(CollectionRuleSentence.MediaTypes(definition));
    }

    [Fact]
    public void ANoneOfException_IsStoredAsOneIsNotRulePerValue_AndFoldsBack()
    {
        var definition = new CollectionRuleDefinitionViewModel();
        CollectionRuleSentence.IncludeGroup(definition).Conditions.Add(Rule("genre", "eq", "Drama"));
        var exceptions = CollectionRuleSentence.EnsureExceptionsGroup(definition);
        exceptions.Conditions.Add(new CollectionRulePredicateViewModel { Field = "genre", Op = "neq", Values = ["Horror", "Comedy"], DisplayValues = ["Horror", "Comedy"] });

        var wire = CollectionRuleSentence.ToWire(definition);
        var wireExceptions = CollectionRuleSentence.ExceptionsGroup(wire)!;
        Assert.Equal(["Horror", "Comedy"], wireExceptions.Conditions.Select(condition => condition.Value));
        Assert.All(wireExceptions.Conditions, condition => Assert.Equal("neq", condition.Op));

        var folded = CollectionRuleSentence.FromWire(wire);
        var row = Assert.Single(CollectionRuleSentence.ExceptionsGroup(folded)!.Conditions);
        Assert.Equal(["Horror", "Comedy"], row.Values!);
    }

    [Theory]
    [InlineData("eq", "neq")]
    [InlineData("contains", "not_contains")]
    [InlineData("known", "unknown")]
    [InlineData("gte", "lt")]
    public void AnExceptionRow_IsStoredAsTheOppositeRule(string shown, string stored)
    {
        var field = CollectionRuleCatalog.Instance.Fields.First(candidate => candidate.Operators.Any(op => op.Key == shown) && candidate.ValueProvider.Kind != RuleValueProviderKind.Boolean);
        var rule = Rule(field.Key, shown, "x");

        CollectionRuleSentence.ApplyDisplayedCondition(rule, field, shown);

        Assert.Equal(stored, rule.Op);
        Assert.Equal(shown == "eq" ? "eq" : shown, CollectionRuleSentence.DisplayedCondition(rule, field));
    }

    [Fact]
    public void ARuleStoredAsTrue_ShowsAsNoUnderExceptionsAndBack()
    {
        Assert.Equal("false", CollectionRuleSentence.DisplayedBoolean("true"));
        Assert.Equal("true", CollectionRuleSentence.StoredBoolean("false"));
    }
}
