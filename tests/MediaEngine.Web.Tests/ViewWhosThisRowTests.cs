using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.View;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The "Who's this?" row (spec view-people-search D4): groups without a name, biggest first, each opening the naming step; hidden when there are none.</summary>
public sealed class ViewWhosThisRowTests : AsyncBunitContext
{
    private static readonly Guid Profile = Guid.NewGuid();

    public ViewWhosThisRowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void NoGroups_ShowsNothing()
    {
        var cut = Render<ViewWhosThisRow>(parameters => parameters.Add(row => row.Groups, []).Add(row => row.ProfileId, Profile));

        Assert.Empty(cut.FindAll(".view-whos-this"));
    }

    [Fact]
    public async Task EachGroupIsAFaceWithItsPhotoCount_AndClickingOneOpensTheNamingStep()
    {
        ViewFaceGroupDto? opened = null;
        var big = new ViewFaceGroupDto(Guid.NewGuid(), null, false, 40, 38, Guid.NewGuid(), []);
        var small = new ViewFaceGroupDto(Guid.NewGuid(), null, false, 1, 1, Guid.NewGuid(), []);
        var cut = Render<ViewWhosThisRow>(parameters => parameters
            .Add(row => row.Groups, [big, small])
            .Add(row => row.ProfileId, Profile)
            .Add(row => row.OnOpen, EventCallback.Factory.Create<ViewFaceGroupDto>(this, group => opened = group)));

        Assert.Equal("Who's this?", cut.Find("h2").TextContent);
        var tiles = cut.FindAll(".view-face-tile");
        Assert.Equal(2, tiles.Count);
        Assert.Contains("38 photos", tiles[0].TextContent);
        Assert.Contains("1 photo", tiles[1].TextContent);
        Assert.StartsWith("/view-media/", cut.Find(".view-face-image").GetAttribute("src"));

        await tiles[0].ClickAsync(new());

        Assert.Equal(big, opened);
    }
}
