using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Ingestion;

namespace MediaEngine.Domain.Tests;

public sealed class IngestionFileStepMapperTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(MediaOperationStage.Discovered, IngestionFileStep.Found)]
    [InlineData(MediaOperationStage.Queued, IngestionFileStep.Settled)]
    [InlineData(MediaOperationStage.Settling, IngestionFileStep.Settled)]
    [InlineData(MediaOperationStage.WaitingForLock, IngestionFileStep.Settled)]
    [InlineData(MediaOperationStage.Hashing, IngestionFileStep.Settled)]
    [InlineData(MediaOperationStage.Parsing, IngestionFileStep.Settled)]
    [InlineData(MediaOperationStage.Scoring, IngestionFileStep.Read)]
    [InlineData(MediaOperationStage.Registered, IngestionFileStep.Registered)]
    [InlineData(MediaOperationStage.QueuedIdentity, IngestionFileStep.Registered)]
    [InlineData(MediaOperationStage.Completed, IngestionFileStep.Registered)]
    public void OperationStage_MapsToTheStepTheFileHasReached(string stage, IngestionFileStep expected)
    {
        var progress = IngestionFileStepMapper.Map(Row(stage: stage, status: MediaOperationStatus.Running), Now);

        Assert.Equal(expected, progress.Step);
        Assert.False(progress.IsDone);
        Assert.Equal(IngestionFileOutcome.None, progress.Outcome);
    }

    [Theory]
    [InlineData(IdentityJobState.Queued, IngestionFileStep.Identifying)]
    [InlineData(IdentityJobState.RetailSearching, IngestionFileStep.Identifying)]
    [InlineData(IdentityJobState.RetailMatched, IngestionFileStep.Hydrating)]
    [InlineData(IdentityJobState.RetailMatchedNeedsReview, IngestionFileStep.Hydrating)]
    [InlineData(IdentityJobState.BridgeSearching, IngestionFileStep.Hydrating)]
    [InlineData(IdentityJobState.QidResolved, IngestionFileStep.Hydrating)]
    [InlineData(IdentityJobState.Hydrating, IngestionFileStep.Hydrating)]
    [InlineData(IdentityJobState.UniverseEnriching, IngestionFileStep.Enriching)]
    public void LiveIdentityJob_MapsToTheStepOfItsState(IdentityJobState state, IngestionFileStep expected)
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed, status: MediaOperationStatus.Succeeded, identity: state), Now);

        Assert.Equal(expected, progress.Step);
        Assert.False(progress.IsDone);
    }

    [Theory]
    [InlineData(IdentityJobState.Ready, false, IngestionFileOutcome.Added)]
    [InlineData(IdentityJobState.ReadyWithoutUniverse, false, IngestionFileOutcome.Added)]
    [InlineData(IdentityJobState.QidNoMatch, false, IngestionFileOutcome.Added)]
    [InlineData(IdentityJobState.Ready, true, IngestionFileOutcome.NeedsReview)]
    [InlineData(IdentityJobState.QidNoMatch, true, IngestionFileOutcome.NeedsReview)]
    [InlineData(IdentityJobState.RetailNoMatch, false, IngestionFileOutcome.NeedsReview)]
    [InlineData(IdentityJobState.QidNeedsReview, false, IngestionFileOutcome.NeedsReview)]
    [InlineData(IdentityJobState.Failed, false, IngestionFileOutcome.Failed)]
    public void FinishedIdentityJob_IsDoneWithTheMatchingOutcome(
        IdentityJobState state, bool pendingReview, IngestionFileOutcome expected)
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed,
            status: MediaOperationStatus.Succeeded,
            identity: state,
            pendingReview: pendingReview), Now);

        Assert.True(progress.IsDone);
        Assert.Equal(IngestionFileStep.Done, progress.Step);
        Assert.Equal(expected, progress.Outcome);
    }

    [Theory]
    [InlineData(MediaOperationStatus.FailedTerminal, IngestionFileOutcome.Failed)]
    [InlineData(MediaOperationStatus.DeadLettered, IngestionFileOutcome.Failed)]
    [InlineData(MediaOperationStatus.Skipped, IngestionFileOutcome.Skipped)]
    [InlineData(MediaOperationStatus.NoResult, IngestionFileOutcome.Skipped)]
    [InlineData(MediaOperationStatus.MissingConfirmed, IngestionFileOutcome.Skipped)]
    [InlineData(MediaOperationStatus.NotApplicable, IngestionFileOutcome.Skipped)]
    [InlineData(MediaOperationStatus.Cancelled, IngestionFileOutcome.Skipped)]
    public void TerminalOperationWithNoIdentityJob_CountsAsDone(string status, IngestionFileOutcome expected)
    {
        var progress = IngestionFileStepMapper.Map(Row(stage: MediaOperationStage.Failed, status: status), Now);

        Assert.True(progress.IsDone);
        Assert.Equal(expected, progress.Outcome);
    }

    [Theory]
    [InlineData(MediaOperationStatus.Pending)]
    [InlineData(MediaOperationStatus.Queued)]
    [InlineData(MediaOperationStatus.Leased)]
    [InlineData(MediaOperationStatus.Running)]
    [InlineData(MediaOperationStatus.Paused)]
    public void OperationInTheQueue_KeepsItsStageStepAndIsNotDone(string status)
    {
        var progress = IngestionFileStepMapper.Map(Row(stage: MediaOperationStage.Scoring, status: status), Now);

        Assert.Equal(IngestionFileStep.Read, progress.Step);
        Assert.False(progress.IsDone);
    }

    [Theory]
    [InlineData(MediaOperationStatus.RetryWaiting)]
    [InlineData(MediaOperationStatus.FailedRetryable)]
    public void RetryDueInTheFutureOrJustPast_StaysOpenAtItsStageStep(string status)
    {
        var future = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Scoring, status: status, nextRetryAt: Now.AddSeconds(30)), Now);
        var justPast = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Scoring, status: status, nextRetryAt: Now.AddMinutes(-3)), Now);

        Assert.False(future.IsDone);
        Assert.Equal(IngestionFileStep.Read, future.Step);
        Assert.False(justPast.IsDone);
    }

    [Theory]
    [InlineData(MediaOperationStatus.RetryWaiting)]
    [InlineData(MediaOperationStatus.FailedRetryable)]
    public void RetryTenMinutesStaleOrWithNoDueTime_IsDoneAndFailed(string status)
    {
        var stale = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Failed, status: status, nextRetryAt: Now.AddMinutes(-10)), Now);
        var undated = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Failed, status: status), Now);

        Assert.True(stale.IsDone);
        Assert.Equal(IngestionFileOutcome.Failed, stale.Outcome);
        Assert.True(undated.IsDone);
        Assert.Equal(IngestionFileOutcome.Failed, undated.Outcome);
    }

    [Theory]
    [InlineData(MediaOperationStatus.Interrupted)]
    [InlineData(MediaOperationStatus.Blocked)]
    public void InterruptedOrBlockedOperation_IsDoneAndFailedSoItNeverHoldsThePeriodOpen(string status)
    {
        var progress = IngestionFileStepMapper.Map(Row(stage: MediaOperationStage.WaitingForLock, status: status), Now);

        Assert.True(progress.IsDone);
        Assert.Equal(IngestionFileOutcome.Failed, progress.Outcome);
    }

    [Fact]
    public void InterruptedOperationWhoseAssetIsStillBeingIdentified_FollowsTheIdentityJob()
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.QueuedIdentity,
            status: MediaOperationStatus.Interrupted,
            identity: IdentityJobState.Hydrating), Now);

        Assert.False(progress.IsDone);
        Assert.Equal(IngestionFileStep.Hydrating, progress.Step);
    }

    [Fact]
    public void OperationStillOpen_IsDecidedByItsOwnStage_NotByTheLinkedAssetsIdentityJob()
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Scoring,
            status: MediaOperationStatus.Running,
            identity: IdentityJobState.Ready,
            asset: Guid.NewGuid()), Now);

        Assert.False(progress.IsDone);
        Assert.Equal(IngestionFileStep.Read, progress.Step);
        Assert.Equal(IngestionFileOutcome.None, progress.Outcome);
    }

    [Fact]
    public void KeepBothDuplicate_IsSkippedEvenThoughItsLinkedAssetIsStillBeingIdentified()
    {
        // Keep both: the second copy is recorded as a duplicate of the kept asset, which is mid-identity.
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed,
            status: MediaOperationStatus.Succeeded,
            summary: "duplicate",
            identity: IdentityJobState.UniverseEnriching,
            asset: Guid.NewGuid()), Now);

        Assert.True(progress.IsDone);
        Assert.Equal(IngestionFileOutcome.Skipped, progress.Outcome);
    }

    [Fact]
    public void RetryStageOfAFileWithNoAsset_IsTreatedAsSettledAndWithAnAssetAsRegistered()
    {
        var unregistered = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Failed, status: MediaOperationStatus.RetryWaiting, nextRetryAt: Now.AddMinutes(1)), Now);
        var registered = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Failed, status: MediaOperationStatus.RetryWaiting, nextRetryAt: Now.AddMinutes(1), asset: Guid.NewGuid()), Now);

        Assert.Equal(IngestionFileStep.Settled, unregistered.Step);
        Assert.Equal(IngestionFileStep.Registered, registered.Step);
    }

    [Fact]
    public void SucceededOperationWithNoIdentityJob_IsDoneAndAdded()
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed, status: MediaOperationStatus.Succeeded, summary: "ingestion completed"), Now);

        Assert.True(progress.IsDone);
        Assert.Equal(IngestionFileOutcome.Added, progress.Outcome);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("same_path_redetected")]
    [InlineData("duplicate_race")]
    public void DuplicateOrRedetectedFile_IsSkippedEvenThoughItPointsAtAnAssetStillBeingIdentified(string summary)
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed,
            status: MediaOperationStatus.Succeeded,
            summary: summary,
            identity: IdentityJobState.Hydrating,
            hold: "ffmpeg"), Now);

        Assert.True(progress.IsDone);
        Assert.Equal(IngestionFileOutcome.Skipped, progress.Outcome);
        Assert.False(progress.IsWaiting);
    }

    [Fact]
    public void FileHeldForAMissingTool_IsNeverDoneAndIsCappedAtEnriching()
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed,
            status: MediaOperationStatus.Succeeded,
            identity: IdentityJobState.Ready,
            hold: "ffmpeg"), Now);

        Assert.False(progress.IsDone);
        Assert.Equal(IngestionFileStep.Enriching, progress.Step);
        Assert.True(progress.IsWaiting);
        Assert.Equal("ffmpeg", progress.WaitingForToolId);
        Assert.Equal(IngestionFileOutcome.None, progress.Outcome);
    }

    [Fact]
    public void FileHeldEarlyInTheQueue_KeepsItsEarlierStep()
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Scoring, status: MediaOperationStatus.Running, hold: "ffmpeg"), Now);

        Assert.Equal(IngestionFileStep.Read, progress.Step);
        Assert.True(progress.IsWaiting);
    }

    private static IngestionQueueFileRow Row(
        string stage,
        string status,
        string? summary = null,
        IdentityJobState? identity = null,
        string? hold = null,
        bool pendingReview = false,
        Guid? asset = null,
        DateTimeOffset? nextRetryAt = null) => new(
            Guid.NewGuid(),
            Now,
            Now,
            stage,
            status,
            nextRetryAt,
            summary,
            asset ?? (identity is null ? null : Guid.NewGuid()),
            identity?.ToString(),
            identity is null ? null : Now,
            "Movies",
            hold,
            pendingReview);
}
