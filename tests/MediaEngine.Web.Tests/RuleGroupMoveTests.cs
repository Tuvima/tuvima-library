using Bunit;
using MediaEngine.Web.Components.Rules;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Rule groups can be reordered without dragging (editor v6 L19): on a touch arrangement each group carries Move rule group up and down buttons that
/// make the same change a drop does; everywhere else the drag grip is kept and the buttons are not drawn.
/// </summary>
public sealed class RuleGroupMoveTests : AsyncBunitContext
{
    private readonly CollectionRuleDefinitionViewModel _definition = new()
    {
        Groups = [new CollectionRuleGroupViewModel { Id = "a" }, new CollectionRuleGroupViewModel { Id = "b" }, new CollectionRuleGroupViewModel { Id = "c" }],
    };

    private int _changes;

    public RuleGroupMoveTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SharedRuleBuilder> RenderBuilder(bool moveButtons)
    {
        Render<AppPopoverHost>();
        return Render<SharedRuleBuilder>(parameters => parameters
            .Add(component => component.Catalog, CollectionRuleCatalog.Instance)
            .Add(component => component.Definition, _definition)
            .Add(component => component.SortField, CollectionRuleCatalog.Instance.SortFields[0].Key)
            .Add(component => component.MoveButtons, moveButtons)
            .Add(component => component.Changed, EventCallback.Factory.Create(this, () => _changes++)));
    }

    private string Order => string.Concat(_definition.Groups.Select(group => group.Id));

    [Fact]
    public void WithMoveButtons_EachGroupHasMoveUpAndDownAndNoDragGrip()
    {
        var cut = RenderBuilder(moveButtons: true);

        Assert.Equal(3, cut.FindAll("button[aria-label='Move rule group up']").Count);
        Assert.Equal(3, cut.FindAll("button[aria-label='Move rule group down']").Count);
        Assert.Empty(cut.FindAll(".collection-rule-builder__drag"));
    }

    [Fact]
    public void WithoutMoveButtons_TheDragGripStaysAndNoMoveButtonIsDrawn()
    {
        var cut = RenderBuilder(moveButtons: false);

        Assert.Equal(3, cut.FindAll(".collection-rule-builder__drag[draggable=true]").Count);
        Assert.Empty(cut.FindAll(".collection-rule-builder__group-move, button[aria-label='Move rule group up']"));
    }

    [Fact]
    public void TheFirstGroupCannotMoveUpAndTheLastCannotMoveDown()
    {
        var cut = RenderBuilder(moveButtons: true);

        Assert.True(cut.FindAll("button[aria-label='Move rule group up']")[0].HasAttribute("disabled"));
        Assert.False(cut.FindAll("button[aria-label='Move rule group up']")[1].HasAttribute("disabled"));
        Assert.True(cut.FindAll("button[aria-label='Move rule group down']")[2].HasAttribute("disabled"));
        Assert.False(cut.FindAll("button[aria-label='Move rule group down']")[1].HasAttribute("disabled"));
    }

    [Fact]
    public void MovingAGroupUpOrDown_ChangesTheOrderOnePlaceAndReportsAChange()
    {
        var cut = RenderBuilder(moveButtons: true);

        cut.FindAll("button[aria-label='Move rule group down']")[0].Click();
        Assert.Equal("bac", Order);
        Assert.Equal(1, _changes);

        cut.FindAll("button[aria-label='Move rule group up']")[2].Click();
        Assert.Equal("bca", Order);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void ADropStillMovesAGroupTheWayItDid()
    {
        var cut = RenderBuilder(moveButtons: false);

        cut.FindAll(".collection-rule-builder__drag")[0].DragStart();
        cut.FindAll(".collection-rule-builder__group")[2].Drop();

        Assert.Equal("bac", Order);
    }
}
