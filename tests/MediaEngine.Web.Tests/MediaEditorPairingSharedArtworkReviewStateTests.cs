using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorPairingSharedArtworkReviewStateTests
{
    [Fact]
    public void ReviewIsBoundToOwnerRoleImageChoiceAndSelection()
    {
        var owner = Guid.NewGuid();
        var image = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var state = new MediaEditorPairingSharedArtworkReviewState();
        state.Set(new MediaEditorPairingSharedArtworkPreviewDto("token", now.AddMinutes(5),
            owner, "TvShow", "Background", image, "revision",
            [new MediaEditorPairingArtworkAffectedFileDto(Guid.NewGuid(), Guid.NewGuid())]),
            "operation-1", "file-1:revision-1");

        Assert.True(state.IsCurrent(owner, "TvShow", "Background", image,
            "operation-1", "file-1:revision-1", now));
        Assert.False(state.IsCurrent(Guid.NewGuid(), "TvShow", "Background", image,
            "operation-1", "file-1:revision-1", now));
        Assert.False(state.IsCurrent(owner, "TvShow", "Logo", image,
            "operation-1", "file-1:revision-1", now));
        Assert.False(state.IsCurrent(owner, "TvShow", "Background", Guid.NewGuid(),
            "operation-1", "file-1:revision-1", now));
        Assert.False(state.IsCurrent(owner, "TvShow", "Background", image,
            "operation-2", "file-1:revision-1", now));
        Assert.False(state.IsCurrent(owner, "TvShow", "Background", image,
            "operation-1", "file-2:revision-2", now));
        Assert.False(state.IsCurrent(owner, "TvShow", "Background", image,
            "operation-1", "file-1:revision-1", now.AddMinutes(6)));

        state.Invalidate();
        Assert.Null(state.Review);
        Assert.False(state.IsCurrent(owner, "TvShow", "Background", image,
            "operation-1", "file-1:revision-1", now));
    }
}
