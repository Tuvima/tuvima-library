using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>
/// Phone, TV and tablet apps always reach the Engine through the Dashboard's app door; there is no on/off switch.
/// This gate answers the one remaining setting: the config-file opt-in for packaged TV web apps.
/// </summary>
public interface INativeAppAccessGate
{
    /// <summary>
    /// The config-file-only opt-in (<c>native_app_access.allow_browser_apps</c>) that lets packaged TV web apps
    /// call the app door cross-origin. Off unless the gate says otherwise.
    /// </summary>
    bool AllowBrowserApps => false;
}

/// <summary>
/// Reads <c>native_app_access.allow_browser_apps</c> from <c>config/network.json</c>, the same file the Engine saves.
/// Fails closed: a missing, unreadable or invalid file means off. The file is re-read only when it changes, so a
/// change takes effect on the next request without a per-request disk parse.
/// </summary>
public sealed class NetworkSettingsNativeAppAccessGate(DashboardConfigurationReader configuration, string configDirectory)
    : INativeAppAccessGate
{
    private readonly object _gate = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _length = -1;
    private bool _allowBrowserApps;

    public bool AllowBrowserApps => Read();

    private bool Read()
    {
        var path = Path.Combine(configDirectory, "network.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            lock (_gate)
            {
                if (info.LastWriteTimeUtc != _lastWriteUtc || info.Length != _length)
                {
                    _allowBrowserApps = configuration.LoadNetwork().NativeAppAccess?.AllowBrowserApps == true;
                    _lastWriteUtc = info.LastWriteTimeUtc;
                    _length = info.Length;
                }

                return _allowBrowserApps;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Fail closed: an unreadable or invalid network file never admits browser apps. The next request retries.
            lock (_gate)
            {
                _allowBrowserApps = false;
                _lastWriteUtc = DateTime.MinValue;
                _length = -1;
            }
            return false;
        }
    }
}
