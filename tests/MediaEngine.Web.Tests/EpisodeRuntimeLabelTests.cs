using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>The length under an episode's tile (issue 1171): the file's real length first, then the provider's runtime, else nothing.</summary>
public sealed class EpisodeRuntimeLabelTests : AsyncBunitContext
{
    public EpisodeRuntimeLabelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
    }

    private IStringLocalizer<SharedStrings> Localizer => Services.GetRequiredService<IStringLocalizer<SharedStrings>>();

    private static SequenceItemViewModel Episode(string? duration = null, double? seconds = null) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        EntityType = DetailEntityType.TvEpisode,
        Title = "Episode",
        Duration = duration,
        DurationSeconds = seconds,
        IsOwned = true,
    };

    [Theory]
    [InlineData(2520d, "42 min")]
    [InlineData(3600d, "1h")]
    [InlineData(3900d, "1h 5m")]
    [InlineData(7320d, "2h 2m")]
    public void TheFilesRealLength_IsWrittenInMinutes_AndAsHoursAndMinutesFromAnHour(double seconds, string expected)
    {
        Assert.Equal(expected, EpisodeRuntimeLabel.For(Episode(duration: "99m", seconds: seconds), Localizer));
    }

    [Theory]
    [InlineData("42m", "42 min")]
    [InlineData("42", "42 min")]
    [InlineData("42:00", "42 min")]
    [InlineData("1h 5m", "1h 5m")]
    [InlineData("1:05:00", "1h 5m")]
    [InlineData("1h", "1h")]
    public void WithoutTheFilesLength_TheProviderRuntimeIsUsed(string runtime, string expected)
    {
        Assert.Equal(expected, EpisodeRuntimeLabel.For(Episode(duration: runtime), Localizer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("unknown")]
    public void WhenNeitherLengthIsKnown_NothingIsDrawn(string? runtime)
    {
        Assert.Null(EpisodeRuntimeLabel.For(Episode(duration: runtime), Localizer));
    }

    [Fact]
    public void AnEpisodeTile_ShowsItsLengthUnderIt_AndHidesTheLineWithoutOne()
    {
        var withLength = Render<SequenceEntryContent>(p => p
            .Add(c => c.Item, Episode(duration: "42m"))
            .Add(c => c.IsSeasonContainer, true));
        var withoutLength = Render<SequenceEntryContent>(p => p
            .Add(c => c.Item, Episode())
            .Add(c => c.IsSeasonContainer, true));

        Assert.Equal("42 min", withLength.Find(".tl-series-item__runtime").TextContent.Trim());
        Assert.Empty(withoutLength.FindAll(".tl-series-item__runtime"));
        Assert.Empty(withoutLength.FindAll(".tl-series-item__meta"));
    }
}
