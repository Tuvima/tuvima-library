using MediaEngine.Contracts.Collections;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Who may do what with collections on the Dashboard (Collections and lists D19: everyone makes their own, administrators push to
/// everyone). This only decides what the screens offer; the Engine decides again on every request, so a screen that offered too much
/// would still be refused.
/// </summary>
public static class CollectionAuthoringRule
{
    /// <summary>True when the signed-in profile is a Kids profile. An unknown profile is treated as one, so nothing is offered by mistake.</summary>
    public static bool IsKids(DashboardSessionAccessor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Authority?.ProfileGrants.FirstOrDefault(grant => grant.ProfileId == session.ActiveProfileId)
            is not { IsRestricted: false };
    }

    /// <summary>Every adult profile can start a New collection for collections it owns (audience Only me). Kids profiles cannot.</summary>
    public static bool CanCreate(DashboardSessionAccessor session) => !IsKids(session);

    /// <summary>
    /// Library ownership ("Share with everyone") and Selected profiles are administrator choices. Only an administrator whose
    /// administrator PIN is unlocked (where one is set) is offered them.
    /// </summary>
    public static bool CanShare(DashboardSessionAccessor session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.HasAction("access.manage");
    }

    /// <summary>
    /// The hand-picked collections and playlists the "Add to collection" list offers: exactly the ones the Engine says the caller may
    /// edit (their own; for an administrator, everything they manage), of the kind the menu is for.
    /// </summary>
    public static bool IsAddTarget(ManagedCollectionDto collection, bool playlist) =>
        collection.CanEdit
        && collection.IsEnabled
        && string.Equals(collection.Resolution, "materialized", StringComparison.OrdinalIgnoreCase)
        && string.Equals(collection.CollectionType, playlist ? "Playlist" : "Custom", StringComparison.OrdinalIgnoreCase);
}
