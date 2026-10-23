using MediaEngine.Domain.Aggregates;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// What deleting a user (a sign-in) does under users, profiles and households (D5, D6). Normally only the sign-in goes: the profile
/// and everything in it stay. The exception is the last enabled user attached to a household-administrator profile of a household
/// that is not the default one: then <see cref="RemovesHousehold"/> is set and the household goes with every profile in it
/// (<see cref="HouseholdProfiles"/>, listed for the confirmation) and with every photo and video of theirs and of its Shared Library
/// (D18; <see cref="HouseholdHoldsSharedItems"/> says the Shared Library has some). The exact rule is in <c>AccountRepository.PlanUserRemoval</c>.
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

/// <summary>
/// What a user delete removed: the household and profiles that went with it (none for a sign-in-only delete) and every user whose
/// sign-in is gone. <paramref name="SharedFilesToDelete"/> are the files behind the removed household's shared photos (their rows are
/// already gone); the caller deletes the ones Tuvima owns from disk (D18).
/// </summary>
public sealed record UserRemovalOutcome(
    Guid? RemovedHouseholdId,
    IReadOnlyList<Guid> DeletedProfileIds,
    IReadOnlyList<Guid> DeletedUserIds,
    IReadOnlyList<ProfilePersonalFile>? SharedFilesToDelete = null);

/// <summary>What deleting a profile removed: the profile's user when it had one (a sign-in without a profile cannot exist, D7).</summary>
public sealed record ProfileRemovalOutcome(Guid? DeletedUserId);

/// <summary>Plain sentences for refusals that both the data store and the service raise (shown to the person as they are).</summary>
public static class UserRemovalMessages
{
    /// <summary>Said when a household is removed on its own (it has no people left) while its Shared Library still holds photos. A household that goes with its last household administrator's sign-in takes its shared photos with it instead.</summary>
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

/// <summary>
/// A profile's access changed after the caller loaded it (someone else saved, or the profile's roles changed), so the access the
/// caller sent may undo that change. Nothing was saved. The message is shown to the person as it is.
/// </summary>
public sealed class ProfileAccessChangedException() : InvalidOperationException(AccountRemovalChangedException.DefaultMessage);
