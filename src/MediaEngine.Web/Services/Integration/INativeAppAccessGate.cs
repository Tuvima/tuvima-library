namespace MediaEngine.Web.Services.Integration;

/// <summary>Whether phone, TV and tablet apps may reach the Engine through the Dashboard.</summary>
public interface INativeAppAccessGate
{
    bool IsEnabled { get; }
}

/// <summary>
/// Temporary stand-in: reads <c>NativeAppAccess:Enabled</c> from app configuration (environment variable
/// <c>NativeAppAccess__Enabled</c>). Off unless explicitly set. The "native_app_access" network setting
/// (next packet) replaces this class; the forwarder only depends on <see cref="INativeAppAccessGate"/>.
/// </summary>
public sealed class ConfigurationNativeAppAccessGate(IConfiguration configuration) : INativeAppAccessGate
{
    public bool IsEnabled => configuration.GetValue<bool>("NativeAppAccess:Enabled");
}
