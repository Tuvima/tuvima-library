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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CastCredit_FaceAsksForTheSmallCopyAndOffersSharperOnesToDenserScreens(bool compact)
    {
        var credit = new EntityCreditViewModel
        {
            EntityId = Guid.NewGuid().ToString("D"),
            EntityType = RelatedEntityType.Person,
            DisplayName = "Timothee Chalamet",
            PrimaryRole = "Actor",
            ImageUrl = "/engine-image/persons/6f6b4d6e-0000-4000-8000-000000000001/headshot?v=abc123",
        };

        var cut = Render<PersonCreditCard>(parameters => parameters
            .Add(component => component.Credit, credit)
            .Add(component => component.Compact, compact));

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
}
