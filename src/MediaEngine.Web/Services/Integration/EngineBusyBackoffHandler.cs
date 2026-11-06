using System.Net;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// When the Engine is busy it answers 429 "too many requests". That is not "the Engine is down": this handler waits the
/// time the Engine asks for (a few seconds at most) and asks again for plain reads, and remembers that the Engine was busy
/// so a page can say "busy, retrying" instead of "Engine unavailable".
/// </summary>
public sealed class EngineBusyBackoffHandler(EngineBusyState state) : DelegatingHandler
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var canRepeat = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        for (var attempt = 0; attempt < MaxRetries && response.StatusCode == HttpStatusCode.TooManyRequests; attempt++)
        {
            state.NoteBusy();
            if (!canRepeat)
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << attempt));
            wait = wait < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : wait > MaxWait ? MaxWait : wait;
            response.Dispose();
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            using var again = await CloneAsync(request).ConfigureAwait(false);
            response = await base.SendAsync(again, cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            state.NoteBusy();
        }

        return response;
    }

    private static Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            clone.Options.TryAdd(option.Key, option.Value);
        }

        return Task.FromResult(clone);
    }
}

/// <summary>Remembers when the Engine last said it was busy (429), so pages can say "busy, retrying" for a short while.</summary>
public sealed class EngineBusyState
{
    private long _lastBusyTicks;

    public void NoteBusy() => Interlocked.Exchange(ref _lastBusyTicks, DateTimeOffset.UtcNow.UtcTicks);

    /// <summary>True when the Engine answered "busy" within the last half minute.</summary>
    public bool WasBusyRecently
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastBusyTicks);
            return ticks != 0 && DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) < TimeSpan.FromSeconds(30);
        }
    }
}
