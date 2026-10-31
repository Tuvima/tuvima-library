using Bunit;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class PersonTileTests : AsyncBunitContext
{
    public PersonTileTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddLocalization(); Services.AddNativeUiServices(); }

    [Fact]
    public void ArtistTile_ShowsNameAndSpelledOutCountsBelowThePhoto_AndOpensThePersonPage()
    {
        var personId = Guid.NewGuid();
        var tile = MediaPersonGroupTileComposer.Compose(
            personId, "Adele", personId, null, ["Artist"], "Music", 52, 2008, 2021, DateTimeOffset.UtcNow, [],
            MediaPersonGroupTileComposer.NavigationUrl(personId, "Adele"), "#000", albumCount: 4, songCount: 52);

        var cut = Render<PersonTile>(p => p.Add(x => x.Item, tile));

        Assert.Equal($"/details/person/{personId:D}", cut.Find("a.person-tile").GetAttribute("href"));
        Assert.Equal("Adele", cut.Find(".person-tile__name").TextContent);
        var counts = cut.FindAll(".media-count-line__item").Select(i => i.GetAttribute("title")).ToList();
        Assert.Equal(["4 albums", "52 songs"], counts);
    }
}
