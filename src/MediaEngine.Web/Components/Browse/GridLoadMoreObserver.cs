using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Browse;

/// <summary>
/// The scroll trigger of a paged grid (View all, My List): it asks the owner's <c>LoadMoreFromObserverAsync</c> when the sentinel after
/// the grid comes near the screen (<c>wwwroot/js/load-more-observer.js</c>). The Load more button stays under the grid, so the list
/// still works when the trigger cannot start.
/// </summary>
public sealed class GridLoadMoreObserver<TOwner>(IJSRuntime js, ILogger logger, TOwner owner) : IAsyncDisposable
    where TOwner : class
{
    private const string ObserverModule = "./js/load-more-observer.js";

    private IJSObjectReference? _module;
    private DotNetObjectReference<TOwner>? _self;
    private ElementReference _sentinel;

    /// <summary>Starts watching the sentinel while there is more to load, and stops watching once there is not.</summary>
    public async Task SyncAsync(ElementReference sentinel, bool watch)
    {
        _sentinel = sentinel;
        try
        {
            if (watch)
            {
                _module ??= await js.InvokeAsync<IJSObjectReference>("import", ObserverModule);
                _self ??= DotNetObjectReference.Create(owner);
                await _module.InvokeVoidAsync("observe", sentinel, _self, new { rootMargin = "800px 0px" });
            }
            else if (_module is not null)
            {
                await _module.InvokeVoidAsync("disconnect", sentinel);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; nothing is left to observe.
        }
        catch (JSException ex)
        {
            // Load more stays under the grid, so the list still works without the scroll trigger.
            logger.LogWarning(ex, "The scroll trigger for {Owner} could not start.", typeof(TOwner).Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            // Two separate steps, so a failed disconnect still lets the module be released.
            await ReleaseAsync(() => _module.InvokeVoidAsync("disconnect", _sentinel).AsTask());
            await ReleaseAsync(() => _module.DisposeAsync().AsTask());
        }

        _self?.Dispose();
    }

    private async Task ReleaseAsync(Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (JSDisconnectedException ex)
        {
            // The circuit closed first; the browser has already released the observer.
            logger.LogDebug(ex, "Grid scroll trigger disconnected during disposal.");
        }
        catch (JSException ex)
        {
            // The sentinel may already be gone from the page; there is nothing left to release.
            logger.LogDebug(ex, "Grid scroll trigger clean-up failed during disposal.");
        }
        catch (OperationCanceledException ex)
        {
            // The circuit is shutting down; the browser releases the observer with the page.
            logger.LogDebug(ex, "Grid scroll trigger clean-up was cancelled during disposal.");
        }
    }
}
