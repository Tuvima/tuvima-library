using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Marks a layout that draws the details window itself, in a <c>DetailsOverlayOutlet</c> placed outside the box that holds the page (spec
/// music-player-and-listen-fixes D5). The route view hands the window to such a layout's outlet instead of drawing it beside the page, where a
/// size container or layout containment on the page box could make that box, not the screen, the window's containing block.
/// </summary>
public interface IDetailsOverlayOutletLayout
{
}

/// <summary>
/// Per-circuit hand-over between the route view, which knows which details window is open, and the layout's outlet, which draws it outside the
/// page box. The route view calls <see cref="Show"/> with the window's content, or <see langword="null"/> when none is open; the outlet draws
/// whatever is current and draws again when it changes. The window's own state (what is open, how it is dismissed, focus return) stays in
/// <see cref="DetailsOverlayState"/>; this holds only the content to draw.
/// </summary>
public sealed class DetailsOverlayOutletState
{
    /// <summary>The window to draw, or null when no details window is open.</summary>
    public RenderFragment? Content { get; private set; }

    /// <summary>Raised when <see cref="Content"/> changes.</summary>
    public event Action? Changed;

    public void Show(RenderFragment? content)
    {
        if (ReferenceEquals(Content, content))
        {
            return;
        }

        Content = content;
        Changed?.Invoke();
    }
}
