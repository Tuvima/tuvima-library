using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Security;

/// <summary>
/// Reads which libraries are Selected-profiles libraries from the libraries file, the one place a library's access mode lives
/// (Library pages D9). Read on each ask, so a change saved from the Access tab applies to the very next request.
/// </summary>
public sealed class ConfigurationLibraryAccessCatalog(IConfigurationLoader configuration) : ILibraryAccessCatalog
{
    public ValueTask<IReadOnlySet<Guid>> GetSelectedLibraryIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlySet<Guid> selected = configuration.LoadLibraries().Libraries
            .Where(library => string.Equals(library.Access, LibraryAccessLevels.Selected, StringComparison.OrdinalIgnoreCase))
            .Select(library => Guid.TryParse(library.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        return new ValueTask<IReadOnlySet<Guid>>(selected);
    }
}
