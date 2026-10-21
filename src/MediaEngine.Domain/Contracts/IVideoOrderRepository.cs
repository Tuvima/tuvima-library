namespace MediaEngine.Domain.Contracts;

/// <summary>One video in a series (or a section of one), in the order it is shown.</summary>
public sealed record VideoOrderEntry(Guid WorkId, Guid AssetId, string Title);

/// <summary>The videos that share one parent: the series itself, or one of its sections.</summary>
public sealed record VideoOrderGroup(Guid ParentWorkId, string Title, IReadOnlyList<VideoOrderEntry> Entries);

/// <summary>Where a video landed after a hand reorder (1 is first).</summary>
public sealed record VideoOrderPlacement(Guid WorkId, Guid AssetId, int Position);

/// <summary>
/// Reads and saves the hand order of the videos in a series. The order is kept in Tuvima's own data, never in the files'
/// names, so a re-scan leaves it alone.
/// </summary>
public interface IVideoOrderRepository
{
    /// <summary>
    /// The groups of videos for a series, given the series or any video inside it. A series without sections is one group; a
    /// series with sections has one group per section. Empty when the work is not part of a Videos series.
    /// </summary>
    Task<IReadOnlyList<VideoOrderGroup>> LoadAsync(Guid workId, CancellationToken ct = default);

    /// <summary>
    /// Puts the children of <paramref name="parentWorkId"/> in the order given. Refused (null) unless the list names exactly the
    /// videos that are there now, so a stale screen can never drop or invent one.
    /// </summary>
    Task<IReadOnlyList<VideoOrderPlacement>?> ReorderAsync(Guid parentWorkId, IReadOnlyList<Guid> orderedWorkIds, CancellationToken ct = default);
}
