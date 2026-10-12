using Bunit;
using MediaEngine.Web.Components.MediaHub;

namespace MediaEngine.Web.Tests;

public sealed class EmptyShelfStateTests : BunitContext
{
    [Fact]
    public void ShowsTheAddLibraryActionOnlyWhenOneIsProvided()
    {
        var plain = Render<EmptyShelfState>(parameters => parameters.Add(component => component.Title, "Nothing yet"));
        Assert.Empty(plain.FindAll("a"));

        var withAction = Render<EmptyShelfState>(parameters => parameters
            .Add(component => component.Title, "Nothing yet")
            .Add(component => component.ActionLabel, "Add a library")
            .Add(component => component.ActionHref, "/settings/libraries?type=books"));
        var link = Assert.Single(withAction.FindAll("a"));
        Assert.Equal("/settings/libraries?type=books", link.GetAttribute("href"));
        Assert.Contains("Add a library", link.TextContent);
    }
}
