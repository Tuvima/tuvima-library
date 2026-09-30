using Bunit;
using MediaEngine.Web.Components.MediaEditor;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class EditorContextNavigatorTests : AsyncBunitContext
{
    private readonly IRenderedComponent<MudPopoverProvider> _popovers;

    public EditorContextNavigatorTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _popovers = Render<MudPopoverProvider>();
    }

    [Fact]
    public void RendersPersistentHierarchyAndKeepsLevelBodiesActionable()
    {
        var seriesId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var selectedId = Guid.Empty;
        var levels = new List<EditorContextLevel>
        {
            new("Series", "series", "Breaking Bad", null, seriesId, false, true, false, []),
            new("Season", "season", "Season 1", "2 episodes", seasonId, false, true, true,
            [
                new EditorContextOption(seasonId, "Season", "Season 1", "2 episodes", true, true),
            ]),
            new("Episode", "episode", "Cat's in the Bag...", "S1 E2", episodeId, true, true, true,
            [
                new EditorContextOption(episodeId, "Episode", "Cat's in the Bag...", "S1 E2", true, true),
            ]),
        };

        var cut = Render<EditorContextNavigator>(parameters => parameters
            .Add(component => component.Levels, levels)
            .Add(component => component.OnTargetSelected,
                EventCallback.Factory.Create<Guid>(this, id => selectedId = id)));

        Assert.Equal(3, cut.FindAll(".editor-context-level").Count);
        Assert.Empty(cut.FindAll(".editor-context__separator"));
        Assert.Equal(3, cut.FindAll(".editor-context-level__selector-trigger").Count);
        Assert.Equal("page", cut.Find(".editor-context-level.is-active .editor-context-level__body").GetAttribute("aria-current"));
        Assert.Contains("Series", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Breaking Bad", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Season 1", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Cat's in the Bag...", cut.Markup, StringComparison.Ordinal);

        cut.FindAll(".editor-context-level__body")[0].Click();

        Assert.Equal(seriesId, selectedId);
    }

    [Theory]
    [InlineData("series", "Series", "Movie series", false)]
    [InlineData("item", "Item", "Standalone movie", false)]
    [InlineData("series", "Series", "Book series", false)]
    [InlineData("book", "Book", "Standalone book", false)]
    [InlineData("series", "Series", "Comic series", false)]
    [InlineData("issue", "Issue", "Standalone comic", false)]
    [InlineData("album", "Album", "Album", false)]
    [InlineData("track", "Track", "Track", true)]
    public void RendersMediaVariantsWithArtworkAndTextOnlyTrackTreatment(
        string nodeKind,
        string label,
        string title,
        bool textOnly)
    {
        var levels = new[]
        {
            new EditorContextLevel(
                label,
                nodeKind,
                title,
                "Retail Matched",
                Guid.NewGuid(),
                true,
                true,
                false,
                [],
                textOnly ? null : "https://engine.test/stream/cover",
                "Matched",
                "Inherited",
                textOnly),
        };

        var cut = Render<EditorContextNavigator>(parameters => parameters.Add(component => component.Levels, levels));

        Assert.Contains("Retail Matched", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Canonical Inherited", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".editor-context-level__statuses"));
        Assert.Equal(!textOnly, cut.FindAll(".editor-context-level__artwork").Count == 1);
        Assert.Equal(textOnly, cut.FindAll(".editor-context-level__icon").Count == 1);
    }

    [Fact]
    public void DropdownBoundsInitialOptionsAndSelectsSearchResult()
    {
        var options = Enumerable.Range(1, 65).Select(index => new EditorContextOption(
            Guid.NewGuid(), "Episode", $"Chapter {index}", $"S1 E{index}", index == 1, true,
            $"/stream/artwork/episode-{index}", "Matched", "Inherited", false)).ToList();
        Guid? selected = null;
        var cut = Render<EditorContextNavigator>(parameters => parameters
            .Add(component => component.Levels, new[]
            {
                new EditorContextLevel("Episode", "episode", "Chapter 1", "S1 E1", options[0].EntityId,
                    true, true, true, options, options[0].ArtworkUrl),
            })
            .Add(component => component.OnTargetSelected,
                EventCallback.Factory.Create<Guid>(this, id => selected = id)));

        cut.Find(".editor-context-level__selector-trigger").Click();
        Assert.Equal(50, _popovers.FindAll(".editor-context-option").Count);
        Assert.Equal(DropdownWidth.Relative, cut.FindComponent<MudPopover>().Instance.RelativeWidth);
        Assert.Contains("app-overflow-menu__popover--match-anchor", _popovers.Markup);
        Assert.DoesNotContain("Inherited", _popovers.Markup);
        Assert.DoesNotContain("Matched", _popovers.Markup);
        Assert.Empty(_popovers.FindAll(".editor-context-option__label"));
        _popovers.Find("input[type=search]").Input("Chapter 65");
        var result = Assert.Single(_popovers.FindAll(".editor-context-option"));
        Assert.Equal("/stream/artwork/episode-65",
            _popovers.Find(".editor-context-option__artwork").GetAttribute("src"));
        result.Click();
        Assert.Equal(options[64].EntityId, selected);
        Assert.Equal("false", cut.Find(".editor-context-level__selector-trigger").GetAttribute("aria-expanded"));
    }

}
