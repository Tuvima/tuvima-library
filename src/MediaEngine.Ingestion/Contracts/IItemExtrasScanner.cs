using MediaEngine.Domain.Models;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Finds the Extras beside titles (Extras spec WP1) and keeps their store up to date. The Extras rule lives in
/// <c>ExtrasRule</c>; this service applies it to the libraries and folders Library reads.
/// </summary>
public interface IItemExtrasScanner
{
    /// <summary>
    /// True when this file is an Extra of a title: the ingestion pipeline then leaves it alone (it never becomes an episode, a
    /// Work or a Needs Review item). The title's folder is queued for a re-check so the Extra is stored shortly after.
    /// A file in a Vault library is never an Extra here.
    /// </summary>
    Task<bool> ClaimsFileAsync(string filePath, LibraryFolderEntry? library, CancellationToken ct = default);

    /// <summary>Queues the folder of a file for a re-check (a picture or a PDF the file watcher saw appear, change or go).</summary>
    void NoteFile(string filePath);

    /// <summary>Re-checks every queued title folder. Returns how many were checked.</summary>
    Task<int> SyncQueuedAsync(CancellationToken ct = default);

    /// <summary>Re-checks every title folder of every library (startup and the daily pass). Returns how many were checked.</summary>
    Task<int> SyncAllAsync(CancellationToken ct = default);

    /// <summary>Re-checks one title folder now. Returns false when the folder is in no library Library reads.</summary>
    Task<bool> SyncItemFolderAsync(string itemFolder, CancellationToken ct = default);

    /// <summary>
    /// Describes one file as an Extra a person decided on (<paramref name="origin"/> is <c>user_extra</c> or <c>user_episode</c>): its title
    /// folder, kind, title, size and facts. Null when the file is in no title folder of a library Library reads, or in a Vault library.
    /// </summary>
    Task<ItemExtra?> DescribeAsync(string filePath, string origin, int? targetSeason, int? targetEpisode, CancellationToken ct = default);

    /// <summary>
    /// Where a person put this file when they moved it from Extras to an episode (WP4), or null when they did not. The ingestion
    /// pipeline reads it so the file is identified as that season and episode of its show.
    /// </summary>
    Task<ExtraEpisodePlacement?> GetEpisodePlacementAsync(string filePath, CancellationToken ct = default);
}

/// <summary>The show, season and episode a person put a file in when they moved it from Extras to an episode.</summary>
public sealed record ExtraEpisodePlacement(string ShowName, string? Year, int Season, int Episode);
