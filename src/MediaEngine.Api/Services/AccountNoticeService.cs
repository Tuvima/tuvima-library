using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// The signed-in account's "Not now" choices for one-time reminders. Every call takes the account from the caller's
/// own authority (never from the request), so one account can neither read nor change another's choices.
/// </summary>
public sealed class AccountNoticeService(IAccountNoticeDismissalRepository dismissals, TimeProvider clock)
{
    /// <summary>The known reminder keys this account has dismissed.</summary>
    public async Task<AccountNoticesResponse> ListAsync(Guid accountId, CancellationToken ct)
    {
        var keys = await dismissals.ListDismissedAsync(accountId, ct).ConfigureAwait(false);
        return new AccountNoticesResponse(keys.Where(AccountNoticeKeys.IsKnown).ToList());
    }

    /// <summary>Remembers "Not now" for a known reminder. Returns false, saving nothing, for an unknown key.</summary>
    public async Task<bool> DismissAsync(Guid accountId, string noticeKey, CancellationToken ct)
    {
        if (!AccountNoticeKeys.IsKnown(noticeKey))
        {
            return false;
        }

        await dismissals.DismissAsync(accountId, noticeKey, clock.GetUtcNow(), ct).ConfigureAwait(false);
        return true;
    }
}
