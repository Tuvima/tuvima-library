using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;

namespace MediaEngine.Web.Tests;

public sealed class MediaEditorPairingReviewStateTests
{
    [Fact]
    public void SaveRequiresExplicitPartitionAndAtLeastOneEligibleAcceptedRow()
    {
        var accepted = Row(Guid.NewGuid(), "123", canSave: true);
        var limited = Row(Guid.NewGuid(), "124", canSave: false);
        var preview = Preview([accepted, limited]);
        var state = new MediaEditorPairingReviewState();
        state.Begin(preview);

        Assert.False(state.CanSubmit(preview, DateTimeOffset.UtcNow));
        Assert.False(state.Accept(limited, limited.Proposed!));
        Assert.True(state.Accept(accepted, accepted.Proposed!));
        Assert.False(state.CanSubmit(preview, DateTimeOffset.UtcNow));
        state.Exclude(limited.AssetId);

        var request = state.BuildRequest(preview, DateTimeOffset.UtcNow);
        Assert.NotNull(request);
        Assert.Equal(accepted.AssetId, Assert.Single(request.Accepted).AssetId);
        Assert.Equal(limited.AssetId, Assert.Single(request.ExcludedAssetIds));
    }

    [Fact]
    public void ConflictRetryKeepsOperationTokenUntilUserChangesChoice()
    {
        var row = Row(Guid.NewGuid(), "123", canSave: true);
        var preview = Preview([row]);
        var state = new MediaEditorPairingReviewState();
        state.Begin(preview);
        state.Accept(row, row.Proposed!);

        var first = state.BuildRequest(preview, DateTimeOffset.UtcNow)!;
        var retry = state.BuildRequest(preview, DateTimeOffset.UtcNow)!;
        Assert.Equal(first.OperationToken, retry.OperationToken);

        state.Exclude(row.AssetId);
        Assert.False(state.CanSubmit(preview, DateTimeOffset.UtcNow));
        state.Accept(row, row.Proposed!);
        Assert.NotEqual(first.OperationToken, state.BuildRequest(preview, DateTimeOffset.UtcNow)!.OperationToken);
    }

    [Fact]
    public void ExpiredOrMusicPreviewCannotSave()
    {
        var row = Row(Guid.NewGuid(), "123", canSave: true);
        var expired = Preview([row]) with { ReviewExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var state = new MediaEditorPairingReviewState();
        state.Begin(expired);
        state.Accept(row, row.Proposed!);
        Assert.Null(state.BuildRequest(expired, DateTimeOffset.UtcNow));

        var music = expired with { MediaKind = "music_release_track", ReviewExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        state.Begin(music);
        state.Accept(row, row.Proposed!);
        Assert.Null(state.BuildRequest(music, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void EligibleAlternativeCanReplaceAnIneligibleProposal()
    {
        var row = Row(Guid.NewGuid(), "wrong", canSave: false);
        var alternative = row.Proposed! with
        {
            Child = row.Proposed.Child with { ChildId = "correct", EpisodeNumber = 2 },
            CanSave = true,
        };
        row = row with { Alternatives = [alternative] };
        var preview = Preview([row]);
        var state = new MediaEditorPairingReviewState();
        state.Begin(preview);

        Assert.False(state.Accept(row, row.Proposed!));
        Assert.True(state.Accept(row, alternative));
        Assert.Equal("correct", Assert.Single(state.BuildRequest(preview, DateTimeOffset.UtcNow)!.Accepted).CandidateId);
    }

    [Fact]
    public void SearchedCrossSeasonCandidateMustBeRegisteredAndEligible()
    {
        var row = Row(Guid.NewGuid(), "wrong", canSave: false);
        var preview = Preview([row]);
        var state = new MediaEditorPairingReviewState();
        state.Begin(preview);
        var secondSeason = new MediaEditorPairingChildSearchItemDto(
            row.Proposed!.Child with { ChildId = "correct-s2", SeasonNumber = 2, EpisodeNumber = 1 }, true, null);
        var unavailable = secondSeason with { Child = secondSeason.Child with { ChildId = "unavailable" }, CanSave = false };

        Assert.False(state.AcceptSearched(row, secondSeason));
        state.RegisterSearchedCandidates(row, [secondSeason, unavailable]);
        Assert.False(state.AcceptSearched(row, unavailable));
        Assert.True(state.AcceptSearched(row, secondSeason));
        Assert.Equal("correct-s2", Assert.Single(state.BuildRequest(preview, DateTimeOffset.UtcNow)!.Accepted).CandidateId);
        Assert.Equal(2, state.SearchedAcceptedChild(row.AssetId)?.SeasonNumber);
    }

    private static MediaEditorPairingRowDto Row(Guid assetId, string candidateId, bool canSave) => new(
        assetId, "Episode.mkv", null, null,
        new MediaEditorPairingCandidateDto(new MediaEditorPairingChildDto(candidateId, "show", "tvdb", "Episode", 1, 1, null, null, null),
            "Review", [], [], canSave), [], "Review", false, null, canSave);

    private static MediaEditorPairingPreviewDto Preview(IReadOnlyList<MediaEditorPairingRowDto> rows) => new(
        "tv_episode", "tvdb", "show", false, "Review every row.", rows,
        "review-token", DateTimeOffset.UtcNow.AddMinutes(5));
}
