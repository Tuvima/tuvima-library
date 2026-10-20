using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Tests.Support;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>A title added anyway (spec D13) carries a quiet "Not matched" tag in the hero, on both hero treatments.</summary>
public sealed class HeroMetadataPillsNotMatchedTests : AsyncBunitContext
{
    public HeroMetadataPillsNotMatchedTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly MetadataPill[] Pills =
    [
        new() { Label = "2007", Kind = "year" },
        new() { Label = "Not matched", Kind = "match_state", Tooltip = "Added without matching an entry in the list." },
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANotMatchedTitle_ShowsTheTag(bool primaryChrome)
    {
        var pills = Render<HeroMetadataPills>(p => p
            .Add(x => x.Metadata, Pills)
            .Add(x => x.EntityType, DetailEntityType.Work)
            .Add(x => x.UsePrimaryHeroChrome, primaryChrome));

        Assert.Contains("Not matched", pills.Markup, StringComparison.Ordinal);
    }
}
