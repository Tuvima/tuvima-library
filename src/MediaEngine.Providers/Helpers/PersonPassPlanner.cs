using MediaEngine.Domain.Models;

namespace MediaEngine.Providers.Helpers;

/// <summary>Which part of a title's credits a contributor pass looks up.</summary>
public enum PersonPass
{
    /// <summary>Everything except the cast beyond the first billed actors.</summary>
    FirstBilled,

    /// <summary>Only the cast beyond the first billed actors.</summary>
    Remainder,

    /// <summary>Every credit in one pass.</summary>
    All,
}

/// <summary>The credits a pass should look up, and how many actors it leaves for the later pass.</summary>
public sealed record PersonPassPlan(
    IReadOnlyList<PersonReference> Linked,
    IReadOnlyList<PersonReference> Unlinked,
    int DeferredCount);

/// <summary>
/// Splits a title's credits so the first billed actors are looked up during ingestion and the rest later,
/// when nothing else is waiting. Directors, writers, narrators and music performers are never deferred.
/// </summary>
public static class PersonPassPlanner
{
    /// <summary>How many actors are looked up in the first pass.</summary>
    public const int FirstBilledActorLimit = 10;

    public static PersonPassPlan Plan(
        IReadOnlyList<PersonReference> linked,
        IReadOnlyList<PersonReference> unlinked,
        PersonPass pass,
        int actorLimit = FirstBilledActorLimit)
    {
        if (pass == PersonPass.All || actorLimit <= 0)
        {
            return new PersonPassPlan(linked, unlinked, 0);
        }

        var deferred = new HashSet<PersonReference>(
            linked.Where(IsActor).Concat(unlinked.Where(IsActor)).Skip(actorLimit),
            ReferenceEqualityComparer.Instance);

        if (pass == PersonPass.Remainder)
        {
            return new PersonPassPlan(
                linked.Where(deferred.Contains).ToList(),
                unlinked.Where(deferred.Contains).ToList(),
                0);
        }

        return new PersonPassPlan(
            linked.Where(reference => !deferred.Contains(reference)).ToList(),
            unlinked.Where(reference => !deferred.Contains(reference)).ToList(),
            deferred.Count);
    }

    private static bool IsActor(PersonReference reference) =>
        string.Equals(reference.Role, "Actor", StringComparison.OrdinalIgnoreCase);
}
