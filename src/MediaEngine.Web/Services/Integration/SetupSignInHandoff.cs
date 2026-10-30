using System.Collections.Concurrent;
using System.Security.Cryptography;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Lets setup carry on signed in without leaving its page. When the first administrator is created, the Engine's
/// sign-in for that person stays on the Dashboard's server and only an unguessable one-time code reaches the browser; the
/// page's own script redeems it once at <c>/auth/setup-handoff</c>, which sets the sign-in cookie. A code works once, for two
/// minutes, so a copy that leaks (a log, a screenshot) is useless.
/// </summary>
public sealed class SetupSignInHandoff(TimeProvider? time = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (AuthSessionResponse Session, DateTimeOffset ExpiresAt)> _pending = new(StringComparer.Ordinal);

    /// <summary>Holds <paramref name="session"/> and returns the code that redeems it once.</summary>
    public string Issue(AuthSessionResponse session)
    {
        Prune();
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _pending[code] = (session, _time.GetUtcNow().Add(Lifetime));
        return code;
    }

    /// <summary>The held sign-in for <paramref name="code"/>, removed so it cannot be used again; null when unknown, used or expired.</summary>
    public AuthSessionResponse? Redeem(string? code)
    {
        if (string.IsNullOrEmpty(code) || !_pending.TryRemove(code, out var held))
        {
            return null;
        }

        return held.ExpiresAt > _time.GetUtcNow() ? held.Session : null;
    }

    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var entry in _pending.Where(pair => pair.Value.ExpiresAt <= now))
        {
            _pending.TryRemove(entry.Key, out _);
        }
    }
}
