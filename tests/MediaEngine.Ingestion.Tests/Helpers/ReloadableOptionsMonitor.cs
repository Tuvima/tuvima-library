using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests.Helpers;

/// <summary>
/// An options monitor a test can "save" new settings into: <see cref="Publish"/> swaps the current value and calls every
/// listener, the way saving <c>libraries.json</c> does through the real change-token path.
/// </summary>
internal sealed class ReloadableOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly object _gate = new();
    private readonly List<Action<T, string?>> _listeners = [];

    public ReloadableOptionsMonitor(T currentValue)
        => CurrentValue = currentValue;

    public T CurrentValue { get; private set; }

    public int ListenerCount
    {
        get
        {
            lock (_gate)
            {
                return _listeners.Count;
            }
        }
    }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return new Subscription(this, listener);
    }

    public void Publish(T updated)
    {
        Action<T, string?>[] listeners;
        lock (_gate)
        {
            CurrentValue = updated;
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(updated, Microsoft.Extensions.Options.Options.DefaultName);
        }
    }

    private sealed class Subscription(ReloadableOptionsMonitor<T> owner, Action<T, string?> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._listeners.Remove(listener);
            }
        }
    }
}
