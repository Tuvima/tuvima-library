using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// What deleting a user (a sign-in) does under users, profiles and households (D5, D6). Normally only the sign-in goes: the profile
/// and everything in it stay. The exception is the last enabled user attached to a household-administrator profile of a household
/// that is not the default one: then <see cref="RemovesHousehold"/> is set and the household goes with every profile in it
/// (<see cref="HouseholdProfiles"/>, listed for the confirmation). The exact rule is in <c>AccountRepository.PlanUserRemoval</c>.
/// </summary>
public sealed record UserRemovalPlan(
    Guid UserId,
    string? Email,
    Guid? ProfileId,
    string? ProfileName,
    Guid? HouseholdId,
    string? HouseholdName,
    bool HouseholdIsDefault,
    bool ProfileIsServerAdministrator,
    bool ProfileIsHouseholdAdministrator,
    bool RemovesHousehold,
    IReadOnlyList<Profile> HouseholdProfiles,
    bool HouseholdHoldsSharedItems);

/// <summary>What a user delete removed: the household and profiles that went with it (none for a sign-in-only delete) and every user whose sign-in is gone.</summary>
public sealed record UserRemovalOutcome(
    Guid? RemovedHouseholdId,
    IReadOnlyList<Guid> DeletedProfileIds,
    IReadOnlyList<Guid> DeletedUserIds);

/// <summary>What deleting a profile removed: the profile's user when it had one (a sign-in without a profile cannot exist, D7).</summary>
public sealed record ProfileRemovalOutcome(Guid? DeletedUserId);

/// <summary>Plain sentences for refusals that both the data store and the service raise (shown to the person as they are).</summary>
public static class UserRemovalMessages
{
    public const string HouseholdHoldsSharedItems = "This household still has shared photos in View. Remove them first.";
    public const string HouseholdCouldNotBeRemoved = "This household could not be removed. Nothing was changed.";
    public const string LastHouseholdAdministrator =
        "A household needs at least one household administrator. Make another profile a household administrator first.";
}

/// <summary>
/// The profiles a delete would take are no longer the ones the caller confirmed (someone gave or removed access in between).
/// Nothing was deleted. The message is shown to the person as it is.
/// </summary>
public sealed class AccountRemovalChangedException() : InvalidOperationException(DefaultMessage)
{
    public const string DefaultMessage = "Something changed since you looked. Check the list and try again.";
}
