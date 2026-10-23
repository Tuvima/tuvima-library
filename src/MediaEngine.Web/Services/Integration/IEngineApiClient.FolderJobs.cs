using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>POST /settings/libraries/folders/conversion/preview - counts a folder's media files, or previews converting it to Managed.</summary>
    Task<FolderConversionPreviewResponse?> PreviewFolderConversionAsync(FolderConversionPreviewRequest request, CancellationToken ct = default);

    /// <summary>POST /settings/libraries/folders/convert - makes the folder Managed and queues the organising job.</summary>
    Task<FolderJobStatusDto?> ConvertFolderAsync(string sourceId, CancellationToken ct = default);

    /// <summary>POST /settings/libraries/folders/merge/preview - counts the files a merge would bring in and the room it needs, or previews it.</summary>
    Task<FolderMergePreviewResponse?> PreviewFolderMergeAsync(FolderMergePreviewRequest request, CancellationToken ct = default);

    /// <summary>POST /settings/libraries/folders/merge - queues the job that merges one folder, once, into a managed folder.</summary>
    Task<FolderJobStatusDto?> MergeFolderAsync(FolderMergeRequest request, CancellationToken ct = default);

    /// <summary>
    /// POST /settings/libraries/folders/{sourceId}/change-kind/preview - what changing what the folder is would do (Library setup D40(c), D60).
    /// A refusal the Engine explained (a Vault, the same kind, another area, a job already running) is <see cref="FolderKindChangeResult{T}.Refusal"/>;
    /// anything else that went wrong (unreachable, timed out, not allowed) is <see cref="FolderKindChangeResult{T}.Failure"/>.
    /// </summary>
    Task<FolderKindChangeResult<FolderKindChangePreviewResponse>> PreviewFolderKindChangeAsync(string sourceId, FolderKindChangeRequest request, CancellationToken ct = default);

    /// <summary>POST /settings/libraries/folders/{sourceId}/change-kind - queues the job that changes what the folder is. Refusals are told from failures as for the preview.</summary>
    Task<FolderKindChangeResult<FolderJobStatusDto>> ChangeFolderKindAsync(string sourceId, FolderKindChangeRequest request, CancellationToken ct = default);

    /// <summary>GET /settings/libraries/folders/jobs - open folder jobs and each folder's latest finished job.</summary>
    Task<IReadOnlyList<FolderJobStatusDto>> GetFolderJobsAsync(CancellationToken ct = default);

    /// <summary>POST /settings/libraries/folders/jobs/{id}/control - pause, resume or cancel a folder job.</summary>
    Task<FolderJobStatusDto?> ControlFolderJobAsync(string operationId, string action, CancellationToken ct = default);
}

/// <summary>
/// The answer to a change-of-kind action: the value, or why there is none. <see cref="Refusal"/> is a plain sentence the Engine gave
/// (show it as it is); <see cref="Failure"/> means the question was not answered at all (the Engine could not be reached, or it failed), which a
/// caller must never read as a refusal or as "nothing to worry about".
/// </summary>
public sealed record FolderKindChangeResult<T>(T? Value, string? Refusal = null, string? Failure = null)
{
    public bool Succeeded => Value is not null && Refusal is null && Failure is null;
}
