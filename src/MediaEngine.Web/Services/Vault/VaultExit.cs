using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Services.Vault;

/// <summary>
/// The one way out of the Vault: locking sends the person Home, and a Vault address for anyone who is not unlocked goes to the same
/// "not found" page an unknown address gets. Both replace the history entry so Back never returns to a Vault page.
/// </summary>
public static class VaultExit
{
    public const string Home = "/";

    public const string NotFound = "/not-found";

    public static void Leave(NavigationManager navigation, string target) => navigation.NavigateTo(target, replace: true);
}
