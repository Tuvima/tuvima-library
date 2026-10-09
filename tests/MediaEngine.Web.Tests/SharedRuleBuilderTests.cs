using Bunit;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SharedRuleBuilderTests : AsyncBunitContext
{
    public SharedRuleBuilderTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void CollectionCatalog_RendersCollectionFieldsThroughSharedCore()
    {
        var cut = RenderBuilder(CollectionRuleCatalog.Instance);

        Assert.Equal("collection", cut.Find("[data-rule-domain]").GetAttribute("data-rule-domain"));
        Assert.Contains("Smart membership", cut.Markup);
        OpenConditionPicker(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Media type", cut.Markup);
            Assert.Contains("Genre", cut.Markup);
        });
        Assert.Contains("Release year", CollectionRuleCatalog.Instance.SortFields.Select(option => option.Label));
        Assert.Contains(CollectionRuleCatalog.Instance.Fields, field =>
            field.Key == "person_qid" && field.ValueProvider.Kind == RuleValueProviderKind.CollectionLibrary);
    }

    [Fact]
    public void ViewCatalog_RendersPersonalMediaFieldsAndTruthfulCapabilitiesThroughSharedCore()
    {
        var cut = RenderBuilder(ViewRuleCatalog.Instance);

        Assert.Equal("view", cut.Find("[data-rule-domain]").GetAttribute("data-rule-domain"));
        Assert.Contains("Smart Gallery membership", cut.Markup);
        OpenConditionPicker(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("File type", cut.Markup);
            Assert.Contains("Orientation", cut.Markup);
            Assert.Contains("Duration", cut.Markup);
        });

        var expectedFields = new[]
        {
            "people", "place", "captured_date", "media_type", "device", "tags", "favorite",
            "orientation", "duration", "file_type", "owner", "source",
        };
        Assert.All(expectedFields, key => Assert.Contains(ViewRuleCatalog.Instance.Fields, field => field.Key == key));
        Assert.False(ViewRuleCatalog.Instance.Capabilities.FaceRecognition);
        Assert.False(ViewRuleCatalog.Instance.Capabilities.SemanticSearch);
        Assert.False(ViewRuleCatalog.Instance.Capabilities.Ocr);
        Assert.DoesNotContain(ViewRuleCatalog.Instance.Fields, field =>
            field.RequiredCapability is "face-recognition" or "semantic-search" or "ocr");
    }

    [Fact]
    public void RuleBuilderWrappers_ConfigureOneSharedInteractiveImplementation()
    {
        var rulesDirectory = GetRepoFile("src", "MediaEngine.Web", "Components", "Rules");
        var collectionWrapper = File.ReadAllText(GetRepoFile("src", "MediaEngine.Web", "Components", "Collections", "CollectionRuleBuilder.razor"));
        var viewWrapper = File.ReadAllText(Path.Combine(rulesDirectory, "ViewRuleBuilder.razor"));
        var sharedBuilder = File.ReadAllText(Path.Combine(rulesDirectory, "SharedRuleBuilder.razor"));

        Assert.Contains("<SharedRuleBuilder", collectionWrapper, StringComparison.Ordinal);
        Assert.Contains("<SharedRuleBuilder", viewWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("collection-rule-builder__group", collectionWrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("collection-rule-builder__group", viewWrapper, StringComparison.Ordinal);
        Assert.Equal(1, Directory.GetFiles(GetRepoFile("src", "MediaEngine.Web", "Components"), "*RuleBuilder.razor", SearchOption.AllDirectories)
            .Count(path => File.ReadAllText(path).Contains("collection-rule-builder__group", StringComparison.Ordinal)));
        Assert.Contains("Task.Delay(350, _previewCts.Token)", sharedBuilder, StringComparison.Ordinal);
        Assert.Contains("_previewCts?.Cancel()", sharedBuilder, StringComparison.Ordinal);
    }

    private IRenderedComponent<SharedRuleBuilder> RenderBuilder(RuleBuilderCatalog catalog)
    {
        Render<AppPopoverHost>();
        return Render<SharedRuleBuilder>(parameters => parameters
            .Add(component => component.Catalog, catalog)
            .Add(component => component.Definition, new CollectionRuleDefinitionViewModel
            {
                Groups = [new CollectionRuleGroupViewModel()],
            })
            .Add(component => component.SortField, catalog.SortFields[0].Key));
    }

    private static void OpenConditionPicker(IRenderedComponent<SharedRuleBuilder> cut) =>
        cut.FindAll("button").Single(button => button.TextContent.Contains("Add condition", StringComparison.Ordinal)).Click();

    private static string GetRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory!.FullName, .. segments]);
    }
}
