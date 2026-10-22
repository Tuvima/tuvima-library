using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Moves a staged media asset into the organised library structure.
///
/// Called by the hydration pipeline after re-scoring lifts an entity's
/// confidence above the auto-organize threshold (0.85). The implementation
/// loads canonical values, calculates the destination path, moves the file,
/// and writes sidecar XML.
///
/// Implementations live in <c>MediaEngine.Ingestion</c>.
/// </summary>
public interface IAutoOrganizeService
{
    /// <summary>
    /// Attempts to organize the asset identified by <paramref name="assetId"/>
    /// from the staging directory into the library. No-op if the asset is
    /// already in the library or if configuration is incomplete.
    /// </summary>
    Task TryAutoOrganizeAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// Works out, without touching anything, what re-filing an asset that is already in a library folder would do: the name and place
    /// the naming policy gives it, and whether the file may be renamed and moved (As-is and read-only folders, protected originals
    /// and taken names say no). With <paramref name="placement"/> the answer is for where the item would sit after a library move;
    /// without it, for where the library places it now.
    /// </summary>
    Task<AssetRefilePlan> PlanRefileAsync(Guid assetId, AssetRefilePlacement? placement, CancellationToken ct = default);

    /// <summary>
    /// Renames and moves the asset's file to fit where the library now places it (after a library move), through the same path as
    /// tidying a filed asset: the plan is made again from the library as it is now, the shared mutation policy is asked again, and
    /// the file, its companion files and the stored path follow. A file the policy refuses, or whose new name is taken, stays where
    /// it is and the reason is recorded in the activity history. Nothing is ever overwritten.
    /// </summary>
    Task<AssetRefileOutcome> RefileAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="RefileAsync(Guid, CancellationToken)"/>, telling <paramref name="steps"/> about each step before and after it
    /// happens, so a stop part-way can be finished or undone from what is on disk. Implementations that do not move files simply
    /// re-file without recording steps.
    /// </summary>
    Task<AssetRefileOutcome> RefileAsync(Guid assetId, IRefileStepRecorder steps, CancellationToken ct = default) =>
        RefileAsync(assetId, ct);

    /// <summary>
    /// Files an asset that Library queued for the next run (Library setup D62): a newly identified file in a Managed folder is renamed
    /// and moved to fit its kind's pattern, through the same step-recorded path as <see cref="RefileAsync(Guid, IRefileStepRecorder, CancellationToken)"/>;
    /// a file in the Import folder is filed out of it into its type's Managed folder. A review opened since the file was queued, a
    /// protected file or a taken name leave the file where it is. Implementations that do not queue filing simply re-file.
    /// </summary>
    Task<AssetRefileOutcome> FileOnScheduleAsync(Guid assetId, IRefileStepRecorder steps, CancellationToken ct = default) =>
        RefileAsync(assetId, steps, ct);

    /// <summary>
    /// Finishes a move that was cut short after the file reached <paramref name="targetPath"/>: the stored path, the files that travel
    /// with it and the folders it left follow, exactly as they would have. Does nothing when the file is not there.
    /// </summary>
    Task CompleteInterruptedMoveAsync(Guid assetId, string sourcePath, string targetPath, CancellationToken ct = default) =>
        Task.CompletedTask;
}
