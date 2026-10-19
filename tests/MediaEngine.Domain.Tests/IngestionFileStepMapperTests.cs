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
        var progress = IngestionFileStepMapper.Map(Row(stage: stage, status: MediaOperationStatus.Running));

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
            stage: MediaOperationStage.Completed, status: MediaOperationStatus.Succeeded, identity: state));

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
            pendingReview: pendingReview));

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
        var progress = IngestionFileStepMapper.Map(Row(stage: MediaOperationStage.Failed, status: status));

        Assert.True(progress.IsDone);
        Assert.Equal(expected, progress.Outcome);
    }

    [Theory]
    [InlineData(MediaOperationStatus.RetryWaiting)]
    [InlineData(MediaOperationStatus.FailedRetryable)]
    [InlineData(MediaOperationStatus.Interrupted)]
    [InlineData(MediaOperationStatus.Paused)]
    [InlineData(MediaOperationStatus.Blocked)]
    public void OperationStillInTheQueue_KeepsItsStageStepAndIsNotDone(string status)
    {
        var progress = IngestionFileStepMapper.Map(Row(stage: MediaOperationStage.Scoring, status: status));

        Assert.Equal(IngestionFileStep.Read, progress.Step);
        Assert.False(progress.IsDone);
    }

    [Fact]
    public void RetryStageOfAFileWithNoAsset_IsTreatedAsSettledAndWithAnAssetAsRegistered()
    {
        var unregistered = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Failed, status: MediaOperationStatus.RetryWaiting));
        var registered = IngestionFileStepMapper.Map(
            Row(stage: MediaOperationStage.Failed, status: MediaOperationStatus.RetryWaiting, asset: Guid.NewGuid()));

        Assert.Equal(IngestionFileStep.Settled, unregistered.Step);
        Assert.Equal(IngestionFileStep.Registered, registered.Step);
    }

    [Fact]
    public void SucceededOperationWithNoIdentityJob_IsDoneAndAdded()
    {
        var progress = IngestionFileStepMapper.Map(Row(
            stage: MediaOperationStage.Completed, status: MediaOperationStatus.Succeeded, summary: "ingestion completed"));

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
            hold: "ffmpeg"));

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
            hold: "ffmpeg"));

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
            stage: MediaOperationStage.Scoring, status: MediaOperationStatus.Running, hold: "ffmpeg"));

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
        Guid? asset = null) => new(
            Guid.NewGuid(),
            Now,
            Now,
            stage,
            status,
            summary,
            asset ?? (identity is null ? null : Guid.NewGuid()),
            identity?.ToString(),
            identity is null ? null : Now,
            "Movies",
            hold,
            pendingReview);
}
