namespace MediaEngine.Domain.Services;

public sealed record TvEpisodePlaybackCandidate(string Id, int? Season, int? Episode, double Progress, string? LastAccessed);
public enum TvEpisodeSelectionReason { Unstarted, Resume, NextOwned, RemainingOwned, AllOwnedCompleted, Empty }
public sealed record TvEpisodePlaybackContext(TvEpisodePlaybackCandidate? Target, TvEpisodeSelectionReason Reason, bool UsesEpisodeArtwork, bool HasGap);

/// <summary>One profile's automatic show context. Explicit episode selection bypasses this policy.</summary>
public static class TvEpisodeContextResolver
{
    public const double CompletionPercent = 99.5;

    /// <summary>
    /// The show's watch order: seasons ascending with specials (season 0) last, then episodes ascending, with an
    /// unnumbered season or episode after the numbered ones and the id as the final tie-break.
    /// </summary>
    public static IReadOnlyList<TvEpisodePlaybackCandidate> Order(IEnumerable<TvEpisodePlaybackCandidate> candidates)
        => candidates.OrderBy(e => e.Season == 0 ? int.MaxValue : e.Season ?? int.MaxValue - 1)
            .ThenBy(e => e.Episode ?? int.MaxValue).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

    public static TvEpisodePlaybackContext Resolve(IEnumerable<TvEpisodePlaybackCandidate> candidates)
    {
        var owned = Order(candidates).ToList();
        if (owned.Count == 0)
        {
            return new(null, TvEpisodeSelectionReason.Empty, false, false);
        }

        var resume = owned.Where(e => e.Progress > 0 && e.Progress < CompletionPercent)
            .OrderByDescending(e => e.LastAccessed, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).FirstOrDefault();
        if (resume is not null)
        {
            return new(resume, TvEpisodeSelectionReason.Resume, true, false);
        }

        var completed = owned.Where(e => e.Progress >= CompletionPercent)
            .OrderByDescending(e => e.LastAccessed, StringComparer.Ordinal).ThenByDescending(e => owned.IndexOf(e)).FirstOrDefault();
        if (completed is null)
        {
            return new(owned[0], TvEpisodeSelectionReason.Unstarted, false, false);
        }

        var next = owned.Skip(owned.IndexOf(completed) + 1).FirstOrDefault(e => e.Progress < CompletionPercent);
        if (next is not null)
        {
            return new(next, TvEpisodeSelectionReason.NextOwned, true,
            next.Season == completed.Season && next.Episode > completed.Episode + 1);
        }

        var remaining = owned.FirstOrDefault(e => e.Progress < CompletionPercent);
        return remaining is not null ? new(remaining, TvEpisodeSelectionReason.RemainingOwned, true, false)
            : new(owned[0], TvEpisodeSelectionReason.AllOwnedCompleted, false, false);
    }
}
