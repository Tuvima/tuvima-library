using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Intelligence.Contracts;
using MediaEngine.Providers.Models;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Runs the collection finalization steps that every organized item needs:
/// lane shelf assignment first, then broader parent-rollup resolution when
/// trusted relationships exist.
/// </summary>
public sealed class CollectionFinalizationService
{
    public const string CollectionAssignmentFailedActionType = "CollectionAssignmentFailed";

    private readonly CollectionAssignmentService _assignment;
    private readonly IParentCollectionResolver _parentResolver;
    private readonly ISystemActivityRepository _activityRepo;
    private readonly ILogger<CollectionFinalizationService> _logger;

    public CollectionFinalizationService(
        CollectionAssignmentService assignment,
        IParentCollectionResolver parentResolver,
        ISystemActivityRepository activityRepo,
        ILogger<CollectionFinalizationService> logger)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(parentResolver);
        ArgumentNullException.ThrowIfNull(activityRepo);
        ArgumentNullException.ThrowIfNull(logger);

        _assignment = assignment;
        _parentResolver = parentResolver;
        _activityRepo = activityRepo;
        _logger = logger;
    }

    public async Task<CollectionFinalizationResult> FinalizeAsync(
        Guid entityId,
        CollectionFinalizationReason reason,
        CancellationToken ct = default)
    {
        CollectionAssignmentResult assignment;
        try
        {
            assignment = await _assignment.AssignAsync(entityId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Collection finalization failed during assignment for entity {EntityId} ({Reason})",
                entityId,
                reason);

            await LogFailureAsync(entityId, reason, "assignment", ex, ct).ConfigureAwait(false);
            return new CollectionFinalizationResult(new CollectionAssignmentResult(
                CollectionAssignmentOutcome.Failed,
                entityId,
                Message: ex.Message));
        }

        if (!assignment.CollectionId.HasValue)
        {
            return new CollectionFinalizationResult(assignment);
        }

        try
        {
            await _parentResolver.ResolveParentCollectionAsync(assignment.CollectionId.Value, ct).ConfigureAwait(false);
            return new CollectionFinalizationResult(assignment, ParentResolutionAttempted: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Collection finalization failed during parent rollup for collection {CollectionId} ({Reason})",
                assignment.CollectionId.Value,
                reason);

            await LogFailureAsync(entityId, reason, "parent_rollup", ex, ct).ConfigureAwait(false);
            return new CollectionFinalizationResult(
                assignment,
                ParentResolutionAttempted: true,
                ParentResolutionFailed: true);
        }
    }

    private async Task LogFailureAsync(
        Guid entityId,
        CollectionFinalizationReason reason,
        string stage,
        Exception ex,
        CancellationToken ct)
    {
        try
        {
            await _activityRepo.LogAsync(new SystemActivityEntry
            {
                ActionType = CollectionAssignmentFailedActionType,
                EntityId = entityId,
                EntityType = "MediaAsset",
                Detail = $"Collection finalization failed during {stage}.",
                ChangesJson = JsonSerializer.Serialize(new
                {
                    reason = reason.ToString(),
                    stage,
                    error = ex.Message,
                }),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception activityEx) when (activityEx is not OperationCanceledException)
        {
            _logger.LogDebug(activityEx, "Could not log collection finalization failure activity for {EntityId}", entityId);
        }
    }
}
