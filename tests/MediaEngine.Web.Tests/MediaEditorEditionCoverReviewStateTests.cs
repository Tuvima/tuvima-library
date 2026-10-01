using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorEditionCoverReviewStateTests
{
    [Fact]
    public void ReviewIsBoundToRouteFileAndArtworkChoice()
    {
        var route = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var artwork = Guid.NewGuid();
        var state = new MediaEditorEditionCoverReviewState();
        state.Set(route, Review(asset, artwork, DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.True(state.IsCurrent(route, asset, artwork));
        Assert.False(state.IsCurrent(Guid.NewGuid(), asset, artwork));
        Assert.False(state.IsCurrent(route, Guid.NewGuid(), artwork));
        Assert.False(state.IsCurrent(route, asset, Guid.NewGuid()));

        state.Clear();
        Assert.Null(state.Review);
        Assert.False(state.IsCurrent(route, asset, artwork));
    }

    [Fact]
    public void ExpiredReviewCannotBeSaved()
    {
        var route = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var artwork = Guid.NewGuid();
        var state = new MediaEditorEditionCoverReviewState();
        state.Set(route, Review(asset, artwork, DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.False(state.IsCurrent(route, asset, artwork));
    }

    private static MediaEditorEditionCoverPreviewDto Review(
        Guid asset, Guid artwork, DateTimeOffset expiresAt) =>
        new("review", expiresAt, asset, Guid.NewGuid(), Guid.NewGuid(), "Books",
            "Work", Guid.NewGuid(), artwork, "revision", null,
            [new MediaEditorEditionCoverAffectedFileDto(asset, Guid.NewGuid())]);
}
