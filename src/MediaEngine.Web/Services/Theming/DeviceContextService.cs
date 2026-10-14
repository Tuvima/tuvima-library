using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Theming;

/// <summary>
/// What the browser reports about the device: the Engine settings key
/// (<c>mobile</c>, <c>web</c>, <c>television</c>), the form factor
/// (<c>phone</c>, <c>tablet</c>, <c>desktop</c>, <c>tv</c>) and whether the primary input is touch.
/// </summary>
public sealed record DeviceContextSnapshot(string DeviceClass, string FormFactor, bool Touch);

/// <summary>
/// Per-circuit (scoped) service that holds the active device class and the
/// fully-resolved UI settings for this browser session.
///
/// <para>
/// A generalised device-context
/// model. Each Blazor Server circuit maintains its own device class and resolved
/// settings so that a TV in television mode does not affect a phone in mobile mode.
/// </para>
///
/// <para>
/// Initialised by <c>MainLayout.OnAfterRenderAsync</c> via JS interop — the browser
/// calls <c>detectDeviceClass()</c> to select responsive web/mobile presentation.
/// Native television identity is issued by the Engine during pairing
/// and is not derived from browser-controlled state.
/// </para>
///
/// <para>
/// Components subscribe to <see cref="OnChanged"/> and call <c>StateHasChanged()</c>
/// in the handler. They must unsubscribe in <c>Dispose()</c> to prevent memory leaks.
/// </para>
/// </summary>
public sealed class DeviceContextService
{
    private readonly IEngineApiClient _apiClient;
    private long _settingsRequestVersion;

    public DeviceContextService(IEngineApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    /// <summary>The active device class for this circuit (web, mobile, television).</summary>
    public string DeviceClass { get; private set; } = "web";

    /// <summary>The form factor for this circuit: phone, tablet, desktop or tv.</summary>
    public string FormFactor { get; private set; } = "desktop";

    /// <summary>Whether the primary input is touch (hover: none or pointer: coarse).</summary>
    public bool IsTouch { get; private set; }

    /// <summary>
    /// The fully-resolved UI settings for the current device class and profile.
    /// Falls back to compiled defaults if the Engine is unreachable.
    /// </summary>
    public ResolvedUISettingsViewModel Settings { get; private set; } = new();

    /// <summary>Whether the service has been initialised with a device class and resolved settings.</summary>
    public bool IsInitialised { get; private set; }

    /// <summary>
    /// Fires whenever the device class or resolved settings change.
    /// Subscribe in <c>OnInitialized</c> and unsubscribe in <c>Dispose()</c>.
    /// </summary>
    public event Action? OnChanged;

    /// <summary>
    /// Initialises the service with the detected device class and fetches resolved settings
    /// from the Engine API. Called once per circuit from <c>MainLayout.OnAfterRenderAsync</c>.
    /// </summary>
    /// <param name="deviceClass">Device class detected by JS interop.</param>
    /// <param name="profileId">Optional profile UUID for the current user.</param>
    public async Task InitialiseAsync(string deviceClass, string? profileId = null)
    {
        var version = ++_settingsRequestVersion;
        DeviceClass = NormalizeResponsiveDeviceClass(deviceClass);
        Settings = CreateFallbackSettings(DeviceClass);
        IsInitialised = true;
        OnChanged?.Invoke();

        var resolved = await ResolveSettingsOrFallbackAsync(DeviceClass, profileId);
        if (version != _settingsRequestVersion)
        {
            return;
        }

        Settings = resolved ?? CreateFallbackSettings(DeviceClass);
        Settings.DeviceClass = DeviceClass;
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Initialises the service from a full browser snapshot (device class, form factor, touch).
    /// </summary>
    public Task InitialiseAsync(DeviceContextSnapshot snapshot, string? profileId = null)
    {
        ApplyFormFactor(snapshot);
        return InitialiseAsync(snapshot.DeviceClass, profileId);
    }

    /// <summary>
    /// Switches presentation from a full browser snapshot. Form factor and touch update
    /// immediately, even when the Engine settings key is unchanged (phone to tablet is not a key change).
    /// </summary>
    public async Task SwitchDeviceAsync(DeviceContextSnapshot snapshot, string? profileId = null)
    {
        var changed = ApplyFormFactor(snapshot);
        var before = DeviceClass;
        await SwitchDeviceAsync(snapshot.DeviceClass, profileId);
        if (changed && string.Equals(before, DeviceClass, StringComparison.OrdinalIgnoreCase))
        {
            OnChanged?.Invoke();
        }
    }

    private bool ApplyFormFactor(DeviceContextSnapshot snapshot)
    {
        var formFactor = NormalizeFormFactor(snapshot.FormFactor, snapshot.DeviceClass);
        var changed = !string.Equals(FormFactor, formFactor, StringComparison.Ordinal) || IsTouch != snapshot.Touch;
        FormFactor = formFactor;
        IsTouch = snapshot.Touch;
        return changed;
    }

    private static string NormalizeFormFactor(string? formFactor, string? deviceClass)
    {
        if (string.Equals(formFactor, "phone", StringComparison.OrdinalIgnoreCase))
        {
            return "phone";
        }

        if (string.Equals(formFactor, "tablet", StringComparison.OrdinalIgnoreCase))
        {
            return "tablet";
        }

        if (string.Equals(formFactor, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return "tv";
        }

        if (string.Equals(formFactor, "desktop", StringComparison.OrdinalIgnoreCase))
        {
            return "desktop";
        }

        return string.Equals(deviceClass, "mobile", StringComparison.OrdinalIgnoreCase) ? "phone"
            : string.Equals(deviceClass, "television", StringComparison.OrdinalIgnoreCase) ? "tv"
            : "desktop";
    }

    /// <summary>
    /// Switches responsive presentation density and re-fetches resolved settings.
    /// </summary>
    public async Task SwitchDeviceAsync(string deviceClass, string? profileId = null)
    {
        var nextClass = NormalizeResponsiveDeviceClass(deviceClass);
        if (string.Equals(DeviceClass, nextClass, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var version = ++_settingsRequestVersion;
        DeviceClass = nextClass;
        Settings = CreateFallbackSettings(nextClass);
        IsInitialised = true;
        OnChanged?.Invoke();

        var resolved = await ResolveSettingsOrFallbackAsync(nextClass, profileId);
        if (version != _settingsRequestVersion
            || !string.Equals(DeviceClass, nextClass, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Settings = resolved ?? CreateFallbackSettings(nextClass);
        Settings.DeviceClass = nextClass;
        OnChanged?.Invoke();
    }

    private async Task<ResolvedUISettingsViewModel?> ResolveSettingsOrFallbackAsync(string deviceClass, string? profileId)
    {
        try
        {
            return await _apiClient.GetResolvedUISettingsAsync(deviceClass, profileId);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeResponsiveDeviceClass(string? deviceClass) =>
        string.Equals(deviceClass, "mobile", StringComparison.OrdinalIgnoreCase) ? "mobile"
        : string.Equals(deviceClass, "television", StringComparison.OrdinalIgnoreCase) ? "television"
        : "web";

    private static ResolvedUISettingsViewModel CreateFallbackSettings(string deviceClass) =>
        new() { DeviceClass = deviceClass };

    // ── Convenience accessors ──────────────────────────────────────────────

    /// <summary>Shorthand: returns <c>true</c> if the named feature is disabled.</summary>
    public bool IsFeatureDisabled(string feature) => Settings.IsFeatureDisabled(feature);

    /// <summary>Shorthand: returns <c>true</c> if the named page is disabled.</summary>
    public bool IsPageDisabled(string page) => Settings.IsPageDisabled(page);

    /// <summary>Whether the device is television.</summary>
    public bool IsTelevision => string.Equals(DeviceClass, "television", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the device is mobile.</summary>
    public bool IsMobile => string.Equals(DeviceClass, "mobile", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the form factor is a phone (below 600 px).</summary>
    public bool IsPhone => FormFactor == "phone";

    /// <summary>Whether the form factor is a tablet (600 to 1023 px).</summary>
    public bool IsTablet => FormFactor == "tablet";

    /// <summary>Whether the form factor is a desktop (1024 px and wider).</summary>
    public bool IsDesktop => FormFactor == "desktop";

    /// <summary>Whether the form factor is a television (alias of <see cref="IsTelevision"/>).</summary>
    public bool IsTv => FormFactor == "tv" || IsTelevision;

    /// <summary>Whether the device is the standard web (desktop) class.</summary>
    public bool IsWeb => string.Equals(DeviceClass, "web", StringComparison.OrdinalIgnoreCase);
}
