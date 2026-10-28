using MediaEngine.Api.Services.Libraries.Moves;

namespace MediaEngine.Api.Services.EditSession;

/// <summary>
/// Who is saving the edit session and what they may edit. The library move service needs both (a preview belongs to the person who
/// asked for it, and every item and place is checked against the caller's resource scope), so the edit-session action hands them to the
/// relationship and physical-files handlers through <see cref="EditSessionCallerAccessor"/>.
/// </summary>
public sealed record EditSessionCaller(LibraryMoveActor Actor, LibraryMoveAccess CanEdit);

/// <summary>
/// Carries the <see cref="EditSessionCaller"/> of the request being served down to the handlers without changing their signatures
/// (the same pattern as <c>IHttpContextAccessor</c>): the action opens a scope around the run, the handlers read <see cref="Current"/>.
/// Outside a scope <see cref="Current"/> is null, and the handlers that need a caller refuse the change.
/// </summary>
public sealed class EditSessionCallerAccessor
{
    private readonly AsyncLocal<EditSessionCaller?> _current = new();

    public EditSessionCaller? Current => _current.Value;

    /// <summary>Makes <paramref name="caller"/> the current caller until the returned scope is disposed.</summary>
    public IDisposable Use(EditSessionCaller? caller)
    {
        var previous = _current.Value;
        _current.Value = caller;
        return new Scope(() => _current.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
