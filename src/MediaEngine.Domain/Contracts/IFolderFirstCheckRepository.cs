using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The first check of a newly added folder (Library setup spec D35, D61): the safety break that pauses a folder when most of its first
/// files do not match, and the hold that keeps the folder untouched on disk until the check passes. One row per folder (source). Files
/// are matched to a folder by their path, so a file that moves keeps counting for the folder it was first seen in.
/// </summary>
public interface IFolderFirstCheckRepository
{
    /// <summary>Starts a check for a folder that has none. Returns false when the folder already has one (a check never restarts by itself).</summary>
    Task<bool> StartAsync(string libraryId, string sourceId, string path, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>One folder's check, or null.</summary>
    Task<FolderFirstCheck?> GetAsync(string sourceId, CancellationToken ct = default);

    /// <summary>Every check that is still checking or paused, with the live counts.</summary>
    Task<IReadOnlyList<FolderFirstCheckSnapshot>> ListOpenAsync(CancellationToken ct = default);

    /// <summary>Every check that is still checking (not yet paused or ended), as the stored rows. Cheap enough to ask often.</summary>
    Task<IReadOnlyList<FolderFirstCheck>> ListCheckingAsync(CancellationToken ct = default);

    /// <summary>What the check has seen so far: the outcomes of the first <paramref name="firstFiles"/> files and what is still waiting.</summary>
    Task<FolderFirstCheckProgress> GetProgressAsync(string sourceId, int firstFiles, CancellationToken ct = default);

    /// <summary>
    /// Records a file's final identification outcome for the folder it is in, when that folder is being checked. A file counts once (a later
    /// outcome for the same file replaces the earlier one). Does nothing for a file in no checking folder.
    /// </summary>
    Task RecordOutcomeAsync(Guid assetId, string outcome, CancellationToken ct = default);

    /// <summary>
    /// The check passes: its state becomes passed and the folder's held filing rows become waiting, to be filed in the next run with their
    /// tags. Returns false when the check was not checking.
    /// </summary>
    Task<bool> PassAsync(string sourceId, int tried, int unmatched, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The check pauses the folder: its remaining identity jobs are not claimed and the held rows stay held. Returns false when the check was not checking.</summary>
    Task<bool> PauseAsync(string sourceId, int tried, int unmatched, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Keep going on a paused folder: the check ends as kept going, identification resumes and the held rows of files that are not Unmatched
    /// become waiting. Returns false when the check was not paused.
    /// </summary>
    Task<bool> KeepGoingAsync(string sourceId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Removes a folder's check and its held filing rows (the folder was removed, or its kind changed and the check is to start over).
    /// No file is moved. Returns true when a check existed.
    /// </summary>
    Task<bool> DropAsync(string sourceId, CancellationToken ct = default);

    /// <summary>True when files filed into the folder must wait: its check is checking or paused.</summary>
    bool IsHeld(string sourceId);
}
