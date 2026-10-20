using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>Browser implementation of <see cref="IDetailsOverlayHistory"/>: <c>detailsOverlay.goBack</c> in <c>app.js</c>.</summary>
public sealed class DetailsOverlayJsHistory(IJSRuntime js, ILogger<DetailsOverlayJsHistory> logger) : IDetailsOverlayHistory
{
    public async ValueTask GoBackAsync(int steps)
    {
        if (steps <= 0)
        {
            return;
        }

        try
        {
            await js.InvokeVoidAsync("detailsOverlay.goBack", steps);
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; there is no page left to step back on.
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException)
        {
            logger.LogDebug(ex, "Could not step the browser history back by {Steps}.", steps);
        }
    }
}
