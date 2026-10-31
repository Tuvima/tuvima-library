using Bunit;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The rule fields added by the Collections and lists spec (4c) appear in the builder's field menu, read as sentences,
/// and each one reaches the preview with the value the person chose.
/// </summary>
public sealed class CollectionRuleFieldCatalogTests : AsyncBunitContext
{
    private static readonly string[] NewFieldKeys =
    [
        "release_date", "content_rating", "age_level", "runtime", "page_count", "track_count", "custom_tags", "themes", "mood",
        "has_variants", "available_as", "in_collection", "in_universe", "your_rating",
        "watch_state", "play_count", "last_played", "on_my_list", "favourite",
        "added_date", "library", "file_location", "quality_tier",
    ];

    public CollectionRuleFieldCatalogTests()
    {
        Services.AddNativeUiServices();
        Services.AddSingleton(System.Reflection.DispatchProxy.Create<IEngineApiClient, AccountSecurityRenderTests.EmptyEngineApiClient>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void EveryNewField_IsInTheCatalogUnderAKnownCategoryWithConditions()
    {
        var catalog = CollectionRuleCatalog.Instance;

        Assert.Equal(catalog.Fields.Count, catalog.Fields.Select(field => field.Key).Distinct().Count());
        Assert.All(NewFieldKeys, key =>
        {
            var field = Assert.Single(catalog.Fields, candidate => candidate.Key == key);
            Assert.Contains(catalog.Categories, category => category.Key == field.Category);
            Assert.NotEmpty(field.Operators);
            Assert.False(string.IsNullOrWhiteSpace(field.Label));
        });
    }

    [Fact]
    public void ActivityFields_LiveUnderYourActivity()
    {
        var catalog = CollectionRuleCatalog.Instance;

        Assert.Contains(catalog.Categories, category => category.Key == "activity" && category.Label == "Your activity");
        Assert.All(new[] { "watch_state", "play_count", "last_played", "on_my_list", "favourite" }, key =>
            Assert.Equal("activity", catalog.Field(key).Category));
    }

    [Fact]
    public void Conditions_ReadAsSentences()
    {
        var labels = CollectionRuleCatalog.Instance.Fields
            .SelectMany(field => field.Operators)
            .Select(op => op.Label)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(
            new[] { "is", "is not", "is any of", "contains", "is before", "is after", "is between", "at least", "at most", "is known", "is unknown" },
            label => Assert.Contains(label, labels));
    }

    [Fact]
    public void EveryNewField_ReachesThePreviewWithItsChosenValue()
    {
        foreach (var key in NewFieldKeys)
        {
            var field = CollectionRuleCatalog.Instance.Field(key);
            var op = field.Operators.First(candidate => candidate.Key is not ("known" or "unknown" or "between"));
            var condition = new CollectionRulePredicateViewModel { Field = key, Op = op.Key, Value = SampleValue(field, op.Key) };
            RuleBuilderPreviewRequest? seen = null;

            Render<AppPopoverHost>();
            var cut = Render<SharedRuleBuilder>(parameters => parameters
                .Add(component => component.Catalog, CollectionRuleCatalog.Instance)
                .Add(component => component.Definition, new CollectionRuleDefinitionViewModel
                {
                    Groups = [new CollectionRuleGroupViewModel { Conditions = [condition] }],
                })
                .Add(component => component.SortField, "title")
                .Add(component => component.PreviewLoader, (request, _) =>
                {
                    seen = request;
                    return Task.FromResult<CollectionPreviewResult?>(new CollectionPreviewResult { Count = 1 });
                }));

            cut.WaitForAssertion(() => Assert.True(seen is not null, $"The preview never ran for the {key} field."));
            var sent = Assert.Single(seen!.Definition.AllConditions);
            Assert.Equal(key, sent.Field);
            Assert.Equal(op.Key, sent.Op);
            Assert.Equal(condition.Value, sent.Value);
        }
    }

    [Fact]
    public void BetweenCondition_AsksForBothEndsAndSendsThemTogether()
    {
        var condition = new CollectionRulePredicateViewModel { Field = "release_date", Op = "between", Values = ["2020-01-01", "2024-12-31"] };
        RuleBuilderPreviewRequest? seen = null;

        var cut = Render<SharedRuleBuilder>(parameters => parameters
            .Add(component => component.Catalog, CollectionRuleCatalog.Instance)
            .Add(component => component.Definition, new CollectionRuleDefinitionViewModel
            {
                Groups = [new CollectionRuleGroupViewModel { Conditions = [condition] }],
            })
            .Add(component => component.SortField, "title")
            .Add(component => component.PreviewLoader, (request, _) =>
            {
                seen = request;
                return Task.FromResult<CollectionPreviewResult?>(new CollectionPreviewResult { Count = 2 });
            }));

        cut.WaitForAssertion(() => Assert.NotNull(seen));
        Assert.Equal(2, cut.FindAll(".collection-rule-builder__range input[type=date]").Count);
        Assert.Equal(["2020-01-01", "2024-12-31"], Assert.IsType<string[]>(seen!.Definition.AllConditions.Single().Values));
    }

    [Fact]
    public void BetweenCondition_WithOnlyOneEnd_IsNotYetSentToThePreview()
    {
        var condition = new CollectionRulePredicateViewModel { Field = "runtime", Op = "between", Values = ["90", ""] };
        var runs = 0;

        var cut = Render<SharedRuleBuilder>(parameters => parameters
            .Add(component => component.Catalog, CollectionRuleCatalog.Instance)
            .Add(component => component.Definition, new CollectionRuleDefinitionViewModel
            {
                Groups = [new CollectionRuleGroupViewModel { Conditions = [condition] }],
            })
            .Add(component => component.SortField, "title")
            .Add(component => component.PreviewLoader, (_, _) =>
            {
                runs++;
                return Task.FromResult<CollectionPreviewResult?>(null);
            }));

        Assert.Equal(2, cut.FindAll(".collection-rule-builder__range input").Count);
        Assert.Equal(0, runs);
    }

    private static string SampleValue(RuleFieldDefinition field, string op) =>
        RuleOperators.TakesWholeNumber(op) ? "7"
        : field.ValueProvider.Kind switch
        {
            RuleValueProviderKind.Boolean => "true",
            RuleValueProviderKind.StaticOptions => field.ValueProvider.Options!.First().Value,
            RuleValueProviderKind.Date => "2026-01-01",
            _ => "5",
        };
}
