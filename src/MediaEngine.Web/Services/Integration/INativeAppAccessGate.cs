using MediaEngine.Web.Services.Configuration;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Whether phone, TV and tablet apps may reach the Engine through the Dashboard.</summary>
public interface INativeAppAccessGate
{
    bool IsEnabled { get; }
}

/// <summary>
/// Reads the <c>native_app_access</c> network setting from <c>config/network.json</c>, the same file the Engine
/// saves. Apps are admitted only while the setting is on and remote access (the verified secure path) is also on.
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

    public bool IsEnabled
    {
        get
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
                        var settings = configuration.LoadNetwork();
                        _enabled = settings.NativeAppAccess?.Enabled == true && settings.Remote?.Enabled == true;
                        _lastWriteUtc = info.LastWriteTimeUtc;
                        _length = info.Length;
                    }

                    return _enabled;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Fail closed: an unreadable or invalid network file never admits apps. The next request retries.
                lock (_gate)
                {
                    _enabled = false;
                    _lastWriteUtc = DateTime.MinValue;
                    _length = -1;
                }
                return false;
            }
        }
    }
}
