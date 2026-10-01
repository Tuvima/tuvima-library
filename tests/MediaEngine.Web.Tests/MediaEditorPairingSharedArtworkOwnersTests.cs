using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorPairingSharedArtworkOwnersTests
{
    [Fact]
    public void AcceptedCatalogEpisodeExposesVerifiedShowAndTouchedSeason()
    {
        var show = Guid.NewGuid();
        var season = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var child = new MediaEditorPairingChildDto("episode-201", "show-1", "tvdb",
            "Opening", 2, 1, null, null, null, season);
        var candidate = new MediaEditorPairingCandidateDto(child, "high", [], [], CanSave: true);
        var row = new MediaEditorPairingRowDto(asset, "S02E01.mkv", null, null,
            candidate, [], "high", CanPreselect: false, Limitation: null, CanSave: true);
        var preview = new MediaEditorPairingPreviewDto("tv_episode", "tvdb", "show-1",
            CatalogueComplete: false, CatalogueWarning: null, Rows: [row],
            ReviewToken: "review-token", LocalParentWorkId: show);
        var state = new MediaEditorPairingReviewState();
        state.Begin(preview);

        Assert.Empty(MediaEditorPairingSharedArtworkOwners.Build(preview, state, "Show"));
        Assert.True(state.Accept(row, candidate));
        var owners = MediaEditorPairingSharedArtworkOwners.Build(preview, state, "Show");
        Assert.Equal((show, "TvShow"), (owners[0].Id, owners[0].Scope));
        Assert.Equal((season, "TvSeason"), (owners[1].Id, owners[1].Scope));
        Assert.Contains("Season 2", owners[1].Label);

        state.Exclude(asset);
        Assert.Empty(MediaEditorPairingSharedArtworkOwners.Build(preview, state, "Show"));
    }
}
