using MediaEngine.Contracts.Details;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Who is who on the page being edited (universal editor spec D7): the trail to the page's own item and what each step can switch to,
/// read from the detail page the Dashboard already has. Pure: nothing is fetched.
/// </summary>
public sealed class EditTrailCatalogTests
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid SeasonTwo = EditTrailTestData.SeasonTwoId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;

    [Fact]
    public void AShowPage_TrailsToItself_AndOffersItsSeasonsAndTheEpisodesOfTheOpenSeason()
    {
        var catalog = EditTrailCatalog.From(EditTrailTestData.ShowPage());

        Assert.Equal([ShowId], catalog.PageTrail.Select(step => step.EntityId));
        Assert.Equal(["Season 1", "Season 2"], catalog.ChildrenOf(ShowId).Select(child => child.Title));
        Assert.Equal(["Episode 1", "Episode 2"], catalog.ChildrenOf(SeasonOne).Select(child => child.Title));
        Assert.Empty(catalog.ChildrenOf(SeasonTwo));
        Assert.True(catalog.HasChoices);
    }

    [Fact]
    public void AnEpisodeOnAShowPage_TrailsThroughItsSeasonToTheShow()
    {
        var catalog = EditTrailCatalog.From(EditTrailTestData.ShowPage());

        var trail = catalog.TrailTo(EpisodeTwo)!;

        Assert.Equal([ShowId, SeasonOne, EpisodeTwo], trail.Select(step => step.EntityId));
        Assert.Equal(["Show", "Season", "Episode"], trail.Select(step => step.Kind));
        Assert.Equal("Episode 2", catalog.NameOf(EpisodeTwo));
    }

    [Fact]
    public void AnEpisodePage_TrailsShowSeasonEpisode_AndEachStepCanSwitch()
    {
        var catalog = EditTrailCatalog.From(EditTrailTestData.EpisodePage());

        Assert.Equal([ShowId, SeasonOne, EpisodeTwo], catalog.PageTrail.Select(step => step.EntityId));
        Assert.Equal(["Season 1"], catalog.ChildrenOf(ShowId).Select(child => child.Title));
        Assert.Equal(["Episode 1", "Episode 2"], catalog.ChildrenOf(SeasonOne).Select(child => child.Title));
        Assert.True(catalog.CanSwitchFrom(SeasonOne));
        Assert.False(catalog.CanSwitchFrom(EpisodeTwo));
    }

    [Fact]
    public void AnAlbumPage_TrailsArtistAlbum_AndOffersItsOwnedTracksOnly()
    {
        var catalog = EditTrailCatalog.From(EditTrailTestData.AlbumPage());

        Assert.Equal([EditTrailTestData.ArtistId, EditTrailTestData.AlbumId], catalog.PageTrail.Select(step => step.EntityId));
        Assert.Equal(EditTrailKinds.Person, catalog.PageTrail[0].Kind);
        Assert.Equal(["Airbag", "Paranoid Android"], catalog.ChildrenOf(EditTrailTestData.AlbumId).Select(child => child.Title));
        Assert.All(catalog.ChildrenOf(EditTrailTestData.AlbumId), track => Assert.Equal(EditTrailKinds.Track, track.Kind));
        Assert.Equal(
            [EditTrailTestData.ArtistId, EditTrailTestData.AlbumId, EditTrailTestData.TrackTwoId],
            catalog.TrailTo(EditTrailTestData.TrackTwoId)!.Select(step => step.EntityId));
    }

    [Fact]
    public void AMovie_HasNothingToSwitchTo()
    {
        var movie = new DetailPageViewModel { Id = EditingModeTestData.MovieId.ToString("D"), EntityType = DetailEntityType.Movie, Title = "Dune: Part One" };

        var catalog = EditTrailCatalog.From(movie);

        Assert.Equal([EditingModeTestData.MovieId], catalog.PageTrail.Select(step => step.EntityId));
        Assert.False(catalog.HasChoices);
        Assert.False(catalog.CanSwitchFrom(EditingModeTestData.MovieId));
    }

    [Fact]
    public void AShowPageAddressedByItsCollection_NamesItsItemByTheIdItsFieldsAreEditedUnder()
    {
        var collectionId = Guid.NewGuid();
        var model = new DetailPageViewModel
        {
            Id = collectionId.ToString("D"),
            EntityType = DetailEntityType.TvShow,
            Title = EditTrailTestData.ShowTitle,
            EditorTarget = new DetailEditorTarget { EntityId = ShowId.ToString("D"), EntityKind = "Work" },
        };

        var catalog = EditTrailCatalog.From(model);

        Assert.Equal(ShowId, Assert.Single(catalog.PageTrail).EntityId);
    }

    [Fact]
    public void AdoptingTheEnginesChain_FillsInParentsThePageDoesNotName()
    {
        var model = new DetailPageViewModel { Id = EpisodeTwo.ToString("D"), EntityType = DetailEntityType.TvEpisode, Title = "Episode 2" };
        var catalog = EditTrailCatalog.From(model);
        Assert.Single(catalog.PageTrail);

        catalog.AdoptAncestors(
        [
            new EditTrailItem(ShowId, "Le Continental", EditTrailKinds.Show),
            new EditTrailItem(SeasonOne, "Season 1", EditTrailKinds.Season),
            new EditTrailItem(EpisodeTwo, "Episode 2", EditTrailKinds.Episode),
        ]);

        Assert.Equal([ShowId, SeasonOne, EpisodeTwo], catalog.PageTrail.Select(step => step.EntityId));
        Assert.Equal([SeasonOne], catalog.ChildrenOf(ShowId).Select(child => child.EntityId));
        Assert.Equal([EpisodeTwo], catalog.ChildrenOf(SeasonOne).Select(child => child.EntityId));
    }

    [Fact]
    public void ChildrenFoundLater_JoinTheSeasonAndKeepTheirTrail()
    {
        var catalog = EditTrailCatalog.From(EditTrailTestData.ShowPage());
        var late = new EditTrailItem(EditTrailTestData.EpisodeThreeId, "Episode 3", EditTrailKinds.Episode);

        catalog.AddChildren(SeasonTwo, [late, late]);

        Assert.Equal([EditTrailTestData.EpisodeThreeId], catalog.ChildrenOf(SeasonTwo).Select(child => child.EntityId));
        Assert.Equal([ShowId, SeasonTwo, EditTrailTestData.EpisodeThreeId], catalog.TrailTo(EditTrailTestData.EpisodeThreeId)!.Select(step => step.EntityId));
    }

    [Fact]
    public void APageWithoutAnId_GivesAnEmptyCatalog()
    {
        var catalog = EditTrailCatalog.From(new DetailPageViewModel { Id = "not-an-id", Title = "x" });

        Assert.Empty(catalog.PageTrail);
        Assert.False(catalog.HasChoices);
        Assert.Null(catalog.TrailTo(EpisodeOne));
    }
}
