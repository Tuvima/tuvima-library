using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Services.Settings;

/// <summary>What the "Share with friends" card needs to offer an invitation: the away-from-home address, and the households and libraries to pick from.</summary>
/// <param name="AwayAddress">The address that works away from home, or null when none is set up yet (no invitation can be shared then).</param>
public sealed record FriendInviteOptions(
    string? AwayAddress,
    IReadOnlyList<HouseholdResponse> Households,
    IReadOnlyList<AccessLibraryOptionDto> Libraries,
    bool LibrariesUnavailable);

/// <summary>One friend to invite: who they are, and the household and libraries they get (spec remote-access D13 and D15).</summary>
/// <param name="HouseholdId">An existing household, or null to start a new one named <paramref name="NewHouseholdName"/>.</param>
/// <param name="HiddenLibraryIds">For a new household only: the libraries left out of it. An existing household keeps its own libraries.</param>
public sealed record FriendInviteRequest(
    string Name,
    string Email,
    Guid? HouseholdId,
    string? NewHouseholdName,
    IReadOnlyList<Guid> HiddenLibraryIds);

/// <summary>The answer: the one-time link and code to share, or the plain sentence saying why not. The code is shown once and never logged.</summary>
public sealed record FriendInviteResult(bool Succeeded, string? Message, string? Link, string? Code, DateTimeOffset? ExpiresAt, bool NeedsSecuredAdministrators = false)
{
    public static FriendInviteResult Failed(string message, bool needsSecuredAdministrators = false) => new(false, message, null, null, null, needsSecuredAdministrators);
}

/// <summary>Makes one-time invitations for friends outside the home, through the existing invitation system (spec remote-access WP5b).</summary>
public interface IFriendInviteService
{
    /// <summary>True when the signed-in person may add sign-ins. The Engine still decides every action.</summary>
    bool CanInvite { get; }

    Task<FriendInviteOptions> LoadAsync(CancellationToken ct = default);

    Task<FriendInviteResult> CreateAsync(FriendInviteRequest request, CancellationToken ct = default);
}

/// <summary>Where an invitation link points: the away-from-home address plus the existing invitation page, with the one-time code.</summary>
public static class FriendInviteLink
{
    /// <summary>The page the QR and the link open. It offers the app first and a password form in the browser as the fallback.</summary>
    public const string Path = "/auth/invite";

    public static string Build(string awayAddress, string code) =>
        $"{awayAddress.Trim().TrimEnd('/')}{Path}?code={Uri.EscapeDataString(code)}";
}

public sealed class FriendInviteService(
    DashboardIdentityClient identity,
    ConfirmedActionRunner confirmed,
    DashboardSessionAccessor session,
    IEngineApiClient engine) : IFriendInviteService
{
    // A household made for a friend but not yet used (the library list was refused, say): a second try reuses it instead of making another.
    private Guid? _pendingHouseholdId;
    private string? _pendingHouseholdName;

    public bool CanInvite => session.HasAction("access.manage");

    public async Task<FriendInviteOptions> LoadAsync(CancellationToken ct = default)
    {
        if (!CanInvite)
        {
            return new FriendInviteOptions(null, [], [], false);
        }

        var addresses = await engine.GetNetworkAppAddressesAsync(ct).ConfigureAwait(false);
        var households = await identity.GetManagedHouseholdsAsync(ct).ConfigureAwait(false);
        var libraries = await identity.GetAccessLibrariesAsync(ct).ConfigureAwait(false);
        return new FriendInviteOptions(
            string.IsNullOrWhiteSpace(addresses?.AwayFromHome) ? null : addresses.AwayFromHome.Trim(),
            households, libraries ?? [], libraries is null);
    }

    public async Task<FriendInviteResult> CreateAsync(FriendInviteRequest request, CancellationToken ct = default)
    {
        // The Engine decides who may add a sign-in; this keeps the call itself closed to anyone else.
        if (!CanInvite)
        {
            return FriendInviteResult.Failed(ManagedUserMessages.For(DashboardAccessMutationFailure.Forbidden));
        }

        var options = await LoadAsync(ct).ConfigureAwait(false);
        if (options.AwayAddress is null)
        {
            return FriendInviteResult.Failed("Set up away-from-home access first, so the invitation has an address to open.");
        }

        var household = request.HouseholdId;
        if (household is null)
        {
            // A new household is made empty and given its libraries BEFORE any invitation exists, so a failure here never leaves a
            // code that could open more than was chosen. The friend joins it as an ordinary member, never as its administrator.
            var name = request.NewHouseholdName?.Trim() ?? string.Empty;
            if (_pendingHouseholdId is null || !string.Equals(_pendingHouseholdName, name, StringComparison.Ordinal))
            {
                var made = await confirmed.RunAsync(() => identity.CreateManagedHouseholdResultAsync(name, ct)).ConfigureAwait(false);
                if (!made.Succeeded || made.Value is null)
                {
                    return FriendInviteResult.Failed(ManagedUserMessages.For(made.Failure, made.Message), made.NeedsSecuredAdministrators);
                }

                _pendingHouseholdId = made.Value.Id;
                _pendingHouseholdName = name;
            }

            household = _pendingHouseholdId;
            if (request.HiddenLibraryIds.Count > 0)
            {
                var ceiling = await confirmed.RunAsync(() => identity.ReplaceHouseholdLibraryAccessResultAsync(household!.Value, request.HiddenLibraryIds, ct)).ConfigureAwait(false);
                if (!ceiling.Succeeded)
                {
                    return FriendInviteResult.Failed(ManagedUserMessages.For(ceiling.Failure, ceiling.Message));
                }
            }
        }

        // Always an invitation (the friend chooses a password themselves), always a new ordinary profile: never an existing profile,
        // never a Kids switch, never a role. The Engine adds nothing else.
        var create = new CreateUserRequest(
            request.Email.Trim(), "invitation", NewProfile: new NewUserProfileRequest(request.Name.Trim(), household, null, false));
        var created = await confirmed.RunAsync(() => identity.CreateUserResultAsync(create, ct)).ConfigureAwait(false);
        if (!created.Succeeded || created.Value?.Invitation is not { } invitation)
        {
            return FriendInviteResult.Failed(ManagedUserMessages.For(created.Failure, created.Message), created.NeedsSecuredAdministrators);
        }

        _pendingHouseholdId = null;
        _pendingHouseholdName = null;
        return new FriendInviteResult(true, null, FriendInviteLink.Build(options.AwayAddress, invitation.Code), invitation.Code, invitation.ExpiresAt);
    }
}
