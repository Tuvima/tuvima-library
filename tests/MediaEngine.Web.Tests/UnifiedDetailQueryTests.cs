using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The detail address carries <c>season</c> and <c>episode</c> as ids. A value that is not an id is not a page the
/// Engine could hold, so the page says "not found" without asking the Engine and without failing.
/// </summary>
public sealed class UnifiedDetailQueryTests : AsyncBunitContext
{
    private static readonly Guid Show = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private readonly List<(DetailEntityType Type, Guid Id)> _loads = [];

    public UnifiedDetailQueryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.LoadDetailPageAsync), args =>
            {
                _loads.Add(((DetailEntityType)args![0]!, (Guid)args[1]!));
                return Task.FromResult(DetailPageLoadResult.Failed(DetailPageLoadFailure.NotFound));
            })));
    }

    [Theory]
    [InlineData("season=abc", "Season not found")]
    [InlineData("episode=abc", "Episode not found")]
    [InlineData("season=%20abc&tab=cast", "Season not found")]
    public void AMalformedSeasonOrEpisodeShowsNotFoundWithoutAskingTheEngine(string query, string expectedTitle)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/details/tvshow/{Show:D}?{query}");

        var cut = Render<UnifiedDetailPage>(parameters => parameters
            .Add(c => c.EntityType, "tvshow")
            .Add(c => c.Id, Show));

        Assert.Contains(expectedTitle, cut.Markup);
        Assert.Empty(_loads);
    }

    [Fact]
    public void AWellFormedSeasonStillAsksTheEngineForThatSeason()
    {
        var season = Guid.Parse("32000000-0000-0000-0000-000000000002");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/details/tvshow/{Show:D}?season={season:D}");

        Render<UnifiedDetailPage>(parameters => parameters
            .Add(c => c.EntityType, "tvshow")
            .Add(c => c.Id, Show));

        Assert.Equal((DetailEntityType.TvSeason, season), Assert.Single(_loads));
    }
}
