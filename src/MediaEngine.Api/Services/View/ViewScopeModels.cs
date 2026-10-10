using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// A user-facing View scope. Physical library identifiers are deliberately not
/// part of this contract; they are resolved only after the caller is trusted.
/// </summary>
public sealed record ViewScopeRequest(ViewScopeKind Kind, Guid? ProfileId = null)
{
    public static ViewScopeRequest Shared { get; } = new(ViewScopeKind.Shared);
    public static ViewScopeRequest Mine { get; } = new(ViewScopeKind.Mine);
    public static ViewScopeRequest ForProfile(Guid profileId) =>
        new(ViewScopeKind.Profile, profileId);
}

/// <summary>Profile identity established by trusted Engine middleware.</summary>
public sealed record ViewRequestProfile(Guid ProfileId, string Role);

/// <summary>
/// Persistence projection used by scope resolution. Shared Library access and
/// contribution are independent grants. <see cref="HouseholdId"/> is the household the profile belongs to; people
/// in the same household can browse each other's Personal Space, nobody else can.
/// </summary>
public sealed record ViewScopeStoreEntry(
    ViewProfilePolicy Policy,
    ViewPersonalSpace? PersonalSpace,
    string DisplayName = "",
    string AvatarColor = "#7C4DFF",
    string? AvatarUrl = null,
    Guid? HouseholdId = null);

public sealed record ResolvedViewScope(
    ViewScopeKind Kind,
    Guid? ProfileId,
    IReadOnlySet<Guid> LibraryIds,
    bool WasFallback = false)
{
    public bool ContainsLibrary(Guid libraryId) => LibraryIds.Contains(libraryId);
}

public sealed record ViewScopeOption(
    ViewScopeKind Kind,
    Guid? ProfileId,
    string Label,
    string? AvatarColor = null,
    string? AvatarUrl = null);

public sealed record ViewScopeResolution(
    ResolvedViewScope Scope,
    IReadOnlyList<ViewScopeOption> AvailableScopes);
