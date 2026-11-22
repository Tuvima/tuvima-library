using System.Collections.Concurrent;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// Watches password sign-ins for the two things an administrator wants to hear about: a device an account has not signed in
/// from before, and a run of failed attempts. It only reports; it never blocks a sign-in or changes how one works.
/// </summary>
public sealed class SignInWatcher(
    IFirstPartyIdentityService identity,
    INotificationPublisher notifications,
    TimeProvider clock,
    ILogger<SignInWatcher> logger)
{
    /// <summary>Failed attempts inside <see cref="Window"/> that count as "several in a row".</summary>
    public const int FailuresBeforeAlert = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A sign-in that worked: reports the device when it is the first time this account has used it.</summary>
    public async Task SucceededAsync(SessionIssueResult issued, CancellationToken ct)
    {
        try
        {
            _failures.TryRemove(issued.Account.Email ?? string.Empty, out _);
            var session = issued.Session;
            var sessions = await identity.GetSessionsAsync(session.AccountId, ct).ConfigureAwait(false);

            // The very first session an account ever has is its set-up, not a surprise.
            var others = sessions.Where(s => s.Id != session.Id).ToList();
            if (others.Count == 0 || string.IsNullOrWhiteSpace(session.DeviceId))
            {
                return;
            }

            if (others.Any(s => string.Equals(s.DeviceId, session.DeviceId, StringComparison.Ordinal)))
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(session.DeviceName) ? "a new device" : session.DeviceName;
            notifications.Publish(new NotificationRequest(
                NotificationEventIds.AccountNewDevice,
                session.DeviceId,
                $"{issued.Account.Email} signed in from {name}. If that was not them, sign other devices out under their account."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reporting is best effort: a failure here must never turn a good sign-in into an error.
            logger.LogDebug(ex, "Could not check whether a sign-in came from a new device");
        }
    }

    /// <summary>A sign-in that failed: reports once when several fail close together for the same address.</summary>
    public void Failed(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var now = clock.GetUtcNow();
        var queue = _failures.GetOrAdd(email.Trim(), _ => new Queue<DateTimeOffset>());
        var alert = false;
        lock (queue)
        {
            queue.Enqueue(now);
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= FailuresBeforeAlert)
            {
                queue.Clear();
                alert = true;
            }
        }

        if (alert)
        {
            notifications.Publish(new NotificationRequest(
                NotificationEventIds.SecurityFailedSignIns,
                email.Trim().ToLowerInvariant(),
                $"{FailuresBeforeAlert} sign-in attempts failed in a row for {email.Trim()}. It may be a mistyped password or someone guessing."));
        }
    }
}
