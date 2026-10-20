namespace MediaEngine.Web.Services.Navigation;

/// <summary>
/// Lets the open detail page veto leaving the details overlay (for example "Save or discard?" while a field has unsaved input).
/// A page registers a guard through <see cref="DetailsOverlayState.RegisterDismissGuard"/> and removes it when it goes away.
/// It is consulted by the close button, Esc, the backdrop and any navigation away from the open item, including browser Back.
/// Nothing registers one yet; the Editing Mode work does.
/// </summary>
public interface IDetailsOverlayDismissGuard
{
    /// <summary>Returns true to let the overlay close, false to keep it open. The guard owns any question it asks the person.</summary>
    ValueTask<bool> CanDismissAsync();
}

/// <summary>Steps the browser history back; the one thing the overlay needs that <see cref="Microsoft.AspNetCore.Components.NavigationManager"/> cannot do.</summary>
public interface IDetailsOverlayHistory
{
    /// <summary>Goes back <paramref name="steps"/> history entries (the equivalent of <c>history.go(-steps)</c>).</summary>
    ValueTask GoBackAsync(int steps);
}
