using MediaEngine.Contracts.Activity;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Which kinds of event belong to which category of the Activity log (the Events part of System &gt; Logs). This is the one
/// place the grouping is written. The "all" filter is the four categories together, not every row in the ledger: the
/// ledger also holds technical events (hashing, scoring, sidecars) that are never shown as Events.
/// </summary>
public static class ActivityLogCategories
{
    private static readonly string[] AddedTypes =
    [
        SystemActionType.MediaAdded,
        SystemActionType.PersonalMediaAdded,
        SystemActionType.PersonalMediaSkipped,
    ];

    private static readonly string[] ReviewTypes =
    [
        SystemActionType.ReviewItemCreated,
        SystemActionType.ReviewItemResolved,
    ];

    private static readonly string[] FailedTypes =
    [
        SystemActionType.MediaFailed,
        SystemActionType.FileQuarantined,
        SystemActionType.ArtworkWritebackFailed,
        SystemActionType.PersonalMediaRefused,
        SystemActionType.PersonalMediaStray,
    ];

    private static readonly string[] SystemTypes =
    [
        SystemActionType.ServerStarted,
        SystemActionType.ServerStopped,
        SystemActionType.MediaRemoved,
        SystemActionType.PersonalTrashEmptied,
        SystemActionType.PersonalMissingPurged,
        SystemActionType.ToolMissing,
        SystemActionType.ToolFound,
        SystemActionType.ToolDownloadStarted,
        SystemActionType.ToolDownloadFinished,
        SystemActionType.ToolDownloadFailed,
        SystemActionType.ToolDownloadChecksumRefused,
        SystemActionType.ToolDownloadCancelled,
        SystemActionType.ProviderDown,
        SystemActionType.ProviderRecovered,
        SystemActionType.ActivityPruned,
        SystemActionType.ReconciliationCompleted,
    ];

    private static readonly string[] AllTypes = [.. AddedTypes, .. ReviewTypes, .. FailedTypes, .. SystemTypes];

    /// <summary>The action types of one category. An empty, unknown or <c>all</c> category gives every shown type.</summary>
    public static IReadOnlyList<string> ActionTypesFor(string? category) =>
        category?.Trim().ToLowerInvariant() switch
        {
            ActivityLogCategoryValues.Added => AddedTypes,
            ActivityLogCategoryValues.Review => ReviewTypes,
            ActivityLogCategoryValues.Failed => FailedTypes,
            ActivityLogCategoryValues.System => SystemTypes,
            _ => AllTypes,
        };

    /// <summary>The category an action type belongs to, or null when it is not shown in the Activity log.</summary>
    public static string? CategoryOf(string actionType)
    {
        if (AddedTypes.Contains(actionType, StringComparer.Ordinal))
        {
            return ActivityLogCategoryValues.Added;
        }

        if (ReviewTypes.Contains(actionType, StringComparer.Ordinal))
        {
            return ActivityLogCategoryValues.Review;
        }

        if (FailedTypes.Contains(actionType, StringComparer.Ordinal))
        {
            return ActivityLogCategoryValues.Failed;
        }

        return SystemTypes.Contains(actionType, StringComparer.Ordinal) ? ActivityLogCategoryValues.System : null;
    }
}
