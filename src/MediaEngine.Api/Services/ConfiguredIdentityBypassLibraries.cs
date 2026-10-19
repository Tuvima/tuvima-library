using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

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
