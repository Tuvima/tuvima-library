namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Remembers which one-time reminders an account chose "Not now" for, so the choice follows the person to any browser.
/// </summary>
public interface IAccountNoticeDismissalRepository
{
    /// <summary>The notice keys the account has dismissed, in key order.</summary>
    Task<IReadOnlyList<string>> ListDismissedAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Records that the account dismissed the notice. Dismissing twice keeps the first time.</summary>
    Task DismissAsync(Guid accountId, string noticeKey, DateTimeOffset dismissedAt, CancellationToken ct = default);
}
