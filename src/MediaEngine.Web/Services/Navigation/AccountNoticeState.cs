using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The signed-in account's "Not now" choices for one-time reminders, remembered by the Engine with the account so
/// they follow the person to any browser. Shared by the profile menu card, the Settings menu and Settings &gt; Security.
/// </summary>
public sealed class AccountNoticeState(DashboardIdentityClient identity, ILogger<AccountNoticeState>? logger = null)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private Task? _loading;
    private bool _loaded;

    /// <summary>Raised after a notice is dismissed or the dismissed list is loaded.</summary>
    public event Action? Changed;

    public bool IsDismissed(string noticeKey)
    {
        lock (_gate)
        {
            return _dismissed.Contains(noticeKey);
        }
    }

    /// <summary>
    /// Loads the account's dismissed reminders once. Callers that arrive while a load is running share it. If the
    /// Engine cannot answer, nothing counts as dismissed and the next caller asks again.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return Task.CompletedTask;
            }

            _loading ??= LoadAsync(ct);
            return _loading;
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var notices = await identity.GetAccountNoticesAsync(ct).ConfigureAwait(false);
            if (notices is null)
            {
                return;
            }

            lock (_gate)
            {
                _loaded = true;
                foreach (var key in notices.Dismissed ?? [])
                {
                    _dismissed.Add(key);
                }
            }

            Changed?.Invoke();
        }
        finally
        {
            lock (_gate)
            {
                _loading = null;
            }
        }
    }

    /// <summary>
    /// Hides the reminder straight away and asks the Engine to remember it. If the Engine cannot save the choice the
    /// reminder stays hidden for this visit only, and returns next time.
    /// </summary>
    public async Task DismissAsync(string noticeKey, CancellationToken ct = default)
    {
        bool added;
        lock (_gate)
        {
            added = _dismissed.Add(noticeKey);
        }

        if (added)
        {
            Changed?.Invoke();
        }

        var result = await identity.DismissAccountNoticeAsync(noticeKey, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            logger?.LogWarning("The Engine could not remember dismissing the {NoticeKey} reminder: {Failure}", noticeKey, result.Failure);
        }
    }
}
