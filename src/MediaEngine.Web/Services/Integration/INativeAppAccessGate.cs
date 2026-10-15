using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Whether phone, TV and tablet apps may reach the Engine through the Dashboard.</summary>
public interface INativeAppAccessGate
{
    bool IsEnabled { get; }

    /// <summary>
    /// The separate, config-file-only opt-in (<c>native_app_access.allow_browser_apps</c>) that lets packaged TV web apps
    /// call the app door cross-origin. Off unless the gate says otherwise.
    /// </summary>
    bool AllowBrowserApps => false;
}

/// <summary>
/// Reads the <c>native_app_access</c> network setting from <c>config/network.json</c>, the same file the Engine
/// saves. Apps are admitted while the setting is on; where the app connects from is not checked.
/// Fails closed: a missing, unreadable or invalid file means off. The file is re-read only when it changes, so
/// switching it off takes effect on the next request without a per-request disk parse.
/// </summary>
public sealed class NetworkSettingsNativeAppAccessGate(DashboardConfigurationReader configuration, string configDirectory)
    : INativeAppAccessGate
{
    private readonly object _gate = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _length = -1;
    private bool _enabled;
    private bool _allowBrowserApps;

    public bool IsEnabled => Read().Enabled;

    /// <summary>True only while app access is on and <c>allow_browser_apps</c> is also set.</summary>
    public bool AllowBrowserApps => Read() is { Enabled: true, AllowBrowserApps: true };

    private (bool Enabled, bool AllowBrowserApps) Read()
    {
        var path = Path.Combine(configDirectory, "network.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return (false, false);
            }

            lock (_gate)
            {
                if (info.LastWriteTimeUtc != _lastWriteUtc || info.Length != _length)
                {
                    var settings = configuration.LoadNetwork();
                    _enabled = settings.NativeAppAccess?.Enabled == true;
                    _allowBrowserApps = _enabled && settings.NativeAppAccess?.AllowBrowserApps == true;
                    _lastWriteUtc = info.LastWriteTimeUtc;
                    _length = info.Length;
                }

                return (_enabled, _allowBrowserApps);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Fail closed: an unreadable or invalid network file never admits apps. The next request retries.
            lock (_gate)
            {
                _enabled = false;
                _allowBrowserApps = false;
                _lastWriteUtc = DateTime.MinValue;
                _length = -1;
            }
            return (false, false);
        }
    }
}
