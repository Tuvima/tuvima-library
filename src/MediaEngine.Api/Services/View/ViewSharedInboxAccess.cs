using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// Who files documents waiting in the Shared Library Inbox: a household administrator or the server administrator,
/// the same people who may change Shared Library names and rules. Everyone else reads them.
/// </summary>
public static class ViewSharedInboxAccess
{
    public static bool CanFile(RequestAuthority authority) =>
        authority.IsEffectiveAdministrator || authority.IsEffectiveHouseholdAdministrator;
}
