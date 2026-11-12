using Bunit;
using MediaEngine.Web.Components.Collections;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The rule rows of the smart playlist and collection editors are built on the shared form row (Editor v6 D12, WP11): each rule is one
/// row whose fields share a label line, a control line and a help line, so a label or helper text can never push a neighbour down.
/// </summary>
public sealed class RuleRowFormRowTests : AsyncBunitContext
{
    private const string FieldSelector = ".app-field, [data-form-row-field]";

    public RuleRowFormRowTests()
    {
        Services.AddNativeUiServices();
        Services.AddSingleton(System.Reflection.DispatchProxy.Create<IEngineApiClient, AccountSecurityRenderTests.EmptyEngineApiClient>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SharedRuleBuilder> RenderRules(RuleBuilderLayout layout, params CollectionRulePredicateViewModel[] conditions)
    {
        Render<AppPopoverHost>();
        return Render<SharedRuleBuilder>(parameters => parameters
            .Add(component => component.Catalog, CollectionRuleCatalog.Instance)
            .Add(component => component.Definition, new CollectionRuleDefinitionViewModel
            {
                Groups = [new CollectionRuleGroupViewModel { Conditions = [.. conditions] }],
            })
            .Add(component => component.SortField, "title")
            .Add(component => component.Layout, layout)
            .Add(component => component.ShowHeader, false)
            .Add(component => component.ShowSort, false));
    }

    [Theory]
    [InlineData(RuleBuilderLayout.Groups)]
    [InlineData(RuleBuilderLayout.Sentence)]
    public void ARuleRow_StaysOnOneLineByColumnsAndStacksOnlyWhenNarrow_NotByTheTwelveRemWrap(RuleBuilderLayout layout)
    {
        var cut = RenderRules(layout, new CollectionRulePredicateViewModel { Field = "runtime", Op = "between", Values = ["90", "180"] });

        var row = cut.Find("[data-rule-row]");
        Assert.Contains("app-form-row--stack-narrow", row.ClassList);
        Assert.DoesNotContain("app-form-row--wrap", row.ClassList);
        // Field, Condition, From and To each get a column of the row's grid (narrower for From and To), then the remove button's own.
        var columns = row.QuerySelector(".app-form-row__fields")!.GetAttribute("style")!;
        Assert.Contains("minmax(0, 1fr) minmax(0, 1fr) minmax(0, .9fr) minmax(0, .9fr) auto", columns);
        Assert.Equal(layout == RuleBuilderLayout.Groups, columns.Contains("--tl-form-row-columns: auto ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RuleBuilderLayout.Groups)]
    [InlineData(RuleBuilderLayout.Sentence)]
    public void ARuleRow_RendersOnTheSharedFormRow_WithEveryChildAFormRowField(RuleBuilderLayout layout)
    {
        var cut = RenderRules(layout, new CollectionRulePredicateViewModel { Field = "year", Op = "eq", Value = "2020" });

        var row = cut.Find("[data-rule-row]");
        Assert.True(row.ClassList.Contains("app-form-row"));
        Assert.True(row.HasAttribute("data-form-row"));
        var children = row.QuerySelector(".app-form-row__fields")!.Children;
        Assert.NotEmpty(children);
        Assert.All(children, child => Assert.True(child.Matches(FieldSelector), $"{child.TagName}.{child.ClassName} is not a form row field"));
        // Field, Condition and Value each carry a label on the shared label line.
        var labels = children.Select(child => child.QuerySelector(".app-field__label, label")?.TextContent.Trim()).ToList();
        Assert.Contains("Field", labels);
        Assert.Contains("Condition", labels);
        Assert.Contains("Value", labels);
    }

    [Fact]
    public void ABetweenRule_GivesFromAndToTheirOwnFieldsOfTheRow()
    {
        var cut = RenderRules(RuleBuilderLayout.Groups, new CollectionRulePredicateViewModel { Field = "runtime", Op = "between", Values = ["90", "180"] });

        var fields = cut.Find("[data-rule-row] .app-form-row__fields").Children;
        var from = fields.Single(child => child.QuerySelector(".app-field__label")?.TextContent.Trim() == "From");
        var to = fields.Single(child => child.QuerySelector(".app-field__label")?.TextContent.Trim() == "To");
        Assert.NotNull(from.QuerySelector("input"));
        Assert.NotNull(to.QuerySelector("input"));
    }

    [Fact]
    public void TheRemoveButton_SitsOnTheControlLineOfItsOwnFieldBelowABlankLabelLine()
    {
        var cut = RenderRules(RuleBuilderLayout.Groups, new CollectionRulePredicateViewModel { Field = "year", Op = "eq", Value = "2020" });

        var fields = cut.Find("[data-rule-row] .app-form-row__fields").Children;
        var remove = fields.Last();
        Assert.True(remove.HasAttribute("data-form-row-field"));
        var parts = remove.Children;
        Assert.True(parts[0].ClassList.Contains("app-field__label"));
        Assert.Equal("true", parts[0].GetAttribute("aria-hidden"));
        Assert.NotNull(remove.QuerySelector("button[aria-label]"));
        Assert.Empty(remove.QuerySelectorAll(".app-field__label button"));
    }

    [Fact]
    public void ARuleWithNoValueNeeded_ShowsTheNoteAsTheControlOfALabeledValueField()
    {
        var cut = RenderRules(RuleBuilderLayout.Sentence, new CollectionRulePredicateViewModel { Field = "runtime", Op = "known" });

        var note = cut.Find(".collection-rule-builder__no-value");
        var field = note.ParentElement!;
        Assert.True(field.HasAttribute("data-form-row-field"));
        Assert.Equal("Value", field.QuerySelector(".app-field__label")!.TextContent.Trim());
        Assert.Equal("No value needed", field.Children.Single(child => !child.ClassList.Contains("app-field__label")).TextContent.Trim());
    }

    [Fact]
    public void TheGroupsLayout_PutsTheGripOnTheRowAsAFieldWithABlankLabelLine()
    {
        var cut = RenderRules(RuleBuilderLayout.Groups, new CollectionRulePredicateViewModel { Field = "year", Op = "eq", Value = "2020" });

        var grip = cut.Find("[data-rule-row] .collection-rule-builder__drag-cell");
        Assert.True(grip.HasAttribute("data-form-row-field"));
        Assert.True(grip.Children[0].ClassList.Contains("app-field__label"));
        Assert.NotNull(grip.QuerySelector(".collection-rule-builder__condition-drag svg"));
    }

    [Fact]
    public void TheValuePicker_IsAFieldWithItsLabelOnTheLabelLineAndItsTriggerOnTheControlLine()
    {
        var cut = RenderRules(RuleBuilderLayout.Sentence, new CollectionRulePredicateViewModel { Field = "genre", Op = "in", Values = ["Horror"], DisplayValues = ["Horror"] });

        var picker = cut.Find(".collection-rule-value-picker");
        Assert.True(picker.ClassList.Contains("app-field"));
        Assert.True(picker.Children[0].ClassList.Contains("app-field__label"));
        Assert.True(picker.Children[1].ClassList.Contains("collection-rule-value-picker__trigger"));
    }

    [Fact]
    public void AHelperLine_OnAFieldBesideTheValuePicker_RendersOnTheHelpLineNotInsideAControlCell()
    {
        // A rule field that has helper text renders it as the field's own help line, the same part every field of the row uses.
        var cut = Render<AppFormRow>(parameters => parameters
            .Add(row => row.Wrap, true)
            .Add(row => row.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<AppTextField>(0);
                builder.AddAttribute(1, "Label", "Number");
                builder.AddAttribute(2, "Value", "7");
                builder.AddAttribute(3, "HelpText", "Days back from today");
                builder.CloseComponent();
                builder.OpenComponent<CollectionRuleValuePicker>(4);
                builder.AddAttribute(5, "Field", "genre");
                builder.CloseComponent();
            })));

        var fields = cut.Find(".app-form-row__fields").Children;
        Assert.Equal(2, fields.Length);
        var help = fields[0].QuerySelector(".app-field__help");
        Assert.NotNull(help);
        Assert.Same(fields[0], help!.ParentElement);
        Assert.Equal("Days back from today", help.TextContent.Trim());
        // The help is its own line of the field, never inside the element that holds the control.
        var control = fields[0].Children.Single(child => child.QuerySelector("input") is not null || child.TagName == "INPUT");
        Assert.DoesNotContain(help, control.QuerySelectorAll("*"));
        // The neighbour is a field with no help of its own, so its control line is not pushed.
        Assert.Empty(fields[1].QuerySelectorAll(".app-field__help"));
    }

    [Fact]
    public void TheOrderAndLimitsRow_PutsTheTwoSelectsAsFieldsAndTheAdvancedSwitchOnTheControlLine()
    {
        Render<AppPopoverHost>();
        var cut = Render<CollectionRuleBuilder>(parameters => parameters
            .Add(component => component.Definition, new CollectionRuleDefinitionViewModel { Groups = [new CollectionRuleGroupViewModel()] }));

        var row = cut.Find("[data-order-and-limits] .app-form-row.collection-rule-sentence__order-row");
        Assert.Contains("app-form-row--stack-narrow", row.ClassList);
        var children = row.QuerySelector(".app-form-row__fields")!.Children;
        Assert.Equal(3, children.Length);
        Assert.Equal("Order", children[0].QuerySelector(".app-field__label")!.TextContent.Trim());
        Assert.True(children[0].Matches(FieldSelector));
        Assert.True(children[1].Matches(FieldSelector));
        // The switch is a direct child that is not a field: the row centres it on the control line shared with the selects.
        Assert.False(children[2].Matches(FieldSelector));
        Assert.NotNull(children[2].QuerySelector("input[role='switch']"));
    }
}
