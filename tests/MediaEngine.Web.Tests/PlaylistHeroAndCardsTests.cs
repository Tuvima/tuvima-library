using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The playlist page's own pieces (spec group-pages-and-tiles D7, WP1b): the hero's album tile and the Cards side of the List / Cards switch.</summary>
public sealed class PlaylistHeroAndCardsTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public PlaylistHeroAndCardsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(EngineApiClientStub.CreateDefault());
    }

    private static ArtworkStackItem Cover(string name) => new() { Id = name, Title = name, ImageUrl = $"/art/{name}.jpg", MediaType = "Music", Shape = ArtworkShape.Square };

    private static SongRow Song(string title, bool owned = true) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Source = SongRowSource.PlaylistItem,
        Title = title,
        Artist = "Ada",
        WorkId = Guid.NewGuid(),
        IsOwned = owned,
    };

    [Fact]
    public async Task TheHero_DrawsTheFirstFourAlbumsAs2By2AndLeavesAMissingQuarterEmpty()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

        var cut = Render<PlaylistHero>(parameters => parameters
            .Add(hero => hero.Title, "Late night")
            .Add(hero => hero.Pills, ["Playlist", "3 songs"])
            .Add(hero => hero.Covers, [Cover("a"), Cover("b"), Cover("c")]));

        Assert.Equal(3, cut.FindAll("img.playlist-hero__quadrant").Count);
        Assert.Single(cut.FindAll(".playlist-hero__quadrant.is-empty"));
        Assert.Equal(["kind", "fact"], cut.FindAll("[data-playlist-pill]").Select(pill => pill.GetAttribute("data-playlist-pill") ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task TheHero_WithNoCovers_ShowsTheFallbackTileInsteadOfPictures()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

        var cut = Render<PlaylistHero>(parameters => parameters.Add(hero => hero.Title, "Empty"));

        Assert.Empty(cut.FindAll("img"));
        Assert.NotEmpty(cut.FindAll("[data-playlist-hero-art]"));
    }

    [Fact]
    public void TheCards_PlayASongOnClickAndOnEnter_ButNeverOneTheLibraryCannotFind()
    {
        var owned = Song("Midnight");
        var missing = Song("Lost", owned: false);
        var played = new List<SongRow>();

        var cut = Render<PlaylistSongCards>(parameters => parameters
            .Add(cards => cards.Rows, [owned, missing])
            .Add(cards => cards.OnPlay, EventCallback.Factory.Create<SongRow>(this, played.Add)));

        cut.FindAll(".playlist-song-card__art")[0].Click();
        cut.FindAll(".playlist-song-card__art")[0].KeyDown(new KeyboardEventArgs { Key = "Enter" });
        cut.FindAll(".playlist-song-card__art")[1].Click();

        Assert.Equal([owned, owned], played);
        Assert.Equal("true", cut.FindAll(".playlist-song-card__art")[1].GetAttribute("aria-disabled"));
    }

    [Fact]
    public void TheCards_HighlightTheSongPlayingNow()
    {
        var first = Song("One");
        var second = Song("Two");

        var cut = Render<PlaylistSongCards>(parameters => parameters
            .Add(cards => cards.Rows, [first, second])
            .Add(cards => cards.CurrentRowId, second.Id)
            .Add(cards => cards.IsPlaying, true));

        var playing = cut.FindAll(".playlist-song-card.is-playing");
        Assert.Single(playing);
        Assert.Equal(second.Id, playing[0].GetAttribute("data-song-row-id"));
    }

    [Fact]
    public void TheCardMenu_OffersRemove_WhenSomeoneListensForIt()
    {
        var song = Song("One");
        var removed = new List<SongRow>();

        var cut = Render<PlaylistSongCards>(parameters => parameters
            .Add(cards => cards.Rows, [song])
            .Add(cards => cards.OnRemove, EventCallback.Factory.Create<SongRow>(this, removed.Add)));

        cut.Find(".playlist-song-card__menu button[aria-haspopup]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Contains("Remove", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal([song], removed));
    }

    [Fact]
    public void TheCardMenu_LeavesRemoveOut_ForAListThePersonCannotChange()
    {
        var cut = Render<PlaylistSongCards>(parameters => parameters
            .Add(cards => cards.Rows, [Song("One")])
            .Add(cards => cards.OnQueue, EventCallback.Factory.Create<SongRow>(this, _ => { })));

        cut.Find(".playlist-song-card__menu button[aria-haspopup]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));

        Assert.DoesNotContain(cut.FindAll("[role='menuitem']"), item => item.TextContent.Contains("Remove", StringComparison.Ordinal));
    }
}
