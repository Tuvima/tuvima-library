namespace MediaEngine.Api.Services;

/// <summary>
/// The one place that answers "is the Engine running inside a container?". Container installs are for a server
/// or NAS, so features meant for a single desktop (such as starting without a password) are not offered there.
/// </summary>
public static class ContainerEnvironment
{
    /// <summary>True when the standard container flag, a declared container network mode, or the Docker marker file is present.</summary>
    public static bool IsContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TUVIMA_CONTAINER_NETWORK_MODE"))
        || File.Exists("/.dockerenv");
}

/// <summary>Lets code (and tests) ask whether this install is a container without reading the environment directly.</summary>
public interface IContainerProbe
{
    bool IsContainer();
}

/// <summary>The real answer, read from the environment the Engine runs in.</summary>
public sealed class EnvironmentContainerProbe : IContainerProbe
{
    public bool IsContainer() => ContainerEnvironment.IsContainer();
}
