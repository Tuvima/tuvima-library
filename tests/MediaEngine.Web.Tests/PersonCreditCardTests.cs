using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

public sealed class PersonCreditCardTests : AsyncBunitContext
{
    [Fact]
    public void CastCredit_ShowsTheActorWithTheirRoleNameAndLinksToThePerson()
    {
        var personId = Guid.NewGuid();
        var credit = new EntityCreditViewModel
        {
            EntityId = personId.ToString("D"),
            EntityType = RelatedEntityType.Person,
            DisplayName = "Timothee Chalamet",
            PrimaryRole = "Actor",
            CharacterName = "Paul Atreides",
        };

        var cut = Render<PersonCreditCard>(parameters => parameters.Add(component => component.Credit, credit));

        var link = cut.Find("a.tl-credit-card");
        Assert.Equal($"/details/person/{personId:D}", link.GetAttribute("href"));
        Assert.Equal("Timothee Chalamet", cut.Find(".tl-credit-card__name").TextContent);
        Assert.Equal("Paul Atreides", cut.Find(".tl-credit-card__role").TextContent);
    }

    [Fact]
    public void CastCredit_FaceAsksForTheSmallCopyAndOffersSharperOnesToDenserScreens()
    {
        var credit = new EntityCreditViewModel
        {
            EntityId = Guid.NewGuid().ToString("D"),
            EntityType = RelatedEntityType.Person,
            DisplayName = "Timothee Chalamet",
            PrimaryRole = "Actor",
            ImageUrl = "/engine-image/persons/6f6b4d6e-0000-4000-8000-000000000001/headshot?v=abc123",
        };

        var cut = Render<PersonCreditCard>(parameters => parameters.Add(component => component.Credit, credit));

        var image = cut.Find(".tl-credit-card__image img");
        Assert.Contains("size=s", image.GetAttribute("src"));
        Assert.DoesNotContain("size=t", image.GetAttribute("src"));
        Assert.Contains("2x", image.GetAttribute("srcset"));
    }

    [Fact]
    public void CastCredit_WithoutARoleNameShowsThePrimaryRole()
    {
        var credit = new EntityCreditViewModel
        {
            EntityId = Guid.NewGuid().ToString("D"),
            EntityType = RelatedEntityType.Person,
            DisplayName = "Denis Villeneuve",
            PrimaryRole = "Director",
        };

        var cut = Render<PersonCreditCard>(parameters => parameters.Add(component => component.Credit, credit));

        Assert.Equal("Director", cut.Find(".tl-credit-card__role").TextContent);
    }

    // One tile everywhere (details-page-bottom D3): photo, name, role. It has no size or compact variant of its own, so Overview, the Credits tab and
    // an album's credits all draw the same thing.
    [Fact]
    public void TheTile_HasOnePhotoThenTheNameThenTheRole_AndNoSizeVariant()
    {
        var credit = new EntityCreditViewModel
        {
            EntityId = Guid.NewGuid().ToString("D"),
            EntityType = RelatedEntityType.Person,
            DisplayName = "Alex Pettyfer",
            PrimaryRole = "Actor",
            CharacterName = "John Smith",
        };

        var cut = Render<PersonCreditCard>(parameters => parameters.Add(component => component.Credit, credit));

        var children = cut.Find("a.tl-credit-card").Children.Select(child => child.ClassList.First()).ToArray();
        Assert.Equal(["tl-credit-card__image", "tl-credit-card__body"], children);
        Assert.DoesNotContain("tl-credit-card--compact", cut.Markup, StringComparison.Ordinal);
        Assert.Null(typeof(PersonCreditCard).GetProperty("Compact"));
    }
}
