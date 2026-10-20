using System.Collections.Concurrent;

namespace MediaEngine.Providers.Services;

/// <summary>
/// One lock per person identity, shared by every place that looks a person up and then creates them. Two jobs that
/// credit the same author at the same moment (a book and its audiobook) would otherwise both miss the lookup and both
/// create the person, and one of the two work links ends up on a duplicate row.
/// </summary>
internal static class PersonCreationLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The lock for a Wikidata person id. Wait on it, look the person up, create if missing, then release.</summary>
    public static SemaphoreSlim ForQid(string qid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);
        return Locks.GetOrAdd($"QID:{qid.Trim()}", _ => new SemaphoreSlim(1, 1));
    }
}
