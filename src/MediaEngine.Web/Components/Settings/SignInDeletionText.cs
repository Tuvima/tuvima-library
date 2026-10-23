using MediaEngine.Contracts.Authentication;

namespace MediaEngine.Web.Components.Settings;

/// <summary>The words of the Delete sign-in confirmation, so every screen that deletes a user says the same thing.</summary>
internal static class SignInDeletionText
{
    /// <summary>
    /// Normally only the sign-in goes: "Delete the sign-in mom@home.com? Mom's profile and everything in it stay in Household 1.
    /// You can attach a new sign-in to it later." When it is the last sign-in that can look after a non-default household, the
    /// household goes too and the sentence lists its profiles. <paramref name="household"/> is the household the list shows for
    /// the user (the preview only names it when it would be removed).
    /// </summary>
    public static string Confirmation(string email, string? profileName, string? household, UserDeletionPreviewResponse preview)
    {
        if (preview.RemovesHousehold)
        {
            var name = preview.HouseholdName ?? household ?? "this household";
            return $"This is the last sign-in that can look after {name}. Deleting it removes {name} and its profiles: "
                + $"{string.Join(", ", preview.HouseholdProfiles.Select(profile => profile.DisplayName))}, "
                + "with their progress, bookmarks, lists, photos and videos.";
        }

        var person = string.IsNullOrWhiteSpace(profileName) ? "Their" : $"{profileName}'s";
        return $"Delete the sign-in {email}? {person} profile and everything in it stay in {household ?? "their household"}. "
            + "You can attach a new sign-in to it later.";
    }

    /// <summary>The line shown after the delete went through.</summary>
    public static string Done(string email, string? profileName, UserDeletionPreviewResponse? preview) =>
        preview is { RemovesHousehold: true }
            ? $"The sign-in {email} was deleted, and {preview.HouseholdName ?? "the household"} and its profiles were removed."
            : $"The sign-in {email} was deleted. {(string.IsNullOrWhiteSpace(profileName) ? "Their" : $"{profileName}'s")} profile was kept.";
}
