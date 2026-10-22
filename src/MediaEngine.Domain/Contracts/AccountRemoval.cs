using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// What deleting a sign-in takes with it: the profiles that are its own. A profile is a sign-in's own when it is the profile
/// the sign-in signs in as (its enabled default grant), even when other sign-ins can open it too (that includes a person's
/// own sign-in and its one person), or when no other sign-in holds any grant on it. People made for the household without a
/// sign-in of their own (held by every main sign-in, default for none) stay while the household has more than one. The exact
/// rule is in <c>AccountRepository.PlanAccountRemoval</c>.
/// </summary>
public sealed record AccountRemovalPlan(IReadOnlyList<Profile> OwnProfiles);

/// <summary>What a sign-in delete removed, and who else lost a grant and so needs their open sessions re-checked.</summary>
public sealed record AccountRemovalOutcome(IReadOnlyList<Guid> DeletedProfileIds, IReadOnlyList<Guid> AffectedAccountIds);

/// <summary>
/// The profiles a delete would take are no longer the ones the caller confirmed (someone gave or removed access in between).
/// Nothing was deleted. The message is shown to the person as it is.
/// </summary>
public sealed class AccountRemovalChangedException() : InvalidOperationException(DefaultMessage)
{
    public const string DefaultMessage = "Something changed since you looked. Check the list and try again.";
}
