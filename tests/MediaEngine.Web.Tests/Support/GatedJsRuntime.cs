using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// Forwards every browser call to the runtime it wraps, but holds the first call that matches the gate until
/// <see cref="Release"/> is called. It stands in for a script or reply that is still on its way from the browser
/// when a reload replaces the component that asked for it.
/// </summary>
public sealed class GatedJsRuntime(IJSRuntime inner, Func<string, object?[]?, bool> isGated) : IJSRuntime
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _held;

    public Task Reached => _reached.Task;

    public void Release() => _released.TrySetResult();

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        InvokeAsync<TValue>(identifier, CancellationToken.None, args);

    public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        if (isGated(identifier, args) && Interlocked.Exchange(ref _held, 1) == 0)
        {
            _reached.TrySetResult();
            await _released.Task;
        }

        return await inner.InvokeAsync<TValue>(identifier, cancellationToken, args);
    }
}
