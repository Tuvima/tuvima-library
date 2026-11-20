using System.Net;
using System.Text;
using Bunit;
using MediaEngine.Web.Components.View;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The Photos smart search bar (spec view-people-search WP4): suggestions open under the bar, a chosen one becomes a chip, chips combine, and the keyboard works.</summary>
public sealed class ViewSmartSearchTests : AsyncBunitContext
{
    private const string Suggestions = "{\"items\":[{\"kind\":\"person\",\"value\":\"Sam\",\"count\":412},{\"kind\":\"year\",\"value\":\"2020\",\"count\":1204},{\"kind\":\"place\",\"value\":\"Lisbon\",\"count\":156}]}";

    public ViewSmartSearchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<ViewWorkspaceService>();
    }

    [Fact]
    public void ChipsCombine_YearBecomesADateRange_AndEverythingElseASearchWord()
    {
        ViewSearchChip[] chips = [new("person", "Sam"), new("place", "Lisbon"), new("year", "2020")];

        Assert.Equal("Sam Lisbon 20", ViewSearchChips.SearchText(chips, "20"));
        Assert.Equal("Sam Lisbon", ViewSearchChips.SearchText(chips, null));
        var (from, to) = ViewSearchChips.YearRange(chips);
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(2020, to!.Value.Year);
        Assert.Equal((null, null), ViewSearchChips.YearRange([new ViewSearchChip("person", "Sam")]));
    }

    [Fact]
    public async Task ClickingTheBar_ShowsGroupedSuggestions_AndChoosingOneAddsAChip()
    {
        ViewSearchChip? added = null;
        var cut = Render<ViewSmartSearch>(parameters => parameters
            .Add(search => search.Chips, [])
            .Add(search => search.ChipAdded, EventCallback.Factory.Create<ViewSearchChip>(this, chip => added = chip)));

        await cut.Find(".view-smart-search__input").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[role=option]")));

        var headings = cut.FindAll(".view-smart-search__list h5").Select(h => h.TextContent).ToList();
        Assert.Equal(["People", "Years", "Places"], headings);
        Assert.Equal("true", cut.Find("[role=combobox]").GetAttribute("aria-expanded"));

        await cut.FindAll("[role=option]")[0].ClickAsync(new());

        Assert.Equal(new ViewSearchChip("person", "Sam"), added);
        Assert.Empty(cut.FindAll("[role=option]"));
    }

    [Fact]
    public async Task ChipsShowARemoveButton_AndBackspaceOnEmptyTextRemovesTheLastChip()
    {
        var removed = new List<ViewSearchChip>();
        var cut = Render<ViewSmartSearch>(parameters => parameters
            .Add(search => search.Chips, [new ViewSearchChip("person", "Sam"), new ViewSearchChip("place", "Lisbon")])
            .Add(search => search.ChipRemoved, EventCallback.Factory.Create<ViewSearchChip>(this, chip => removed.Add(chip))));

        Assert.Equal(2, cut.FindAll(".view-smart-search__chip").Count);
        await cut.FindAll(".view-smart-search__remove")[0].ClickAsync(new());
        await cut.Find(".view-smart-search__input").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Backspace" });

        Assert.Equal([new ViewSearchChip("person", "Sam"), new ViewSearchChip("place", "Lisbon")], removed);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/view/search-suggestions" ? Suggestions : "{}", Encoding.UTF8, "application/json"),
            });
    }
}
