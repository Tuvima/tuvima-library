using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Lets a burst of Dashboard requests from one signed-in browser share a single Engine sign-in check. A page load
/// fetches dozens of scripts, styles and images, and each used to ask the Engine "is this sign-in still good?" on its own.
/// Only a check that came back valid is kept, and only for a few seconds. Anything that changes who is signed in
/// (<see cref="Clear"/>) empties it at once, and a failed check is never remembered, so the next request asks the Engine again.
/// </summary>
public sealed class SessionValidationCache(TimeProvider? clock = null, TimeSpan? lifetime = null)
{
    /// <summary>How long a valid check is shared. The 60 second screen re-check is the long backstop; this only absorbs one burst.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);

    private const int MaxEntries = 1024;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _lifetime = lifetime ?? DefaultLifetime;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _generation;

    /// <summary>
    /// The cache key for one sign-in seen from one place with one Dashboard credential. A different session token,
    /// ingress, or a rotated Dashboard credential never lands on another's entry. Hashed so tokens are not kept as keys.
    /// </summary>
    public static string KeyFor(string sessionToken, string ingress, string? serviceCredential) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\u001f', sessionToken, ingress, serviceCredential ?? string.Empty))));

    /// <summary>Forgets every remembered check, and stops any check already under way from being remembered or joined.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _entries.Clear();
        }
    }

    /// <summary>
    /// Returns the remembered valid check for <paramref name="key"/>, joins one already under way, or runs
    /// <paramref name="validate"/> once for everyone waiting. One caller giving up never cancels the others.
    /// </summary>
    public Task<(SessionValidationResponse? Response, bool Invalid)> GetOrValidateAsync(
        string key,
        Func<Task<(SessionValidationResponse? Response, bool Invalid)>> validate,
        CancellationToken ct)
    {
        Entry entry;
        var owner = false;
        long generation;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            generation = _generation;
            if (_entries.TryGetValue(key, out var existing) && !existing.IsExpired(now))
            {
                entry = existing;
            }
            else
            {
                if (_entries.Count >= MaxEntries)
                {
                    PruneExpired(now);
                }

                entry = new Entry();
                owner = true;
                // A full cache still shares the one check under way for this key's callers by never storing: it runs alone.
                if (_entries.Count < MaxEntries)
                {
                    _entries[key] = entry;
                }
            }
        }

        if (owner)
        {
            _ = RunAsync(key, entry, generation, validate);
        }

        return entry.Completion.Task.WaitAsync(ct);
    }

    private async Task RunAsync(
        string key,
        Entry entry,
        long generation,
        Func<Task<(SessionValidationResponse? Response, bool Invalid)>> validate)
    {
        try
        {
            var result = await validate().ConfigureAwait(false);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    var now = _clock.GetUtcNow();
                    // Only a valid check is kept, never past the session's own end, and never if anything changed meanwhile.
                    if (generation == _generation && result.Response is { } response && !result.Invalid
                        && Earliest(now + _lifetime, response.ExpiresAt) is var expires && expires > now)
                    {
                        entry.ExpiresAt = expires;
                    }
                    else
                    {
                        _entries.Remove(key);
                    }
                }
            }

            entry.Completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(key);
                }
            }

            entry.Completion.TrySetException(exception);
            // Every waiter may have walked away; observe the fault so it is not reported as unobserved.
            _ = entry.Completion.Task.Exception;
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var key in _entries.Where(pair => pair.Value.IsExpired(now)).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(key);
        }
    }

    private static DateTimeOffset Earliest(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private sealed class Entry
    {
        public TaskCompletionSource<(SessionValidationResponse? Response, bool Invalid)> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Null while the check is still under way (joinable); set once it came back valid.</summary>
        public DateTimeOffset? ExpiresAt { get; set; }

        public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && now >= at;
    }
}

/// <summary>
/// Empties <see cref="SessionValidationCache"/> around any Engine call that can change a sign-in or what a person may
/// do (sign out, switch profile, revoke a session or device, change access or password), so a changed sign-in is
/// never answered from memory. Reads leave it alone. It sits outside the credential handlers so it sees every call.
/// </summary>
public sealed class SessionValidationInvalidationHandler(SessionValidationCache cache) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!ChangesSignIn(request))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Before, so nothing started earlier is joined; after, so a check that raced the change is not kept.
        cache.Clear();
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cache.Clear();
        }
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!ChangesSignIn(request))
        {
            return base.Send(request, cancellationToken);
        }

        cache.Clear();
        try
        {
            return base.Send(request, cancellationToken);
        }
        finally
        {
            cache.Clear();
        }
    }

    private static bool ChangesSignIn(HttpRequestMessage request) =>
        request.Method != HttpMethod.Get
        && request.Method != HttpMethod.Head
        && request.Method != HttpMethod.Options
        // The check itself is a POST but changes nothing.
        && !string.Equals(PathOf(request.RequestUri), "/auth/session/validate", StringComparison.OrdinalIgnoreCase);

    private static string PathOf(Uri? uri) =>
        uri is null ? string.Empty
        : uri.IsAbsoluteUri ? uri.AbsolutePath
        : uri.OriginalString.Split('?')[0];
}
