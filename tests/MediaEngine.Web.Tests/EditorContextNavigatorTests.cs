using Bunit;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class EditorContextNavigatorTests : AsyncBunitContext
{
    private readonly IRenderedComponent<AppPopoverHost> _popovers;

    public EditorContextNavigatorTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        _popovers = Render<AppPopoverHost>();
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
    public async Task DropdownShowsAllLoadedOptionsAndSelectsSearchResult()
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
        Assert.Equal(65, cut.FindAll(".editor-context-option").Count);
        Assert.True(cut.FindComponent<AppPopover>().Instance.MatchAnchorWidth);
        Assert.Contains("app-overflow-menu__popover--match-anchor", cut.Markup);
        Assert.DoesNotContain("Inherited", cut.Markup);
        Assert.DoesNotContain("Matched", cut.Markup);
        Assert.Empty(cut.FindAll(".editor-context-option__label"));
        cut.Find("input[type=search]").Input("Chapter 65");
        var result = Assert.Single(cut.FindAll(".editor-context-option"));
        Assert.Equal("/stream/artwork/episode-65?size=s",
            cut.Find(".editor-context-option__artwork").GetAttribute("src"));
        await result.ClickAsync(new());
        Assert.Equal(options[64].EntityId, selected);
        Assert.Equal("false", cut.Find(".editor-context-level__selector-trigger").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task SearchUsesWholeCollectionProviderAndReplacesLocalOptions()
    {
        var remoteId = Guid.NewGuid();
        EditorContextOptionSearchRequest? request = null;
        var level = new EditorContextLevel(
            "Episode", "episode", "Pilot", "S1 E1", Guid.NewGuid(), true, true, true,
            [new EditorContextOption(Guid.NewGuid(), "Episode", "Pilot", "S1 E1", true, true)],
            UsesOwnedCollectionSearch: true);
        var cut = Render<EditorContextNavigator>(parameters => parameters
            .Add(component => component.Levels, [level])
            .Add(component => component.SearchOptionsAsync,
                async (searchRequest, cancellationToken) =>
                {
                    request = searchRequest;
                    await Task.Delay(1, cancellationToken);
                    return [new EditorContextOption(remoteId, "Episode", "The Far Away Chapter", "S9 E99", false, true)];
                }));

        cut.Find(".editor-context-level__selector-trigger").Click();
        cut.Find("input[type=search]").Input("far away");

        await cut.WaitForAssertionAsync(() =>
        {
            Assert.NotNull(request);
            Assert.Equal("far away", request!.Query);
            Assert.Equal(100, request.MaximumResults);
            Assert.Contains("The Far Away Chapter", cut.Find(".editor-context-level__popover").OuterHtml, StringComparison.Ordinal);
            Assert.DoesNotContain(">Pilot<", cut.Find(".editor-context-level__popover").OuterHtml, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ServerSearchableLevelCanOpenWithoutPrefetchedOptions()
    {
        var level = new EditorContextLevel(
            "Episode", "episode", "Select episode", null, null, false, true, true, [],
            UsesOwnedCollectionSearch: true);
        var cut = Render<EditorContextNavigator>(parameters => parameters
            .Add(component => component.Levels, [level])
            .Add(component => component.SearchOptionsAsync,
                (_, _) => Task.FromResult<IReadOnlyList<EditorContextOption>>([])));

        var trigger = cut.Find(".editor-context-level__selector-trigger");
        Assert.False(trigger.HasAttribute("disabled"));
        trigger.Click();
        Assert.Single(cut.FindAll("input[type=search]"));
    }

}
