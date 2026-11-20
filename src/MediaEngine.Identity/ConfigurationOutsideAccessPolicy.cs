using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Identity;

/// <summary>Outside access is on while the network settings name a way in other than local-only.</summary>
public sealed class ConfigurationOutsideAccessPolicy(IConfigurationLoader configuration) : IOutsideAccessPolicy
{
    public bool IsOutsideAccessOn() => configuration.LoadNetwork().Remote.HasOutsidePath;
}
