using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Domain.Contracts;

/// <summary>Stores Tidy this folder runs, their rename ledger and the folders tidied every night (Photo and video filing spec D17).</summary>
public interface IViewTidyRepository
{
    /// <summary>Records a new running job with its planned renames in one step, so a stop never leaves a job without its ledger.</summary>
    Task CreateJobAsync(ViewTidyJob job, IReadOnlyList<ViewTidyMove> moves, Guid? startedBy, CancellationToken ct = default);

    Task<IReadOnlyList<ViewTidyJob>> GetRunningJobsAsync(CancellationToken ct = default);

    Task<ViewTidyJob?> GetLatestJobAsync(Guid sourceId, CancellationToken ct = default);

    Task<IReadOnlyList<ViewTidyMove>> GetPlannedMovesAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Marks one rename done and points the file's location at its new path, together.</summary>
    Task CompleteMoveAsync(Guid jobId, Guid sourceFileId, string toPath, CancellationToken ct = default);

    Task FailMoveAsync(Guid jobId, Guid sourceFileId, string reason, CancellationToken ct = default);

    Task FinishJobAsync(Guid jobId, string state, string message, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>True while a running job belongs to a folder of this library.</summary>
    Task<bool> IsLibraryBeingTidiedAsync(Guid libraryId, CancellationToken ct = default);

    Task<IReadOnlyList<ViewTidySourceFile>> GetSourceFilesAsync(Guid sourceId, CancellationToken ct = default);

    Task SetScheduledAsync(Guid sourceId, bool scheduled, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetScheduledSourceIdsAsync(CancellationToken ct = default);

    Task<bool> IsScheduledAsync(Guid sourceId, CancellationToken ct = default);
}
