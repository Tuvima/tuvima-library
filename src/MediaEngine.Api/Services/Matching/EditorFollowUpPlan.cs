using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// Turns an item's durable identity job into the named steps the saved ribbon shows (universal editor v3, D7 and D8 item 1). Pure: the
/// job row is the only input, so the same job always reads the same way, whether the Dashboard watched it happen or came back later.
/// <para>
/// The five steps are the finishing work a match starts: the match itself (already saved), the details from the providers (retail match
/// and the Wikidata bridge), pictures and cast (canonical hydration), related items (Universe enrichment) and the files (write-back,
/// which closes the job). A job that stops in Needs Review or fails marks the step it was on as failed with the reason, so the ribbon can
/// show it amber.
/// </para>
/// </summary>
public static class EditorFollowUpPlan
{
    private static readonly (string Key, string Label)[] Steps =
    [
        (EditorFollowUpSteps.Matched, "Match saved"),
        (EditorFollowUpSteps.Details, "Details from the providers"),
        (EditorFollowUpSteps.PicturesAndCast, "Pictures and cast"),
        (EditorFollowUpSteps.Related, "Related items"),
        (EditorFollowUpSteps.Files, "Files updated"),
    ];

    /// <summary>The follow-up for an item whose job is <paramref name="job"/>; no job means no finishing work.</summary>
    public static EditorFollowUpDto From(Guid entityId, IdentityJob? job)
    {
        var dto = new EditorFollowUpDto { EntityId = entityId, Finished = true };
        if (job is null || !Enum.TryParse<IdentityJobState>(job.State, ignoreCase: true, out var state))
        {
            return dto;
        }

        // How far the job has got: the index of the first step still to do (0 = nothing done yet is never used, the match is always saved).
        var (done, runningIndex, failedIndex, reason) = Position(state, job);
        for (var index = 0; index < Steps.Length; index++)
        {
            var step = new EditorFollowUpStepDto { Key = Steps[index].Key, Label = Steps[index].Label };
            if (failedIndex == index)
            {
                step.State = EditorFollowUpStates.Failed;
                step.Reason = reason;
            }
            else if (index < done)
            {
                step.State = EditorFollowUpStates.Done;
            }
            else if (index == runningIndex)
            {
                step.State = EditorFollowUpStates.Running;
            }
            else
            {
                step.State = EditorFollowUpStates.Waiting;
            }

            dto.Steps.Add(step);
        }

        dto.Finished = dto.Steps.All(step => step.State == EditorFollowUpStates.Done)
            || dto.Steps.Any(step => step.State == EditorFollowUpStates.Failed);
        return dto;
    }

    private static (int Done, int Running, int Failed, string? Reason) Position(IdentityJobState state, IdentityJob job) => state switch
    {
        IdentityJobState.Queued => (1, -1, -1, null),
        IdentityJobState.RetailSearching or IdentityJobState.RetailMatched or IdentityJobState.RetailMatchedNeedsReview
            or IdentityJobState.BridgeSearching => (1, 1, -1, null),
        IdentityJobState.QidResolved or IdentityJobState.QidNoMatch => (2, -1, -1, null),
        IdentityJobState.Hydrating => (2, 2, -1, null),
        IdentityJobState.UniverseEnriching => (3, 3, -1, null),
        IdentityJobState.Ready or IdentityJobState.ReadyWithoutUniverse => (Steps.Length, -1, -1, null),
        IdentityJobState.RetailNoMatch => (1, -1, 1, "No provider record fitted, so the item is waiting in Needs Review."),
        IdentityJobState.QidNeedsReview => (1, -1, 1, "More than one provider record could fit, so the item is waiting in Needs Review."),
        // A failure with a resolved Wikidata record happened while the pictures and cast were being fetched; without one, while finding the details.
        IdentityJobState.Failed when !string.IsNullOrWhiteSpace(job.ResolvedQid) => (2, -1, 2, FailureReason(job)),
        IdentityJobState.Failed => (1, -1, 1, FailureReason(job)),
        _ => (1, -1, -1, null),
    };

    private static string FailureReason(IdentityJob job) =>
        string.IsNullOrWhiteSpace(job.LastError) ? "The Engine could not finish this step, so the item is in Needs Review." : job.LastError.Trim();
}
