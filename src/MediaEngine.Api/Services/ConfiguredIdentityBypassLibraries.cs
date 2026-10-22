using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services;

/// <summary>Reads the libraries configuration and library kinds, the same sources the ingestion pipeline takes its policy from (a plain kind bypasses external identity whatever its policy field says).</summary>
public sealed class ConfiguredIdentityBypassLibraries(IConfigurationLoader configuration, ILibraryKindSource? kindSource = null) : IIdentityBypassLibraries
{
    public IReadOnlyCollection<string> GetLibraryIds()
    {
        var kinds = kindSource?.GetKinds() ?? configuration.LoadLibraryKinds().Kinds;
        return configuration.LoadLibraries().Libraries
            .Where(library => LibraryKindLookupPolicy.BypassesExternalIdentity(library, kinds)
                && !string.IsNullOrWhiteSpace(library.Id))
            .Select(library => library.Id)
            .ToArray();
    }
}
