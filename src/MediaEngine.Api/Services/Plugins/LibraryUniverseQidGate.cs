using System.Text.RegularExpressions;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// The one rule every lore path shares: a lore plugin may only look up a Wikidata QID the Library already holds as a
/// Universe. Nothing about the user's collection leaves the machine for a QID this gate refuses.
/// </summary>
public interface ILibraryUniverseQidGate
{
    /// <summary>True when the QID is a narrative root, or a root Universe collection, in this Library.</summary>
    Task<bool> HoldsAsync(string? qid, CancellationToken ct = default);
}

public sealed partial class LibraryUniverseQidGate(
    INarrativeRootRepository roots,
    ICollectionRepository collections) : ILibraryUniverseQidGate
{
    public async Task<bool> HoldsAsync(string? qid, CancellationToken ct = default)
    {
        var value = qid?.Trim() ?? string.Empty;
        if (!QidPattern().IsMatch(value))
        {
            return false;
        }

        if (await roots.FindByQidAsync(value, ct).ConfigureAwait(false) is not null)
        {
            return true;
        }

        return await collections.FindUniverseByQidAsync(value, ct).ConfigureAwait(false) is not null;
    }

    [GeneratedRegex("^Q[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex QidPattern();
}
