using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Playback;

public sealed record ContextSidebarDescriptor(
    Guid LeaseId,
    ContextSidebarOwner Owner,
    string Title,
    int Width,
    RenderFragment Body,
    Func<Task>? Close = null,
    Func<int, Task>? WidthChanged = null,
    Func<Task>? Displaced = null);

public enum ContextSidebarOwner { Playback, Ingestion }

/// <summary>Owns the single right-side context surface shared by playback and ingestion.</summary>
public sealed class ContextSidebarCoordinator
{
    private readonly object _sync = new();
    private ContextSidebarDescriptor? _current;
    private bool _explicitlyInteracted;
    private bool _initialRestoreAttempted;

    public event Action? Changed;

    public ContextSidebarDescriptor? Current
    {
        get { lock (_sync) return _current; }
    }

    public async Task<Guid> OpenExplicitAsync(ContextSidebarOwner owner, string title, int width, RenderFragment body,
        Func<Task>? close = null, Func<int, Task>? widthChanged = null, Func<Task>? displaced = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        var leaseId = Guid.NewGuid();
        Func<Task>? displacedPrevious;
        lock (_sync)
        {
            _explicitlyInteracted = true;
            displacedPrevious = _current?.Owner == owner ? null : _current?.Displaced;
            _current = new ContextSidebarDescriptor(leaseId, owner, title, Math.Clamp(width, 320, 480), body, close, widthChanged, displaced);
        }
        Changed?.Invoke();
        if (displacedPrevious is not null) await displacedPrevious().ConfigureAwait(false);
        return leaseId;
    }

    public bool Update(Guid leaseId, string title, int width, RenderFragment body,
        Func<Task>? close = null, Func<int, Task>? widthChanged = null, Func<Task>? displaced = null)
    {
        lock (_sync)
        {
            if (_current?.LeaseId != leaseId) return false;
            _current = _current with { Title = title, Width = Math.Clamp(width, 320, 480), Body = body,
                Close = close, WidthChanged = widthChanged, Displaced = displaced ?? _current.Displaced };
        }
        Changed?.Invoke();
        return true;
    }

    public bool Release(Guid leaseId)
    {
        lock (_sync)
        {
            if (_current?.LeaseId != leaseId) return false;
            _current = null;
            _explicitlyInteracted = true;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Allows one stored-open restore, only before the user or another owner has acted.</summary>
    public bool TryInitialRestore(ContextSidebarOwner owner, string title, int width, RenderFragment body,
        Func<Task>? close = null, Func<int, Task>? widthChanged = null, Func<Task>? displaced = null)
    {
        lock (_sync)
        {
            if (_initialRestoreAttempted || _explicitlyInteracted || _current is not null) return false;
            _initialRestoreAttempted = true;
            _current = new ContextSidebarDescriptor(Guid.NewGuid(), owner, title, Math.Clamp(width, 320, 480), body,
                close, widthChanged, displaced);
        }
        Changed?.Invoke();
        return true;
    }
}
