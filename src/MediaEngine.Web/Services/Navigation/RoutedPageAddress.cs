using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// The address of the routed page a component belongs to, kept apart from the address bar.
/// While the details overlay is open the address bar names the detail item, but the page under it has not moved: a shell
/// inside that page must not reload its list, reset its scroll or change its highlighted entry because of it.
/// A component keeps one of these (seeded from the overlay state the route view cascades, if any) and asks
/// <see cref="Accept"/> for each location change instead of reading the address bar.
/// </summary>
public sealed class RoutedPageAddress
{
    private readonly NavigationManager _navigation;
    private string _current;

    /// <param name="navigation">The address bar.</param>
    /// <param name="overlay">The details overlay state, when the component is shown under the route view; while the overlay is open the page is its underlay.</param>
    public RoutedPageAddress(NavigationManager navigation, DetailsOverlayState? overlay)
    {
        _navigation = navigation;
        _current = overlay?.UnderlayUri ?? DetailsOverlayState.ToAppPath(navigation, navigation.Uri);
    }

    /// <summary>
    /// The address of the routed page as an absolute URI, for code that reads it at one moment (an event, a render) rather than
    /// following changes: the page under the details overlay while it is open, otherwise the address bar.
    /// </summary>
    public static string AbsoluteFor(NavigationManager navigation, DetailsOverlayState? overlay) =>
        overlay?.UnderlayUri is { } underlay ? navigation.ToAbsoluteUri(underlay).ToString() : navigation.Uri;

    /// <summary>The page's address as an app path with its query ("/watch?genre=Drama").</summary>
    public string Current => _current;

    /// <summary>The page's address as an absolute URI.</summary>
    public string Absolute => _navigation.ToAbsoluteUri(_current).ToString();

    /// <summary>
    /// For a location change: true when it moved the page itself to another address (which becomes <see cref="Current"/>).
    /// False for the overlay opening, swapping titles or being dismissed, which leave the page where it was.
    /// </summary>
    public bool Accept(string location)
    {
        var next = DetailsOverlayState.ToAppPath(_navigation, location);
        if (DetailsOverlayRoute.IsDetailUri(next) || string.Equals(next, _current, StringComparison.Ordinal))
        {
            return false;
        }

        _current = next;
        return true;
    }
}
