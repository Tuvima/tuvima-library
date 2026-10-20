using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Per-circuit state of the details overlay (spec D1): which page sits under it, how it is dismissed, and what may veto that.
/// It watches the address bar itself, so every existing link keeps working: an address under <c>/details/</c> is shown
/// as an overlay over the page the person came from, and each detail history entry carries its own underlay so Back and
/// Forward restore the right page (Listen, title, player, Back shows the title over Listen, not over the player).
/// </summary>
/// <remarks>
/// The service is created before the Router subscribes to location changes, so its answer is current by the time any
/// component renders for the new address. Call <see cref="Attach"/> once the circuit can run JavaScript.
/// </remarks>
public sealed class DetailsOverlayState : IDisposable
{
    private readonly NavigationManager _navigation;
    private readonly IDetailsOverlayHistory _history;
    private readonly Dictionary<string, RouteData> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDetailsOverlayDismissGuard> _guards = [];
    private DetailsOverlayRoute _current;
    private string _currentUri;
    private IDisposable? _changingRegistration;
    private bool _firstEntryNeedsTag;
    private bool _guardAlreadyAnswered;
    private string? _title;

    public DetailsOverlayState(NavigationManager navigation, IDetailsOverlayHistory history)
    {
        _navigation = navigation;
        _history = history;
        _currentUri = ToAppPath(navigation.Uri);
        // A reload (or first load) of a detail address opens over Home in read mode, whatever state the entry holds.
        _current = DetailsOverlayRoute.Resolve(DetailsOverlayRoute.None, previousUri: null, _currentUri, historyEntryState: null, firstRender: true);
        _firstEntryNeedsTag = _current.IsOpen;
        navigation.LocationChanged += OnLocationChanged;
    }

    /// <summary>The overlay for the address on screen; <see cref="DetailsOverlayRoute.None"/> when it is an ordinary page.</summary>
    public DetailsOverlayRoute Current => _current;

    public bool IsOpen => _current.IsOpen;

    /// <summary>The page kept under the overlay while it is open; null otherwise.</summary>
    public string? UnderlayUri => _current.IsOpen ? _current.UnderlayUri : null;

    /// <summary>The open item's title, for the overlay's accessible name; null until the page knows it.</summary>
    public string? Title => _title;

    /// <summary>Raised when <see cref="Title"/> changes.</summary>
    public event Action? TitleChanged;

    /// <summary>Remembers the component that shows an ordinary page, so it can be put back under a later overlay (including after Back).</summary>
    public void RememberPage(string appUri, RouteData routeData) =>
        _pages[DetailsOverlayRoute.PathOf(appUri)] = routeData;

    public bool TryGetPage(string appUri, out RouteData routeData) =>
        _pages.TryGetValue(DetailsOverlayRoute.PathOf(appUri), out routeData!);

    /// <summary>Starts watching for navigations to veto and to tag. Safe to call more than once.</summary>
    public void Attach()
    {
        _changingRegistration ??= _navigation.RegisterLocationChangingHandler(OnLocationChanging);

        // The first load of a detail address has no state of its own. Tag it (replacing, so no extra entry) so that
        // going Back onto it later is read as a return, not mistaken for a new navigation.
        if (_firstEntryNeedsTag && _current.IsOpen)
        {
            _firstEntryNeedsTag = false;
            ReplaceCurrentEntry(_navigation.Uri);
        }
    }

    /// <summary>Sets the open item's title (null clears it).</summary>
    public void SetTitle(string? title)
    {
        if (string.Equals(_title, title, StringComparison.Ordinal))
        {
            return;
        }

        _title = title;
        TitleChanged?.Invoke();
    }

    /// <summary>Registers a guard that may refuse to let the overlay close. Dispose the result to remove it.</summary>
    public IDisposable RegisterDismissGuard(IDetailsOverlayDismissGuard guard)
    {
        _guards.Add(guard);
        return new GuardRegistration(this, guard);
    }

    /// <summary>Asks every registered guard whether the overlay may close; the first refusal wins.</summary>
    public async ValueTask<bool> CanDismissAsync()
    {
        foreach (var guard in _guards.ToArray())
        {
            if (!await guard.CanDismissAsync())
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Closes the overlay entirely (close button, Esc, backdrop), returning to the page under it even after several
    /// title-to-title steps. Returns false when a guard kept it open.
    /// </summary>
    /// <param name="force">Skips the guards, for when the open item no longer exists (deleted elsewhere) and nothing typed on it matters.</param>
    public async ValueTask<bool> DismissAsync(bool force = false)
    {
        if (!_current.IsOpen)
        {
            return true;
        }

        if (!force && !await CanDismissAsync())
        {
            return false;
        }

        // The guards have just answered; the navigation that follows must not ask again.
        _guardAlreadyAnswered = true;
        var route = _current;
        if (route.Depth > 0)
        {
            await _history.GoBackAsync(route.Depth);
        }
        else
        {
            // Entries are not known (after a reload): go to the underlay without adding a step.
            _navigation.NavigateTo(_navigation.ToAbsoluteUri(route.UnderlayUri ?? DetailsOverlayRoute.Home).ToString(), replace: true);
        }

        return true;
    }

    /// <summary>
    /// Replaces the current history entry with <paramref name="target"/> (a tab or season picked on the open item),
    /// keeping the entry's underlay so Back and Forward still restore it. Asks no guard: the person stays on the item.
    /// </summary>
    public void ReplaceCurrentEntry(string target)
    {
        _guardAlreadyAnswered = true;
        _navigation.NavigateTo(target, new NavigationOptions
        {
            ReplaceHistoryEntry = true,
            HistoryEntryState = DetailsOverlayRoute.StateOf(_current),
        });
    }

    public void Dispose()
    {
        _navigation.LocationChanged -= OnLocationChanged;
        _changingRegistration?.Dispose();
        _changingRegistration = null;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var next = ToAppPath(e.Location);
        _current = DetailsOverlayRoute.Resolve(_current, _currentUri, next, e.HistoryEntryState, firstRender: false, CanRestoreUnderlay);
        _currentUri = next;
        _guardAlreadyAnswered = false;
        if (!_current.IsOpen)
        {
            _title = null;
        }
    }

    private async ValueTask OnLocationChanging(LocationChangingContext context)
    {
        var target = ToAppPath(context.TargetLocation);

        // Leaving the open item (another page, or another title) is where unsaved input is asked about.
        var leavingItem = _current.IsOpen && !IsSameItem(target);
        if (leavingItem && !_guardAlreadyAnswered && !await CanDismissAsync())
        {
            context.PreventNavigation();
            return;
        }

        // An in-app link or NavigateTo carries no state. Cancel it and issue it again with the underlay recorded in
        // the new entry (still one entry, not two). Browser Back and Forward arrive tagged and pass straight through.
        if (DetailsOverlayRoute.IsDetailUri(target)
            && !DetailsOverlayRoute.TryDecodeState(context.HistoryEntryState, out _, out _))
        {
            var state = DetailsOverlayRoute.StateForNewEntry(_current, _currentUri, target, CanRestoreUnderlay);
            context.PreventNavigation();
            _guardAlreadyAnswered = true;
            _navigation.NavigateTo(context.TargetLocation, new NavigationOptions { HistoryEntryState = state });
        }
    }

    private bool IsSameItem(string target) =>
        DetailsOverlayRoute.IsDetailUri(target)
        && _current.OverlayUri is { } open
        && string.Equals(DetailsOverlayRoute.PathOf(target), DetailsOverlayRoute.PathOf(open), StringComparison.OrdinalIgnoreCase);

    // A page can sit under the overlay when it was seen in this circuit; Home can always be built.
    private bool CanRestoreUnderlay(string appUri)
    {
        var path = DetailsOverlayRoute.PathOf(appUri);
        return string.Equals(path, DetailsOverlayRoute.Home, StringComparison.Ordinal) || _pages.ContainsKey(path);
    }

    /// <summary>An absolute address as an app path with its query ("/listen?x=1"; "/" is Home), without any fragment.</summary>
    public string ToAppPath(string uri)
    {
        // The target of a navigation may be given relative to the base address.
        var relative = _navigation.ToBaseRelativePath(_navigation.ToAbsoluteUri(uri).AbsoluteUri);
        var end = relative.IndexOf('#', StringComparison.Ordinal);
        if (end >= 0)
        {
            relative = relative[..end];
        }

        return "/" + relative.TrimStart('/');
    }

    private sealed class GuardRegistration(DetailsOverlayState owner, IDetailsOverlayDismissGuard guard) : IDisposable
    {
        public void Dispose() => owner._guards.Remove(guard);
    }
}
