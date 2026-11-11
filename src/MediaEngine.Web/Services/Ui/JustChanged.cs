namespace MediaEngine.Web.Services.Ui;

/// <summary>
/// Holds the one row, card or tile a person just changed, so a page can glow it once (design system, Feedback after an action).
/// The page calls <see cref="Mark"/> when the change worked and puts <see cref="Class"/> on the changed element; the global
/// <c>tl-just-changed</c> animation plays when the class first appears. Marking another id moves the glow to it. Use
/// <see cref="Version"/> in the element's <c>@key</c> to play the glow again for the same id.
/// The mark clears itself <see cref="DefaultLifetime"/> after <see cref="Mark"/> (about as long as the glow plays), so a row that is drawn
/// again later never glows a second time; <see cref="Changed"/> is raised then, for a component that wants to draw the cleared state.
/// </summary>
public sealed class JustChanged : IDisposable
{
    /// <summary>The global class that plays the glow (wwwroot/app.css).</summary>
    public const string GlowClass = "tl-just-changed";

    /// <summary>How long a mark lasts before it clears itself.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(4);

    private readonly object _gate = new();
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private string? _id;
    private int _version;
    private bool _disposed;

    /// <param name="lifetime">How long a mark lasts; <see cref="DefaultLifetime"/> when left out.</param>
    /// <param name="time">The clock the lifetime runs on; the system clock when left out (tests pass a fake one).</param>
    public JustChanged(TimeSpan? lifetime = null, TimeProvider? time = null)
    {
        _lifetime = lifetime ?? DefaultLifetime;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when a mark has cleared itself, on the clock's thread; a component should hop to its own with <c>InvokeAsync</c>.</summary>
    public event Action? Changed;

    /// <summary>Glows this id; any earlier id stops glowing. The glow clears itself after the lifetime.</summary>
    public void Mark(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _id = id;
            var version = ++_version;
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Expire(version), null, _lifetime, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the glow.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _id = null;
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>True when this id is the one just changed.</summary>
    public bool Is(string id)
    {
        lock (_gate)
        {
            return _id is not null && string.Equals(_id, id, StringComparison.Ordinal);
        }
    }

    /// <summary>The class to put on the element for this id, or null when it did not just change.</summary>
    public string? Class(string id) => Is(id) ? GlowClass : null;

    /// <summary>Counts every <see cref="Mark"/>; part of an element's key so the same row glows again after a second change.</summary>
    public int Version(string id)
    {
        lock (_gate)
        {
            return _id is not null && string.Equals(_id, id, StringComparison.Ordinal) ? _version : 0;
        }
    }

    private void Expire(int version)
    {
        lock (_gate)
        {
            // A newer Mark, a Clear or Dispose got here first.
            if (_disposed || _version != version || _id is null)
            {
                return;
            }

            _id = null;
            _timer?.Dispose();
            _timer = null;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _id = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
