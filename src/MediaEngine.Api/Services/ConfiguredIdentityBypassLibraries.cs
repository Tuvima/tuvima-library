using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>
/// Which libraries skip external identity. Their works never get an identity job, so the Ready rule counts them as
/// matched without one.
/// </summary>
public interface IIdentityBypassLibraries
{
    /// <summary>The ids of the libraries whose metadata policy is local-only or manual, read fresh each call.</summary>
    IReadOnlyCollection<string> GetLibraryIds();
}

/// <summary>Reads the libraries configuration, the same source the ingestion pipeline takes its policy from.</summary>
public sealed class ConfiguredIdentityBypassLibraries(IConfigurationLoader configuration) : IIdentityBypassLibraries
{
    public IReadOnlyCollection<string> GetLibraryIds() =>
        configuration.LoadLibraries().Libraries
            .Where(library => LibraryMetadataPolicies.BypassesExternalIdentity(library.MetadataPolicy)
                && !string.IsNullOrWhiteSpace(library.Id))
            .Select(library => library.Id)
            .ToArray();
}
