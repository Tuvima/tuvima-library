using MediaEngine.Web.Services.Editing;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Details;

/// <summary>
/// Keyboard focus for the batch sections ("Edit together" and the bar's Move), mirroring the hero's section slot: the opener is remembered
/// when a section opens, and when it closes to nothing focus goes back to it, or when it is gone to "Edit together", the bar, the hero's own
/// "..." button and last the overlay's scroller.
/// </summary>
internal static class DetailBatchFocus
{
    private static readonly string[] Fallbacks =
    [
        ".tl-batch__edit-together",
        ".tl-batch__bar button",
        ".tl-detail-secondary-actions button[aria-label='More actions']",
        ".tl-details-overlay__scroller",
    ];

    /// <summary>Remembers the control that has focus now (the one that opened the section).</summary>
    public static async Task RememberOpenerAsync(IJSRuntime js)
    {
        try { await js.InvokeVoidAsync("tuvimaFocusReturn.remember"); }
        catch (JSDisconnectedException) { /* the circuit ended; there is nothing to remember */ }
    }

    /// <summary>Puts focus on the element with this id (the section's heading or frame).</summary>
    public static async Task FocusAsync(IJSRuntime js, string id)
    {
        try { await js.InvokeVoidAsync("tuvimaFocusById", id); }
        catch (JSDisconnectedException) { /* the circuit ended; there is nothing left to focus */ }
    }

    /// <summary>
    /// Gives focus back to the opener when a section closed and none took its place. A swap to another section closes this one an instant
    /// before the other opens, so the answer is read after that instant: the section that opens focuses itself, and focus must not be pulled away from it.
    /// </summary>
    public static async Task RestoreOpenerAsync(IJSRuntime js, EditingModeContext? mode)
    {
        await Task.Yield();
        if (mode is { OpenSectionKind: not null })
        {
            return;
        }

        try { await js.InvokeVoidAsync("tuvimaFocusReturn.restore", (object)Fallbacks); }
        catch (JSDisconnectedException) { /* the circuit ended; focus no longer matters */ }
    }
}
