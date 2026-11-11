using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Components.Player;

/// <summary>A child view a <see cref="PlaybackPanel"/> slides in over its main view (Subtitle appearance, Custom sleep duration, Playback information).</summary>
public sealed record PlaybackPanelView(string Title, RenderFragment Body, RenderFragment? Footer = null);

/// <summary>
/// The stack of nested views inside one panel. A panel hands this to its content as a cascading value; content calls
/// <see cref="Push"/> to show a child view with a back arrow and <see cref="Pop"/> (or the back arrow) to return to the parent.
/// The stack empties whenever the panel closes, so a panel always opens on its main view.
/// </summary>
public sealed class PlaybackPanelNavigator
{
    private readonly List<PlaybackPanelView> _views = [];

    /// <summary>Raised after the stack changes so the owning panel can draw the new view.</summary>
    internal event Action? Changed;

    public int Depth => _views.Count;

    public PlaybackPanelView? Current => _views.Count == 0 ? null : _views[^1];

    public void Push(string title, RenderFragment body, RenderFragment? footer = null)
    {
        _views.Add(new PlaybackPanelView(title, body, footer));
        Changed?.Invoke();
    }

    /// <summary>Returns to the parent view. Returns false when the panel is already on its main view.</summary>
    public bool Pop()
    {
        if (_views.Count == 0)
        {
            return false;
        }

        _views.RemoveAt(_views.Count - 1);
        Changed?.Invoke();
        return true;
    }

    internal void Reset()
    {
        if (_views.Count == 0)
        {
            return;
        }

        _views.Clear();
        Changed?.Invoke();
    }
}
