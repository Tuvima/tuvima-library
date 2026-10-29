using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Components.Settings;

/// <summary>What Users & Households says when a call to the Engine does not go through.</summary>
internal static class ManagedUserMessages
{
    /// <summary>
    /// The Engine's own sentence for a refusal (a second household without a password, eight profiles in a household, a profile that
    /// already has a sign-in, an email already in use) is shown as it is; the generic lines are only for failures that came without one.
    /// </summary>
    public static string For(DashboardAccessMutationFailure failure, string? engineMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(engineMessage))
        {
            return engineMessage;
        }

        return failure switch
        {
            DashboardAccessMutationFailure.Validation => "Check the values and try again.",
            DashboardAccessMutationFailure.Conflict => "This change conflicts with the current state of the Library.",
            DashboardAccessMutationFailure.Forbidden => "You no longer have permission for this change.",
            DashboardAccessMutationFailure.ConfirmItsYou => "Nothing was changed. Confirm it's you to continue.",
            DashboardAccessMutationFailure.Unauthorized => "Your session has expired.",
            DashboardAccessMutationFailure.NotFound => "This sign-in or profile no longer exists.",
            DashboardAccessMutationFailure.Transient => "The Engine is unavailable. Try again.",
            _ => "The change could not be saved. Try again.",
        };
    }
}
