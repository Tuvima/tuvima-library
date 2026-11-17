using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Editor;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Issue 1044: an episode's frame shows in the editor's episode dropdown. The Engine addresses the frame as <c>/stream/artwork/{id}</c>, which
/// only the Engine can serve, so the Dashboard has to send the browser through its picture route (as it already does for the header's cover).
/// </summary>
public sealed class EditorEpisodeFramePictureTests : AsyncBunitContext
{
    private static readonly Guid Frame = Guid.NewGuid();

    public EditorEpisodeFramePictureTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(_ => { }));
        Render<AppPopoverHost>();
    }

    [Fact]
    public void TheEpisodeDropdown_DrawsEachEpisodesFrameThroughTheDashboardsPictureRoute()
    {
        var cut = Render<EditorHeaderNav>(parameters => parameters.Add(nav => nav.Header, HeaderWithFrames()));
        cut.Find("#tl-hnav-item").Click();

        var sources = cut.FindAll(".tl-hnav__option[data-state] .tl-hnav__still img").Select(img => img.GetAttribute("src")).ToList();
        Assert.NotEmpty(sources);
        Assert.All(sources, src => Assert.StartsWith($"/engine-image/stream/artwork/{Frame:D}", src, StringComparison.Ordinal));
    }

    [Fact]
    public void TheClosedEpisodeDropdown_ShowsTheCurrentEpisodesFrameThroughTheDashboardsPictureRoute()
    {
        var cut = Render<EditorHeaderNav>(parameters => parameters.Add(nav => nav.Header, HeaderWithFrames()));

        var src = cut.Find("#tl-hnav-item .tl-hnav__still img").GetAttribute("src");
        Assert.StartsWith($"/engine-image/stream/artwork/{Frame:D}", src, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEpisodeWithNoPicture_DrawsNoImage_AndKeepsItsPlaceholder()
    {
        Assert.Null(EditorNavText.Picture(null));
        Assert.Null(EditorNavText.Picture("  "));
    }

    [Fact]
    public void APictureAlreadyOnTheDashboardsRoute_IsLeftAlone()
    {
        var address = $"/engine-image/stream/artwork/{Frame:D}";

        Assert.Equal(address, EditorNavText.Picture(address));
    }

    private static EditorHeaderDto HeaderWithFrames()
    {
        var header = EditorHeaderTestData.TvEpisode();
        foreach (var item in header.Level.Items)
        {
            item.GetType().GetProperty(nameof(EditorNavItemDto.StillUrl))!.SetValue(item, $"/stream/artwork/{Frame:D}");
        }

        return header;
    }
}
