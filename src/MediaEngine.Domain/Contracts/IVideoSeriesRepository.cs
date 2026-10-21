namespace MediaEngine.Domain.Contracts;

/// <summary>A place a standalone video can join: a series with no sections, or one section of a series.</summary>
public sealed record VideoSeriesTarget(Guid ParentWorkId, string Title, int VideoCount);

/// <summary>Where a video sits now. <see cref="ParentWorkId"/> is null when the video stands alone.</summary>
public sealed record VideoSeriesMembership(Guid WorkId, Guid AssetId, Guid? ParentWorkId, string? SeriesTitle);

public enum VideoSeriesOutcome
{
    Done,

    /// <summary>The work is not a video (or does not exist).</summary>
    NotAVideo,

    /// <summary>Promote was asked for a video that already belongs to a series.</summary>
    AlreadyInSeries,

    /// <summary>Take-out was asked for a video that stands alone.</summary>
    NotInSeries,

    /// <summary>The chosen series or section is gone, or is not a place videos can join.</summary>
    TargetUnavailable,

    /// <summary>A new series needs a name.</summary>
    NameRequired,

    /// <summary>The change clashed with another writer (a position or series name taken first). Nothing was saved; asking again works.</summary>
    Conflict,
}

/// <summary>
/// What a promote or take-out did. <see cref="CreatedSeries"/> is true when a new series was started, so the caller can give
/// it its title.
/// </summary>
public sealed record VideoSeriesChange(
    VideoSeriesOutcome Outcome,
    Guid AssetId = default,
    Guid? ParentWorkId = null,
    string? SeriesTitle = null,
    int Position = 0,
    bool CreatedSeries = false);

/// <summary>
/// Moves a video into a series, or out of one, in Library's own data. Nothing on disk moves: the series is how Library shows
/// the videos together, never a folder. The move and the locked series fields that go with it are saved together or not at all.
/// </summary>
public interface IVideoSeriesRepository
{
    Task<VideoSeriesMembership?> GetMembershipAsync(Guid workId, CancellationToken ct = default);

    /// <summary>Every place the video can join in its own library, series name first, then by name.</summary>
    Task<IReadOnlyList<VideoSeriesTarget>> ListTargetsAsync(Guid workId, CancellationToken ct = default);

    /// <summary>Puts a standalone video last in the chosen series (or section).</summary>
    Task<VideoSeriesChange> PromoteAsync(Guid workId, Guid parentWorkId, CancellationToken ct = default);

    /// <summary>Starts a series with the given name (or joins the one that already has it) and puts a standalone video in it.</summary>
    Task<VideoSeriesChange> PromoteToNewSeriesAsync(Guid workId, string seriesName, CancellationToken ct = default);

    /// <summary>Makes a video stand alone again. The other videos keep their order.</summary>
    Task<VideoSeriesChange> RemoveAsync(Guid workId, CancellationToken ct = default);
}
