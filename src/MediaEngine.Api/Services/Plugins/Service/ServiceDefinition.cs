using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Service;

/// <summary>
/// How a plugin that is a separate program is run. Either the Library starts the program (<see cref="Command"/>) and keeps
/// it running, or it connects to one that is already running at <see cref="Address"/>.
/// </summary>
internal sealed record ServiceDefinition(
    string? Command,
    IReadOnlyList<string> Arguments,
    Uri? Address,
    string HealthPath,
    string LookupPath,
    int MaxMemoryMb,
    int StartupSeconds)
{
    public const int DefaultMaxMemoryMb = 512;
    public const int DefaultStartupSeconds = 30;

    /// <summary>True when the Library starts and supervises the program; false when it only connects to an address.</summary>
    public bool IsSupervised => Command is not null;
}

/// <summary>The one capability a program plugin has: it can be asked to look up metadata.</summary>
internal sealed class ServiceLookupCapability(ServiceDefinition definition) : IPluginCapability
{
    public const string CapabilityKind = "service-lookup";

    public string Kind => CapabilityKind;

    public ServiceDefinition Definition { get; } = definition;
}
