using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Keeps a phone sheet above the on-screen keyboard (universal editor v6 section 4a): while the sheet is open in its phone arrangement,
/// <c>editor-focus.js</c> sets <c>--tl-editor-keyboard</c> and <c>--tl-editor-safe-bottom</c> and scrolls the field being typed in into view. The editor window
/// and the collection and playlist builder share the script and those two properties; this is only the part that starts and stops it. A failure to reach the
/// script (a test host, a closing circuit, a moment when the connection is down) leaves the state as it was, so the next draw tries again.
/// </summary>
public sealed class EditorKeyboardFit : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ILogger _logger;
    private IJSObjectReference? _module;
    private bool _watching;
    private bool _disposed;

    public EditorKeyboardFit(IJSRuntime js, ILogger logger)
    {
        _js = js;
        _logger = logger;
    }

    /// <summary>Starts watching the keyboard when <paramref name="wanted"/> and stops when not; a call that changes nothing does nothing.</summary>
    public async Task SyncAsync(bool wanted)
    {
        if (_disposed || wanted == _watching)
        {
            return;
        }

        try
        {
            _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/editor-focus.js");
            if (wanted)
            {
                await _module.InvokeVoidAsync("watchKeyboard");
            }
            else
            {
                await _module.InvokeVoidAsync("stopKeyboard");
            }

            _watching = wanted;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or TaskCanceledException or JSDisconnectedException)
        {
            _logger.LogDebug(ex, "The sheet could not reach its keyboard script; it will try again at the next draw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is null)
        {
            return;
        }

        try
        {
            if (_watching)
            {
                await _module.InvokeVoidAsync("stopKeyboard");
            }

            await _module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // The circuit ended first; the browser already dropped its listeners with the page.
        }
    }
}
