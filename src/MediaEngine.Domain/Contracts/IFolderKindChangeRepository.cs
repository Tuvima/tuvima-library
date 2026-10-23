using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The data side of "Change what this folder is" (Library setup spec D40(c), D60, D64, D69): the plan of a change job (one row per job,
/// written before anything changes) and the two changes made to a file's data. Each file change is one write transaction and can be repeated
/// safely, so a job that was stopped half way finds a file in either state and carries on.
/// </summary>
public interface IFolderKindChangeRepository
{
    /// <summary>Writes the plan. A job's plan is written once; writing the same operation again changes nothing.</summary>
    Task SavePlanAsync(FolderKindChangePlan plan, CancellationToken ct = default);

    Task<FolderKindChangePlan?> GetPlanAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Every change job planned for the folder, oldest first: the ledgers to read to find files an earlier run left unfinished.</summary>
    Task<IReadOnlyList<Guid>> ListPlanOperationIdsAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>Removes the places (shows, seasons, albums, series) a stopped placement left empty, by the rule every move uses. Safe to repeat.</summary>
    Task TidyEmptyPlacesAsync(CancellationToken ct = default);

    /// <summary>The most recent plan made for the folder, or null: how a change that was stopped half way is found again.</summary>
    Task<FolderKindChangePlan?> GetLatestPlanAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>Records that the folder has moved to the new library and its first checks are settled. Returns false when the plan is unknown.</summary>
    Task<bool> MarkConfigAppliedAsync(Guid operationId, DateTimeOffset at, CancellationToken ct = default);

    /// <summary>Where the file sits now, or null when the file is not in the data store.</summary>
    Task<AssetWorkPlacement?> GetPlacementAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// Files the file under a Work of <paramref name="mediaType"/>, the way ingestion places a new file: the hierarchy is resolved from
    /// <paramref name="metadata"/> (a show, album or series folder is found or made), the file's Edition moves under the resolved Work, the
    /// file's media type is recorded, what people did with the old title (plays, hearts, bookmarks, queue places, tags) is carried to the new
    /// one, the details a person set on the old title follow the file, and the old title (and any parent it leaves empty) is removed. Nothing
    /// happens when the file's Work already has that media type.
    /// </summary>
    Task<AssetReplacement> ReplaceUnderMediaTypeAsync(
        Guid assetId,
        Enums.MediaType mediaType,
        IReadOnlyDictionary<string, string> metadata,
        string? sourceFilePath,
        CancellationToken ct = default);

    /// <summary>
    /// Drops the details looked up for the file and for the Work it sits in: claims from providers, outside ids (bridge ids, the Wikidata id,
    /// external identifiers) and provider artwork. Claims from the file itself, a person's own edits and personal state stay. The canonical
    /// values of the keys whose claims went are removed, for the caller to work out again from the claims that remain.
    /// </summary>
    Task<DroppedProviderDetails> DropProviderDetailsAsync(Guid assetId, CancellationToken ct = default);
}
