using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// The active profile's dismissed Settings attention lines. Every call takes the profile from the caller's own
/// authority (never from the request), so one profile can neither read nor change another's choices.
/// </summary>
public sealed class ProfileNoticeService(IProfileNoticeDismissalRepository dismissals, TimeProvider clock)
{
    /// <summary>The longest fingerprint accepted: a handful of tool ids or a count is far shorter.</summary>
    internal const int MaxFingerprintLength = 512;

    /// <summary>The known notices this profile has dismissed, with what was true at the time.</summary>
    public async Task<ProfileNoticesResponse> ListAsync(Guid profileId, CancellationToken ct)
    {
        var rows = await dismissals.ListAsync(profileId, ct).ConfigureAwait(false);
        return new ProfileNoticesResponse(rows
            .Where(row => ProfileNoticeKeys.IsKnown(row.NoticeKey))
            .Select(row => new ProfileNoticeDismissalDto(row.NoticeKey, row.Fingerprint))
            .ToList());
    }

    /// <summary>Remembers a dismissal. Returns false, saving nothing, for an unknown key or an unusable fingerprint.</summary>
    public async Task<bool> DismissAsync(Guid profileId, string noticeKey, string? fingerprint, CancellationToken ct)
    {
        if (!ProfileNoticeKeys.IsKnown(noticeKey)
            || string.IsNullOrWhiteSpace(fingerprint)
            || fingerprint.Length > MaxFingerprintLength)
        {
            return false;
        }

        await dismissals.DismissAsync(profileId, noticeKey, fingerprint.Trim(), clock.GetUtcNow(), ct).ConfigureAwait(false);
        return true;
    }
}
