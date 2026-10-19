using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Ui;

/// <summary>What happened when text was sent to the browser's clipboard.</summary>
public enum ClipboardCopyResult
{
    /// <summary>The text is on the clipboard.</summary>
    Copied,

    /// <summary>The browser refused, for example on a plain-http address where it offers no clipboard.</summary>
    Failed,

    /// <summary>The browser connection was already gone, so there is nobody left to tell.</summary>
    Disconnected,
}

/// <summary>
/// One safe way to copy text from a Dashboard page. The browser only offers a clipboard on https and localhost, so on a
/// home-network http address the call throws; that must become a message for the person, never a broken circuit.
/// </summary>
public static class ClipboardInterop
{
    public static async Task<ClipboardCopyResult> TryCopyAsync(this IJSRuntime js, string text, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return ClipboardCopyResult.Copied;
        }
        catch (JSDisconnectedException)
        {
            logger.LogDebug("The browser connection closed before the text could be copied.");
            return ClipboardCopyResult.Disconnected;
        }
        catch (JSException exception)
        {
            logger.LogWarning(exception, "The browser refused to copy to the clipboard; it is probably not a secure (https) address.");
            return ClipboardCopyResult.Failed;
        }
    }

    /// <summary>The toast shown when the copy did not happen; <paramref name="what"/> reads as "the address", "the link", "the code" or "the password".</summary>
    public static string FailedMessage(string what) => $"Couldn't copy — select {what} and copy it yourself.";
}
