using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Profiles;

/// <summary>What happened when a profile was saved or removed from the editor sheet.</summary>
/// <param name="Done">True when the sheet can close.</param>
/// <param name="Error">A plain-language message to show in the sheet, if something went wrong.</param>
/// <param name="NeedsUnlock">True when the household's administrator PIN has to be entered before trying again.</param>
/// <param name="Notice">Something to tell the person after the sheet closes (for example, a photo that could not be saved).</param>
public sealed record ProfileEditOutcome(bool Done, string? Error = null, bool NeedsUnlock = false, string? Notice = null);

/// <summary>
/// Sends the choices made in <see cref="ProfileEditorSheet"/> to the Engine: add a person, change one, set or remove a PIN or
/// photo, and delete. It holds the same rules the profile picker uses so Settings and the picker behave alike.
/// </summary>
public sealed class ProfileEditingWorkflow(DashboardIdentityClient identity, ConfirmedActionRunner confirmed)
{
    /// <summary>
    /// Shown when the Engine refuses a removal without giving its own sentence. The Engine refuses to remove the profile in use, a
    /// server administrator's profile (for a household administrator) and a household's last household administrator.
    /// </summary>
    public const string RemoveBlockedMessage =
        "This person can't be removed. A household always keeps one household administrator, and you can't remove the person you are using or a server administrator.";

    public async Task<ProfileEditOutcome> SaveAsync(ProfileViewModel? editing, Guid? householdId, ProfileEditorResult result, bool afterUnlock = false)
    {
        return editing is null
            ? await AddAsync(householdId, result, afterUnlock).ConfigureAwait(false)
            : await ChangeAsync(editing, result, afterUnlock).ConfigureAwait(false);
    }

    public async Task<ProfileEditOutcome> RemoveAsync(ProfileViewModel profile, bool keepPhotos, bool afterUnlock = false)
    {
        var removed = await confirmed.RunAsync(() => identity.DeleteManagedProfileResultAsync(profile.Id, keepPhotos)).ConfigureAwait(false);
        if (removed.Succeeded)
        {
            return new ProfileEditOutcome(true);
        }

        // The Engine's own sentence for a refusal is shown as it is.
        return removed.Failure is DashboardAccessMutationFailure.Conflict or DashboardAccessMutationFailure.Validation
            ? new ProfileEditOutcome(false, string.IsNullOrWhiteSpace(removed.Message) ? RemoveBlockedMessage : removed.Message)
            : Fail(removed.Failure, afterUnlock);
    }

    private async Task<ProfileEditOutcome> AddAsync(Guid? householdId, ProfileEditorResult result, bool afterUnlock)
    {
        if (householdId is not { } household)
        {
            return new ProfileEditOutcome(false, "Your household couldn't be found. Try again.");
        }

        var added = await confirmed.RunAsync(() => identity.AddHouseholdPersonResultAsync(household,
            new AddHouseholdPersonRequest(result.DisplayName, result.Color, result.IsChild, result.NewPin, result.Icon,
                result.ContentLimit, result.ContentLimitAllowUnrated))).ConfigureAwait(false);
        if (!added.Succeeded || added.Value is null)
        {
            // A refusal such as the eight-person limit is shown in the Engine's own sentence.
            return added.Failure is DashboardAccessMutationFailure.Conflict or DashboardAccessMutationFailure.Validation && !string.IsNullOrWhiteSpace(added.Message)
                ? new ProfileEditOutcome(false, added.Message)
                : Fail(added.Failure, afterUnlock);
        }

        if (result.PhotoBytes is { } photo)
        {
            var uploaded = await confirmed.RunAsync(() => identity.UploadManagedProfilePhotoResultAsync(added.Value.Id, photo, result.PhotoFileName ?? "photo.jpg")).ConfigureAwait(false);
            if (!uploaded.Succeeded)
            {
                // The person exists now, so close the sheet and say what is left to do instead of adding them twice.
                return new ProfileEditOutcome(true, Notice: $"{added.Value.DisplayName} was added, but the photo couldn't be saved. Edit the profile to try again.");
            }
        }

        return new ProfileEditOutcome(true);
    }

    private async Task<ProfileEditOutcome> ChangeAsync(ProfileViewModel profile, ProfileEditorResult result, bool afterUnlock)
    {
        // An empty icon tells the Engine to go back to the initial; leaving it out would keep the old icon.
        var updated = await confirmed.RunAsync(() => identity.UpdateManagedProfileResultAsync(profile.Id,
            new UpdateManagedProfileRequest(result.DisplayName, result.Color, result.Icon ?? string.Empty,
                result.ContentLimit, result.ContentLimitAllowUnrated))).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            return Fail(updated.Failure, afterUnlock);
        }

        if (result.NewPin is not null || result.RemovePin)
        {
            var pin = await confirmed.RunAsync(() => identity.SetProfilePinResultAsync(profile.Id, result.NewPin ?? string.Empty)).ConfigureAwait(false);
            if (!pin.Succeeded)
            {
                return Fail(pin.Failure, afterUnlock);
            }
        }

        if (result.PhotoBytes is { } photo)
        {
            var uploaded = await confirmed.RunAsync(() => identity.UploadManagedProfilePhotoResultAsync(profile.Id, photo, result.PhotoFileName ?? "photo.jpg")).ConfigureAwait(false);
            if (!uploaded.Succeeded)
            {
                return Fail(uploaded.Failure, afterUnlock);
            }
        }
        else if (result.RemovePhoto)
        {
            var removed = await confirmed.RunAsync(() => identity.RemoveManagedProfilePhotoResultAsync(profile.Id)).ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                return Fail(removed.Failure, afterUnlock);
            }
        }

        return new ProfileEditOutcome(true);
    }

    // A household administrator who turned on an administrator PIN is refused until it is entered, so ask for it once.
    private static ProfileEditOutcome Fail(DashboardAccessMutationFailure failure, bool afterUnlock)
    {
        if (failure == DashboardAccessMutationFailure.Forbidden && !afterUnlock)
        {
            return new ProfileEditOutcome(false, NeedsUnlock: true);
        }

        return new ProfileEditOutcome(false, failure switch
        {
            DashboardAccessMutationFailure.ConfirmItsYou => "Nothing was changed. Confirm it's you to continue.",
            DashboardAccessMutationFailure.Conflict => "That can't be done right now. A household holds up to 8 people, and each person needs a way in.",
            DashboardAccessMutationFailure.Validation => "Check the name and PIN, then try again.",
            DashboardAccessMutationFailure.NotFound => "That person is no longer in the household.",
            DashboardAccessMutationFailure.Forbidden => "You can't change this person.",
            DashboardAccessMutationFailure.Unauthorized => "Sign in again to continue.",
            DashboardAccessMutationFailure.Transient => "The Engine is unavailable. Try again.",
            _ => "That couldn't be saved. Try again.",
        });
    }
}
