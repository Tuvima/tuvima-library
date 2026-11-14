using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// What the Engine says is being watched and prepared right now, kept fresh while something on screen needs it (the Playing now tab, the
/// Right now card on General and the tab dots). It reads the list every few seconds, so a percent moves, a countdown ticks and a stopped
/// row leaves without a reload (playing-now spec section 5). Administrators only: the Engine refuses anyone else, and then there is simply no list.
/// </summary>
public sealed class PlayingNowState(IEngineApiClient api) : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private int _retained;
    private CancellationTokenSource? _loop;
    private bool _disposed;

    /// <summary>The latest list, or null until the first answer arrives (the loading state).</summary>
    public PlayingNowDto? Current { get; private set; }

    /// <summary>True when the last read failed, so the page can say it is not live.</summary>
    public bool IsUnreachable { get; private set; }

    public event Action? Changed;

    /// <summary>True while something is playing or being prepared, for the tab dot.</summary>
    public bool HasLiveWork => Current is { Rows.Count: > 0 };

    /// <summary>True while a video is being prepared (running or waiting), for the top-bar spinner.</summary>
    public bool HasEncode => Current is { PreparingCount: > 0 };

    /// <summary>Starts reading on a timer while anything holds on; pair every call with <see cref="Release"/>.</summary>
    public void Retain()
    {
        lock (_gate)
        {
            if (_disposed || ++_retained != 1)
            {
                return;
            }

            _loop = new CancellationTokenSource();
            _ = RunAsync(_loop.Token);
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_retained == 0 || --_retained != 0)
            {
                return;
            }

            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var list = await api.GetPlayingNowAsync(ct).ConfigureAwait(false);
        IsUnreachable = list is null;
        if (list is not null)
        {
            Current = list;
        }

        Changed?.Invoke();
    }

    /// <summary>Stops one encode. True when it stopped; the next read removes the row.</summary>
    public async Task<bool> StopAsync(string id)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return false;
        }

        var stopped = await api.StopPlayingNowAsync(guid).ConfigureAwait(false);
        if (stopped)
        {
            await RefreshAsync().ConfigureAwait(false);
        }

        return stopped;
    }

    /// <summary>Stops every encode nobody is watching; returns how many stopped, or null when the call failed.</summary>
    public async Task<int?> StopUnwatchedAsync()
    {
        var stopped = await api.StopUnwatchedPlayingNowAsync().ConfigureAwait(false);
        if (stopped is not null)
        {
            await RefreshAsync().ConfigureAwait(false);
        }

        return stopped;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(Every);
            do
            {
                try
                {
                    await RefreshAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // The Engine is briefly out of reach: the page shows "Not live" and the next tick tries again.
                    IsUnreachable = true;
                    Changed?.Invoke();
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Nothing needs the list any more.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
        }
    }
}
