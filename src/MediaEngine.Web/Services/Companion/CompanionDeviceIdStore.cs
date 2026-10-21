using Microsoft.JSInterop;

namespace MediaEngine.Web.Services.Companion;

/// <summary>Where a browser keeps the id that tells the Engine which companion it is.</summary>
public interface ICompanionDeviceIdStore
{
    /// <summary>The companion's own device id; a new random one the first time, then the same one for this browser.</summary>
    ValueTask<string> GetAsync();
}

/// <summary>
/// Keeps the id in the browser's local storage through <c>wwwroot/js/companion-device.js</c>. When storage is blocked (a
/// private window) the script hands back an id that lasts for the page, so the companion still works and simply starts fresh
/// next time.
/// </summary>
public sealed class BrowserCompanionDeviceIdStore(IJSRuntime js) : ICompanionDeviceIdStore
{
    public async ValueTask<string> GetAsync()
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/companion-device.js");
        return await module.InvokeAsync<string>("getDeviceId");
    }
}
