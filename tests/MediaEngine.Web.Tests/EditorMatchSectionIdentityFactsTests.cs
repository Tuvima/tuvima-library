using Bunit;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.MediaEditor.Sections;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class EditorMatchSectionIdentityFactsTests : AsyncBunitContext
{
    public EditorMatchSectionIdentityFactsTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void MatchSection_ShowsIdentityStatusAndArtworkSourceWhenTheEngineSendsThem()
    {
        var cut = RenderMatchSection("WikidataLinked", "Provider");

        var facts = FactsByLabel(cut);
        Assert.Equal("Wikidata linked", facts["Identity status"]);
        Assert.Equal("Provider", facts["Artwork source"]);
    }

    [Theory]
    [InlineData("NeedsReview", "Needs review")]
    [InlineData("ProviderMatched", "Provider matched")]
    [InlineData("LocalOnly", "Local only")]
    public void MatchSection_WritesIdentityStatusInPlainWords(string status, string expected)
    {
        var cut = RenderMatchSection(status, null);

        Assert.Equal(expected, FactsByLabel(cut)["Identity status"]);
    }

    [Fact]
    public void MatchSection_ShowsNeitherFactWhenTheEngineSendsNothing()
    {
        var cut = RenderMatchSection(null, null);

        var facts = FactsByLabel(cut);
        Assert.False(facts.ContainsKey("Identity status"));
        Assert.False(facts.ContainsKey("Artwork source"));
    }

    private IRenderedComponent<EditorMatchSection> RenderMatchSection(string? identityStatus, string? artworkSource)
    {
        var card = new MatchCardDisplay("Retail", "A Title", null, null, null, [], [], string.Empty);
        return Render<EditorMatchSection>(parameters => parameters
            .Add(component => component.IsSingleItem, true)
            .Add(component => component.IdentityStatus, identityStatus)
            .Add(component => component.ArtworkSource, artworkSource)
            .Add(component => component.BuildCurrentRetailMatchCard, () => card)
            .Add(component => component.BuildCurrentWikidataMatchCard, () => card)
            .Add(component => component.GetGroupsForTab, _ => Array.Empty<MediaEditorFieldGroup>())
            .Add(component => component.NormalizeContributorDisplay, value => value ?? string.Empty));
    }

    private static Dictionary<string, string> FactsByLabel(IRenderedComponent<EditorMatchSection> cut) =>
        cut.FindAll(".sme-match-current-facts > div")
            .ToDictionary(
                fact => fact.QuerySelector("span")!.TextContent.Trim(),
                fact => fact.QuerySelector("strong")!.TextContent.Trim());
}
