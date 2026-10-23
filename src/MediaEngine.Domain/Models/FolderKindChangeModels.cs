namespace MediaEngine.Domain.Models;

/// <summary>
/// The plan of one "Change what this folder is" job (Library setup spec D40(c), D60, D61, D69), written before anything changes so a job
/// that was stopped half way carries on from the same plan. <see cref="ConfigAppliedAt"/> is set once the folder has moved to the new
/// library and its first checks are settled; that step is never repeated. <see cref="RepairsOperationId"/> names the earlier change of the
/// folder (cancelled, or finished with files not changed) whose failed files this change retries.
/// </summary>
public sealed record FolderKindChangePlan(
    Guid OperationId,
    Guid SourceId,
    string FromLibraryId,
    string ToLibraryId,
    string FromKindKey,
    string ToKindKey,
    string TargetMediaType,
    bool TargetIsPlain,
    bool StartsFirstCheck,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfigAppliedAt = null,
    Guid? StartedByProfileId = null,
    Guid? RepairsOperationId = null);

/// <summary>Where one file sits in the Work graph: its Edition, the Work that edition belongs to and that Work's media type.</summary>
public sealed record AssetWorkPlacement(Guid AssetId, Guid EditionId, Guid WorkId, string WorkMediaType, string? LibraryId);

/// <summary>
/// What re-placing a file under a Work of another media type did. <see cref="Replaced"/> is false when the file's Work already has that
/// media type and was left where it is. <see cref="OwnedFilesToRemove"/> are files Tuvima wrote for titles that were removed (artwork), for
/// the caller to delete after the change is committed.
/// </summary>
public sealed record AssetReplacement(bool Replaced, Guid WorkId, IReadOnlyList<string> OwnedFilesToRemove);

/// <summary>
/// What dropping the details looked up for one file removed (Library setup D69): the entities whose looked-up claims, outside ids and
/// provider artwork links were dropped, and how many claims went. Claims from the file itself and a person's own edits are never dropped.
/// </summary>
public sealed record DroppedProviderDetails(IReadOnlyList<Guid> EntityIds, int ClaimsDropped, IReadOnlyList<string>? OwnedFilesToRemove = null);

/// <summary>
/// A file is not placed again because its title and the title it would join both hold artwork or details a person made, and one cannot be
/// carried over the other (Library setup D69). Nothing was changed. The job reports the file and starting the change again refuses it again
/// without looping.
/// </summary>
public sealed class UserDetailsClashException(string first, string second) : InvalidOperationException(Describe(first, second))
{
    /// <summary>The sentence the ledger, the job's message and the Dashboard show: both titles, and what to do.</summary>
    public static string Describe(string first, string second) =>
        $"\"{first}\" and \"{second}\" both have your own artwork or details. Remove them from one of the two in Editing Mode, then start the change again.";

    public string FirstTitle { get; } = first;

    public string SecondTitle { get; } = second;
}
