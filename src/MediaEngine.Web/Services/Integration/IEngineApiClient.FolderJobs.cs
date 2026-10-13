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

    /// <summary>GET /settings/libraries/folders/jobs - open folder jobs and each folder's latest finished job.</summary>
    Task<IReadOnlyList<FolderJobStatusDto>> GetFolderJobsAsync(CancellationToken ct = default);

    /// <summary>POST /settings/libraries/folders/jobs/{id}/control - pause, resume or cancel a folder job.</summary>
    Task<FolderJobStatusDto?> ControlFolderJobAsync(string operationId, string action, CancellationToken ct = default);
}
