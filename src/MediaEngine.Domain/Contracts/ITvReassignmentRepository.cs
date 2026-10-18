using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Where a whole-series file (for example a miniseries stored as one film file) is filed under its
/// TV show when its parts cannot be matched to the show's episodes: Season 0 (Specials), Episode 1.
/// </summary>
public static class TvSpecialPlacement
{
    public const int SeasonNumber = 0;
    public const int EpisodeNumber = 1;
}

/// <summary>
/// Moves an owned media asset between Work hierarchies as a logical change only: the file on disk is
/// never read, moved, renamed or written.
/// </summary>
public interface ITvReassignmentRepository
{
    /// <summary>
    /// Re-files a Movies asset as Season 0, Episode 1 (Specials) of the TV show
    /// <paramref name="showName"/>: finds or creates the show, its Season 0 and the episode Work, moves
    /// the asset's Edition under that episode, and removes the old standalone movie Work (and the data
    /// that belonged only to it) once it holds no Edition. Idempotent when the asset is already there.
    /// </summary>
    /// <param name="assetId">The media asset to re-file.</param>
    /// <param name="showName">The TV show to file it under.</param>
    /// <param name="episodeTitle">Title of the episode Work, normally the file's own title.</param>
    /// <exception cref="InvalidOperationException">The asset is unknown, or is not currently a Movies asset.</exception>
    Task<TvReassignment> ReassignToTvShowSpecialAsync(
        Guid assetId,
        string showName,
        string? episodeTitle,
        CancellationToken ct = default);

    /// <summary>
    /// The whole "Move to TV" decision as one all-or-nothing change: finds or creates the show, the
    /// requested season and its episode Works, re-parents the asset's Edition under the first episode,
    /// records every further episode as covered by the asset (manual coverage), removes the empty movie
    /// Work, marks the covered episodes and TV containers owned, moves the asset to the TV library,
    /// writes the decision's claims, canonical values and bridge ids, and resolves the review item.
    /// If any step fails, nothing is written and the review item stays pending. The file is never touched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The asset is unknown or not a Movies asset, or the review item is no longer pending.
    /// </exception>
    Task<TvReassignment> MoveToTvShowAsync(
        TvMoveRequest request,
        CancellationToken ct = default);
}

/// <summary>Everything one "Move to TV" decision changes, committed together.</summary>
/// <param name="AssetId">The media asset to re-file.</param>
/// <param name="ShowName">The TV show to file it under.</param>
/// <param name="EpisodeTitle">Title of the episode Work, normally the file's own title.</param>
/// <param name="SeasonNumber">The season to file it under (0 is Specials).</param>
/// <param name="EpisodeNumbers">
/// The episodes the file covers, in episode order. The first is the host episode that holds the
/// Edition; any further ones are recorded as coverage.
/// </param>
/// <param name="LibraryId">The TV library the asset moves into (data only).</param>
/// <param name="ReviewItemId">The pending review item resolved by the move.</param>
/// <param name="ResolvedBy">Who made the decision.</param>
/// <param name="BuildDecision">
/// Builds the claims, canonical values and bridge ids to record once the Work ids are known.
/// Pure: it runs inside the transaction and must not do I/O.
/// </param>
public sealed record TvMoveRequest(
    Guid AssetId,
    string ShowName,
    string? EpisodeTitle,
    int SeasonNumber,
    IReadOnlyList<int> EpisodeNumbers,
    string LibraryId,
    Guid ReviewItemId,
    string ResolvedBy,
    Func<TvReassignment, TvMoveDecision> BuildDecision);

/// <summary>The records written for a "Move to TV" decision.</summary>
public sealed record TvMoveDecision(
    IReadOnlyList<MetadataClaim> Claims,
    IReadOnlyList<CanonicalValue> Values,
    IReadOnlyList<BridgeIdEntry> BridgeIds);

/// <summary>
/// Outcome of <see cref="ITvReassignmentRepository.ReassignToTvShowSpecialAsync"/> and
/// <see cref="ITvReassignmentRepository.MoveToTvShowAsync"/>.
/// </summary>
/// <param name="CoveredEpisodeWorkIds">Every episode Work the file covers, in episode order, host first.</param>
public sealed record TvReassignment(
    Guid AssetId,
    Guid EditionId,
    Guid PreviousWorkId,
    bool PreviousWorkRemoved,
    Guid ShowWorkId,
    Guid SeasonWorkId,
    Guid EpisodeWorkId,
    IReadOnlyList<Guid> CoveredEpisodeWorkIds);
