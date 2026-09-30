using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Contracts.Tests;

public sealed class EffectiveArtworkResolverTests
{
    [Fact]
    public void SeasonPreferenceWinsOverSeriesAndRecordsProvenance()
    {
        var season = Workspace("Work", Variant("Primary", "SeasonPoster"));
        var series = Workspace("Work", Variant("Primary", "CoverArt"));
        var child = Workspace("Work");

        var result = EffectiveArtworkResolver.Resolve("Primary", "SeasonPoster", child, [season, series]);

        Assert.Equal(season.EntityId, result.SourceEntityId);
        Assert.True(result.IsInherited);
        Assert.Equal(season.Variants[0].ArtworkAssetId, result.Variant?.ArtworkAssetId);
    }

    [Fact]
    public void SeasonFallsBackToSeriesCoverSlot()
    {
        var season = Workspace("Work");
        var series = Workspace("Work", Variant("Primary", "CoverArt"));

        var result = EffectiveArtworkResolver.Resolve("Primary", "SeasonPoster", season, [series]);

        Assert.Equal(series.EntityId, result.SourceEntityId);
        Assert.True(result.IsInherited);
    }

    [Fact]
    public void ChildPreferenceWinsOverParents()
    {
        var child = Workspace("Work", Variant("Primary", "CoverArt"));
        var parent = Workspace("Work", Variant("Primary", "CoverArt"));

        var result = EffectiveArtworkResolver.Resolve("Primary", "CoverArt", child, [parent]);

        Assert.Equal(child.EntityId, result.SourceEntityId);
        Assert.False(result.IsInherited);
    }

    [Fact]
    public void EpisodeStillDoesNotInheritAParentImage()
    {
        var child = Workspace("Work");
        var parent = Workspace("Work", Variant("Primary", "EpisodeStill"));

        var result = EffectiveArtworkResolver.Resolve("Primary", "EpisodeStill", child, [parent]);

        Assert.Null(result.Variant);
        Assert.False(result.IsInherited);
    }

    [Fact]
    public void ExplicitChildStillRemainsAvailable()
    {
        var child = Workspace("Work", Variant("Primary", "EpisodeStill"));

        var result = EffectiveArtworkResolver.Resolve("Primary", "EpisodeStill", child, []);

        Assert.Equal(child.Variants[0].ArtworkAssetId, result.Variant?.ArtworkAssetId);
    }

    private static ArtworkEntityWorkspaceDto Workspace(string type, params ArtworkEntityVariantDto[] variants) =>
        new(Guid.NewGuid(), type, variants);

    private static ArtworkEntityVariantDto Variant(string role, string slot) =>
        new(Guid.NewGuid(), Guid.NewGuid(), role, null, slot, true, false, "/content", "/thumbnail", null, null, "Portrait", null, null);
}
