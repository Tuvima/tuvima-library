namespace MediaEngine.Providers.Models;

public enum CollectionFinalizationReason
{
    QuickHydration,
    QidResolved,
    RetainedRetailIdentity,
    Backfill,
    PersonReleasedSeriesChoice,
}

public sealed record CollectionFinalizationResult(
    CollectionAssignmentResult Assignment,
    bool ParentResolutionAttempted = false,
    bool ParentResolutionFailed = false)
{
    public bool Succeeded => Assignment.Outcome is not CollectionAssignmentOutcome.Failed && !ParentResolutionFailed;
}
