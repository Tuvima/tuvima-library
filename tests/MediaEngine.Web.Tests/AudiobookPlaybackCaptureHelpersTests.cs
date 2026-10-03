using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class AudiobookPlaybackCaptureHelpersTests
{
    [Fact]
    public void SleepBoundary_CapturesCurrentChapterAndImmediateCrossAssetSuccessor()
    {
        var workId = Guid.NewGuid();
        var sessionLeaseId = Guid.NewGuid();
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var chapters = new PlaybackChapterDto[]
        {
            new() { Index = 28, AssetId = firstAsset, Title = "Chapter 29", StartSeconds = 0, EndSeconds = 2199 },
            new() { Index = 29, AssetId = secondAsset, Title = "Chapter 30", StartSeconds = 0, EndSeconds = 837 },
            new() { Index = 30, AssetId = secondAsset, Title = "Outro", StartSeconds = 837, EndSeconds = 881 },
        };
        var authorized = new HashSet<Guid> { firstAsset, secondAsset };

        Assert.True(AudiobookSleepBoundaryResolver.TryCapture(
            workId, sessionLeaseId, firstAsset, 1020, chapters, authorized,
            AudiobookSleepBoundaryKind.EndOfChapter, out var currentTarget));
        Assert.Equal(firstAsset, currentTarget!.TargetAssetId);
        Assert.Equal(28, currentTarget.TargetChapterIndex);
        Assert.Equal(2199, currentTarget.TargetEndSeconds);

        Assert.True(AudiobookSleepBoundaryResolver.TryCapture(
            workId, sessionLeaseId, firstAsset, 1020, chapters, authorized,
            AudiobookSleepBoundaryKind.EndOfNextChapter, out var nextTarget));
        Assert.Equal(firstAsset, nextTarget!.OriginAssetId);
        Assert.Equal(secondAsset, nextTarget.TargetAssetId);
        Assert.Equal(29, nextTarget.TargetChapterIndex);
        Assert.Equal(837, nextTarget.TargetEndSeconds);
    }

    [Fact]
    public void SleepBoundary_RejectsUnknownOrAmbiguousTargetsWithoutFirstChapterFallback()
    {
        var assetId = Guid.NewGuid();
        var chapters = new PlaybackChapterDto[]
        {
            new() { Index = 0, AssetId = assetId, Title = "Chapter one", StartSeconds = 0, EndSeconds = 10 },
            new() { Index = 1, AssetId = assetId, Title = "Unknown end", StartSeconds = 10 },
        };

        Assert.False(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), assetId, 20, chapters, new HashSet<Guid> { assetId },
            AudiobookSleepBoundaryKind.EndOfChapter, out var outsideTarget));
        Assert.Null(outsideTarget);

        Assert.False(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), assetId, 3,
            [chapters[0], chapters[0] with { Index = 2, Title = "Overlapping chapter" }],
            new HashSet<Guid> { assetId }, AudiobookSleepBoundaryKind.EndOfChapter, out var overlapTarget));
        Assert.Null(overlapTarget);

        Assert.False(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), assetId, 3,
            [chapters[0], chapters[1]], new HashSet<Guid> { assetId },
            AudiobookSleepBoundaryKind.EndOfNextChapter, out var incompleteSuccessor));
        Assert.Null(incompleteSuccessor);
    }

    [Fact]
    public void SleepBoundary_RejectsUnauthorizedCrossAssetSuccessorAndInvalidOrdering()
    {
        var originAsset = Guid.NewGuid();
        var otherAsset = Guid.NewGuid();
        var chapters = new PlaybackChapterDto[]
        {
            new() { Index = 0, AssetId = originAsset, StartSeconds = 0, EndSeconds = 10 },
            new() { Index = 1, AssetId = otherAsset, StartSeconds = 0, EndSeconds = 20 },
        };

        Assert.False(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), originAsset, 3, chapters, new HashSet<Guid> { originAsset },
            AudiobookSleepBoundaryKind.EndOfNextChapter, out var unauthorizedTarget));
        Assert.Null(unauthorizedTarget);

        Assert.False(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), originAsset, 3, [chapters[1], chapters[0]],
            new HashSet<Guid> { originAsset, otherAsset }, AudiobookSleepBoundaryKind.EndOfNextChapter,
            out var unorderedTarget));
        Assert.Null(unorderedTarget);
    }

    [Fact]
    public void SleepBoundary_ClosedEarlierChaptersDoNotMakeTheCurrentChapterAmbiguous()
    {
        var assetId = Guid.NewGuid();
        var chapters = new PlaybackChapterDto[]
        {
            new() { Index = 0, AssetId = assetId, Title = "First", StartSeconds = 0, EndSeconds = 10 },
            new() { Index = 1, AssetId = assetId, Title = "Middle", StartSeconds = 10, EndSeconds = 20 },
            new() { Index = 2, AssetId = assetId, Title = "Last", StartSeconds = 20, EndSeconds = 30 },
        };

        Assert.True(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), assetId, 15, chapters, new HashSet<Guid> { assetId },
            AudiobookSleepBoundaryKind.EndOfChapter, out var target));
        Assert.Equal("Middle", target!.TargetChapterTitle);
        Assert.Equal(20, target.TargetEndSeconds);
    }

    [Fact]
    public void SleepBoundary_EndCurrentWorksWhenUnknownFutureSuccessorCannotBeCaptured()
    {
        var assetId = Guid.NewGuid();
        var chapters = new PlaybackChapterDto[]
        {
            new() { Index = 0, AssetId = assetId, Title = "Known chapter", StartSeconds = 0, EndSeconds = 10 },
            new() { Index = 1, Title = "Unresolved next chapter", StartSeconds = 0 },
        };
        var authorized = new HashSet<Guid> { assetId };

        Assert.True(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), assetId, 5, chapters, authorized,
            AudiobookSleepBoundaryKind.EndOfChapter, out var currentTarget));
        Assert.Equal("Known chapter", currentTarget!.TargetChapterTitle);

        Assert.False(AudiobookSleepBoundaryResolver.TryCapture(
            Guid.NewGuid(), Guid.NewGuid(), assetId, 5, chapters, authorized,
            AudiobookSleepBoundaryKind.EndOfNextChapter, out var nextTarget));
        Assert.Null(nextTarget);
    }

    [Fact]
    public void BookmarkDraft_FreezesPositionAndAllowsSameBookAssetProgressionWhenCapturedAssetRemainsAuthorized()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var capturedAsset = Guid.NewGuid();
        var successorAsset = Guid.NewGuid();
        var chapter = new PlaybackChapterDto
        {
            Index = 4,
            AssetId = capturedAsset,
            Title = "Source chapter",
            StartSeconds = 100,
            EndSeconds = 200,
        };
        var lease = new CapturedAudiobookBookmarkDraftLease();
        var capturedAt = DateTimeOffset.Parse("2026-10-02T12:00:00Z");

        Assert.True(lease.TryCapture(profileId, workId, leaseId, capturedAsset, 123.5, 200, chapter,
            out var draft, capturedAt));
        Assert.Equal(123.5, draft!.PositionSeconds);
        Assert.Equal("Source chapter", draft.ChapterTitle);
        Assert.Equal(capturedAt, draft.CapturedAt);

        // The current queue may now be on a verified same-book asset. Save still targets
        // the original captured asset and position, as long as that source remains authorized.
        Assert.True(lease.TryBeginSave(draft.Generation, profileId, workId, leaseId,
            new HashSet<Guid> { capturedAsset, successorAsset }, "  margin note  ", out var attempt));
        Assert.Equal(capturedAsset, attempt!.Draft.AssetId);
        Assert.Equal(123.5, attempt.Draft.PositionSeconds);
        Assert.Equal("margin note", attempt.Note);
        Assert.False(lease.TryBeginSave(draft.Generation, profileId, workId, leaseId,
            new HashSet<Guid> { capturedAsset }, null, out _));
        Assert.True(lease.CompleteSave(draft.Generation));
    }

    [Fact]
    public void BookmarkDraft_RequiresCurrentProfileBookSessionAndCapturedAssetAuthorization()
    {
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var lease = new CapturedAudiobookBookmarkDraftLease();
        Assert.True(lease.TryCapture(profileId, workId, leaseId, assetId, 8, 100, null, out var draft));

        Assert.False(lease.TryBeginSave(draft!.Generation, Guid.NewGuid(), workId, leaseId,
            new HashSet<Guid> { assetId }, null, out _));
        Assert.False(lease.TryBeginSave(draft.Generation, profileId, Guid.NewGuid(), leaseId,
            new HashSet<Guid> { assetId }, null, out _));
        Assert.False(lease.TryBeginSave(draft.Generation, profileId, workId, Guid.NewGuid(),
            new HashSet<Guid> { assetId }, null, out _));
        Assert.False(lease.TryBeginSave(draft.Generation, profileId, workId, leaseId,
            new HashSet<Guid>(), null, out _));
        Assert.False(lease.TryBeginSave(draft.Generation, profileId, workId, leaseId,
            new HashSet<Guid> { assetId }, new string('x', 201), out _));
    }

    [Fact]
    public void BookmarkDraft_RequiresCapturedChapterAssetAndPositionWithinKnownDuration()
    {
        var lease = new CapturedAudiobookBookmarkDraftLease();
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var chapter = new PlaybackChapterDto
        {
            Index = 0,
            AssetId = assetId,
            StartSeconds = 0,
            EndSeconds = 50,
            Title = "Chapter",
        };

        Assert.False(lease.TryCapture(profileId, workId, sessionId, assetId, 101, 100, chapter, out _));
        Assert.Null(lease.Current);
        Assert.False(lease.TryCapture(profileId, workId, sessionId, assetId, 10, 100,
            chapter with { AssetId = null }, out _));
        Assert.Null(lease.Current);
        Assert.True(lease.TryCapture(profileId, workId, sessionId, assetId, 10, 100, chapter, out var draft));
        Assert.NotNull(draft);
    }

    [Fact]
    public void BookmarkDraft_InvalidationAndStaleSaveCompletionCannotAffectANewerGeneration()
    {
        var lease = new CapturedAudiobookBookmarkDraftLease();
        var profileId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();

        Assert.True(lease.TryCapture(profileId, workId, sessionId, firstAsset, 10, 100, null, out var first));
        Assert.True(lease.TryBeginSave(first!.Generation, profileId, workId, sessionId,
            new HashSet<Guid> { firstAsset }, null, out _));

        Assert.True(lease.TryCapture(profileId, workId, sessionId, secondAsset, 40, 100, null, out var second));
        Assert.False(lease.CompleteSave(first.Generation));
        Assert.Equal(second, lease.Current);

        Assert.False(lease.TryCapture(profileId, workId, sessionId, Guid.Empty, 20, 100, null, out _));
        Assert.Null(lease.Current);
        lease.Invalidate();
        Assert.Null(lease.Current);
        Assert.False(lease.CompleteSave(second!.Generation));
    }
}
