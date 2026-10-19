namespace MediaEngine.Domain.Contracts;

/// <summary>A Settings attention line a profile dismissed, and what was true when it did.</summary>
/// <param name="NoticeKey">The notice, for example <c>needs-review</c>.</param>
/// <param name="Fingerprint">What was true at the time (the review count, or the sorted missing tool ids).</param>
public sealed record ProfileNoticeDismissal(string NoticeKey, string Fingerprint);

/// <summary>
/// Remembers which Settings attention lines a profile dismissed, so the choice follows the profile to any browser and
/// each line returns only when something new needs attention.
/// </summary>
public interface IProfileNoticeDismissalRepository
{
    /// <summary>The profile's dismissed notices, in key order.</summary>
    Task<IReadOnlyList<ProfileNoticeDismissal>> ListAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Records the dismissal. Dismissing again replaces the earlier fingerprint and time.</summary>
    Task DismissAsync(Guid profileId, string noticeKey, string fingerprint, DateTimeOffset dismissedAt, CancellationToken ct = default);
}
