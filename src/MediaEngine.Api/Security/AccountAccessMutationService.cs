using System.Net.Mail;
using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Identity;

namespace MediaEngine.Api.Security;

public sealed class AccountAccessMutationService(
    IAccountRepository accounts,
    IProfileAccessRepository profileAccess,
    IIdentityRepository identities,
    IProfileRepository profiles,
    IConfigurationLoader configuration,
    IAccountAccessDecisionService accountDecisions,
    IAuthorizationEvaluator evaluator,
    IAuthorizationInvalidationService invalidation,
    IAuthorizationAuditWriter audit,
    IFirstPartyIdentityService firstParty,
    IProfilePhotoDisposer photoDisposer,
    TimeProvider clock,
    IUsableAdministratorService usableAdministrators,
    AuthenticationPolicyMutationGate mutationGate,
    ILogger<AccountAccessMutationService>? logger = null,
    IHouseholdRepository? households = null) : IAccountAccessMutationService
{
    /// <summary>What a person is told when a change would leave nobody able to sign in as a server administrator.</summary>
    public const string LastAdministratorMessage =
        "This is the last administrator who can sign in. Make someone else an administrator first.";

    public async Task<CreatedUser> CreateUserAsync(
        RequestAuthority actor,
        CreateUserCommand command,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same data.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        if ((command.ProfileId is null) == (command.NewProfile is null))
        {
            throw new ArgumentException("Choose one existing profile or make one new profile.");
        }

        var email = NormalizeEmail(command.Email);
        if (command.Method == NewUserSignInMethod.TemporaryPassword && string.IsNullOrEmpty(command.TemporaryPassword))
        {
            throw new ArgumentException("Set a starting password.");
        }

        if (command.Method == NewUserSignInMethod.Invitation && !string.IsNullOrEmpty(command.TemporaryPassword))
        {
            throw new ArgumentException("Choose a starting password or an invitation link, not both.");
        }

        await RequireEmailAvailableAsync(Guid.Empty, email.ToUpperInvariant(), ct).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        Profile? newProfile = null;
        Guid? householdId = null;
        string? newHouseholdName = null;
        var features = new HashSet<AccountFeatureId>();
        Guid profileId;
        if (command.NewProfile is { } requested)
        {
            var namesNewHousehold = !string.IsNullOrWhiteSpace(requested.NewHouseholdName);
            if ((requested.HouseholdId is not null) == namesNewHousehold)
            {
                throw new ArgumentException("Choose an existing household or name a new one.");
            }

            if (namesNewHousehold)
            {
                // The first person of a new household looks after it, so they can't be a Kids profile (D15); and a second household
                // is added only once every server administrator has an email and a password (D10).
                if (requested.IsKids)
                {
                    throw new InvalidOperationException("The first person in a new household looks after it, so they can't be a Kids profile.");
                }

                await RequireAdministratorsSecuredAsync(ct).ConfigureAwait(false);
            }
            else
            {
                householdId = requested.HouseholdId;
            }

            newProfile = new Profile
            {
                Id = Guid.NewGuid(),
                DisplayName = NormalizeDisplayName(requested.DisplayName),
                AvatarColor = NormalizeAvatarColor(requested.AvatarColor),
                // A new profile is an ordinary member; Kids only when asked, with the Kids content limit.
                Role = requested.IsKids ? ProfileRole.RestrictedProfile : ProfileRole.StandardUser,
                ContentLimit = requested.IsKids ? ProfileContentLimits.KidsDefault : null,
                CreatedAt = now,
            };
            profileId = newProfile.Id;

            // Read, Watch and Listen start on; View (the household's shared photos) starts off for someone outside the household.
            features.Add(AccountFeatureId.Read);
            features.Add(AccountFeatureId.Watch);
            features.Add(AccountFeatureId.Listen);
            if (!namesNewHousehold)
            {
                features.Add(AccountFeatureId.View);
            }

            if (namesNewHousehold)
            {
                var name = requested.NewHouseholdName!.Trim();
                if (name.Length > HouseholdManagementService.MaximumNameLength)
                {
                    throw new ArgumentException($"A household name must be {HouseholdManagementService.MaximumNameLength} characters or fewer.");
                }

                // The household is made by the data store in the same step as the user and its profile, so nothing is left behind if it fails.
                newHouseholdName = name;
            }
        }
        else
        {
            profileId = command.ProfileId!.Value;
            if (await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) is null)
            {
                throw new KeyNotFoundException("Profile not found.");
            }

            if (await profileAccess.FindUserIdOfProfileAsync(profileId, ct).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("That profile already has a sign-in.");
            }
        }

        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            ProfileId = profileId,
        };
        IssuedAccountInvitation? issued = null;
        AccountInvitation? invitation = null;
        if (command.Method == NewUserSignInMethod.Invitation)
        {
            var code = InvitationCode.Generate();
            invitation = new AccountInvitation
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TokenHash = InvitationCode.Hash(code),
                CreatedAt = now,
                ExpiresAt = now.Add(InvitationLifetime()),
            };
            issued = new IssuedAccountInvitation(account.Id, InvitationCode.Format(code), invitation.ExpiresAt);
        }

        // The user, its profile (with the access a new person starts with) and any new household are saved in one transaction, so a
        // failure here leaves nothing behind.
        await accounts.CreateUserAsync(
            account,
            new NewUserPlacement(newProfile, householdId, newHouseholdName, Features: newProfile is null ? null : features),
            invitation, ct).ConfigureAwait(false);

        try
        {
            if (command.Method == NewUserSignInMethod.TemporaryPassword)
            {
                await firstParty.SetTemporaryPasswordAsync(
                    account.Id, command.TemporaryPassword!, TemporaryPasswordExpiry(), ct: ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // A password that does not meet the rules must not leave a half-made user (or the profile and household made for it) behind.
            await RollBackNewUserAsync(account.Id, newProfile?.Id, newHouseholdName is not null).ConfigureAwait(false);
            throw;
        }

        account = await accounts.GetByIdAsync(account.Id, ct).ConfigureAwait(false) ?? account;
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) ?? newProfile!;
        await ChangedAsync(actor, "account.created", "account", account.Id.ToString("D"),
            account.Id, null, ct).ConfigureAwait(false);
        if (newHouseholdName is not null && profile.HouseholdId is { } startedHousehold)
        {
            await ChangedAsync(actor, "household.created", "household", startedHousehold.ToString("D"),
                account.Id, null, ct).ConfigureAwait(false);
        }

        // The first person of a household made for them looks after it; that is a change of authority, so it is recorded like any other.
        if (profile.IsHouseholdAdministrator)
        {
            await ChangedAsync(actor, "household.admin_granted", "account", account.Id.ToString("D"),
                account.Id, null, ct).ConfigureAwait(false);
        }

        return new CreatedUser(account, profile, issued);
    }

    public async Task<IReadOnlyList<Account>> ListUsersAsync(RequestAuthority actor, CancellationToken ct = default)
    {
        await RequireReadAsync(actor, ct).ConfigureAwait(false);
        return await accounts.GetAllAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Undoes a user that was made but could not be finished: the sign-in, and the profile and household made for it.</summary>
    private async Task RollBackNewUserAsync(Guid userId, Guid? newProfileId, bool startedHousehold)
    {
        try
        {
            if (startedHousehold)
            {
                // A new household's first user is its last administrator, so this takes the household and its profile with it.
                var plan = await accounts.PlanUserRemovalAsync(userId, CancellationToken.None).ConfigureAwait(false);
                if (plan.RemovesHousehold)
                {
                    await accounts.DeleteUserAsync(
                        userId, plan.HouseholdProfiles.Select(profile => profile.Id).ToArray(), CancellationToken.None).ConfigureAwait(false);
                    return;
                }
            }

            // Only the sign-in goes (never a household that was already there), then the profile made for it.
            await accounts.DeleteAccountAsync(userId, CancellationToken.None).ConfigureAwait(false);
            if (newProfileId is { } profileId && await profiles.GetByIdAsync(profileId, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                await accounts.DeleteManagedProfileAsync(profileId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            (logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountAccessMutationService>.Instance).LogError(
                exception, "Could not undo the user {UserId} that was not finished.", userId);
        }
    }

    public async Task SetTemporaryPasswordAsync(
        RequestAuthority actor,
        Guid accountId,
        string temporaryPassword,
        bool mustChangePassword = true,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var targetProfile = await ProfileOfUserAsync(target, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, targetProfile?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        if (actor.AccountId == accountId)
        {
            throw new InvalidOperationException("Use Account > Security to change your own password.");
        }

        RequireNotAdministratorTarget(targetProfile, householdOnly);

        // Taking over an administrator's sign-in needs a person who is an unlocked administrator, not an application.
        if (targetProfile is { IsServerAdministrator: true }
            && (actor.PrincipalKind != PrincipalKind.Human || !actor.ActiveProfileIsServerAdministrator))
        {
            throw new UnauthorizedAccessException("Only an unlocked administrator can set an administrator's temporary password.");
        }

        await firstParty.SetTemporaryPasswordAsync(
            accountId, temporaryPassword, TemporaryPasswordExpiry(), mustChangePassword, ct).ConfigureAwait(false);
        await ChangedAsync(actor, mustChangePassword ? "account.temporary_password_set" : "account.password_set",
            "account", accountId.ToString("D"), accountId, null, ct).ConfigureAwait(false);
    }

    public async Task<Account> ChangeEmailAsync(
        RequestAuthority actor,
        Guid accountId,
        string email,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var targetProfile = await ProfileOfUserAsync(target, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, targetProfile?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        RequireMayChangeEmail(actor, target, targetProfile, householdOnly);

        var normalized = NormalizeEmail(email);
        var normalizedKey = normalized.ToUpperInvariant();
        await RequireEmailAvailableAsync(accountId, normalizedKey, ct).ConfigureAwait(false);

        target.Email = normalized;
        target.NormalizedEmail = normalizedKey;
        target.UpdatedAt = clock.GetUtcNow();
        await UpdateAccountWithEmailGuardAsync(target, emailChanged: true, ct).ConfigureAwait(false);
        await InvalidateChallengesForOldEmailAsync(accountId, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "account.email_changed", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
        return (await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false))!;
    }

    /// <summary>The rules for moving someone's sign-in address.</summary>
    private static void RequireMayChangeEmail(RequestAuthority actor, Account target, Profile? targetProfile, bool householdOnly)
    {
        if (actor.AccountId == target.Id)
        {
            throw new InvalidOperationException("Email changes are made by an administrator, not for your own sign-in.");
        }

        RequireNotAdministratorTarget(targetProfile, householdOnly);

        // Taking over an administrator's sign-in address would let its password reset reach someone else.
        if (targetProfile is { IsServerAdministrator: true }
            && (actor.PrincipalKind != PrincipalKind.Human || !actor.ActiveProfileIsServerAdministrator))
        {
            throw new UnauthorizedAccessException("Only an unlocked administrator can change an administrator's email.");
        }
    }

    private async Task RequireEmailAvailableAsync(Guid accountId, string normalizedKey, CancellationToken ct)
    {
        if (await accounts.GetByNormalizedEmailAsync(normalizedKey, ct).ConfigureAwait(false) is { } existing
            && existing.Id != accountId)
        {
            throw new InvalidOperationException("That email already has a sign-in.");
        }
    }

    private async Task UpdateAccountWithEmailGuardAsync(Account account, bool emailChanged, CancellationToken ct)
    {
        try
        {
            await accounts.UpdateAccountAsync(account, ct).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (emailChanged && ex.SqliteErrorCode == 19)
        {
            // Another change took the address between the check before this write and the write itself: the unique index decides.
            throw new InvalidOperationException("That email already has a sign-in.");
        }
    }

    // A password-reset link or two-step challenge sent for the old address must not outlive the move to the new one.
    private async Task InvalidateChallengesForOldEmailAsync(Guid accountId, CancellationToken ct)
    {
        await identities.InvalidatePasswordResetChallengesAsync(accountId, ct).ConfigureAwait(false);
        await identities.InvalidateTwoStepChallengesAsync(accountId, ct).ConfigureAwait(false);
    }

    public async Task ResetTwoStepAsync(
        RequestAuthority actor,
        Guid accountId,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var targetProfile = await ProfileOfUserAsync(target, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, targetProfile?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        if (actor.AccountId == accountId)
        {
            throw new InvalidOperationException("Use Account > Security to turn off your own two-step codes.");
        }

        RequireNotAdministratorTarget(targetProfile, householdOnly);

        // Switching off an administrator's second sign-in step needs a person who is an unlocked administrator.
        if (targetProfile is { IsServerAdministrator: true }
            && (actor.PrincipalKind != PrincipalKind.Human || !actor.ActiveProfileIsServerAdministrator))
        {
            throw new UnauthorizedAccessException("Only an unlocked administrator can turn off an administrator's two-step codes.");
        }

        if (!await firstParty.ResetTwoStepAsync(accountId, "administrator_reset", ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Two-step codes are not on for this account.");
        }

        await ChangedAsync(actor, "account.two_step_reset", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(
        RequestAuthority actor,
        Guid userId,
        IReadOnlyCollection<Guid>? confirmedProfileIds = null,
        CancellationToken ct = default)
    {
        // Held across the last-administrator checks and the save, so two people removing each other cannot both pass.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var doomed = await accounts.GetByIdAsync(userId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User not found.");
        var plan = await accounts.PlanUserRemovalAsync(userId, ct).ConfigureAwait(false);

        // Every rule is checked before anything is deleted.
        if (await UserDeletionRefusalAsync(actor, doomed, plan, ct).ConfigureAwait(false) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        // What goes must be what the caller confirmed (the grant paths hold the same gate, so nothing moves between this and the delete).
        var goingProfiles = plan.RemovesHousehold ? plan.HouseholdProfiles : [];
        // A household removal always needs the list the person confirmed: a delete sent without it (for a sign-in-only delete that
        // became a household removal in between) is refused too, so nothing is ever removed unseen.
        if (confirmedProfileIds is null
            ? plan.RemovesHousehold
            : !confirmedProfileIds.ToHashSet().SetEquals(goingProfiles.Select(profile => profile.Id)))
        {
            throw new AccountRemovalChangedException();
        }

        // Every refusal is behind us. A household removal deletes every profile's photos and videos with it (D18), never moves them:
        // their file lists are taken first, and the files themselves go once the delete has committed.
        var photoPlans = new List<ProfilePhotoPlan>();
        foreach (var profile in goingProfiles)
        {
            photoPlans.Add(await photoDisposer.PrepareAsync(profile, ProfilePhotoDisposition.Delete, actor.ActiveProfileId ?? profile.Id, ct).ConfigureAwait(false));
        }

        var outcome = await accounts.DeleteUserAsync(
            userId, goingProfiles.Select(profile => profile.Id).ToArray(), ct).ConfigureAwait(false);

        foreach (var photoPlan in photoPlans)
        {
            await photoDisposer.CompleteAsync(photoPlan, ct).ConfigureAwait(false);
        }

        // The removed household's Shared Library photos and videos went with it; their files follow.
        if (outcome.SharedFilesToDelete is { Count: > 0 } sharedFiles)
        {
            await photoDisposer.CompleteAsync(new ProfilePhotoPlan(sharedFiles), ct).ConfigureAwait(false);
        }

        // Every user whose sign-in is gone has their open sessions ended and their paired apps re-checked.
        foreach (var removed in outcome.DeletedUserIds.Where(id => id != userId))
        {
            await invalidation.InvalidateAccountAsync(removed, ct).ConfigureAwait(false);
        }

        // How many profiles went is recorded; never their names.
        await ChangedAsync(actor, "account.deleted", "account", userId.ToString("D"),
            userId, null, ct,
            new Dictionary<string, string?>
            {
                ["profiles_deleted"] = outcome.DeletedProfileIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["household_removed"] = outcome.RemovedHouseholdId is null ? "false" : "true",
            }).ConfigureAwait(false);
        if (outcome.RemovedHouseholdId is { } removedHousehold)
        {
            await ChangedAsync(actor, "household.deleted", "household", removedHousehold.ToString("D"),
                userId, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The one sentence that explains why this user cannot be deleted, or <see langword="null"/> when it can.</summary>
    private async Task<string?> UserDeletionRefusalAsync(
        RequestAuthority actor, Account doomed, UserRemovalPlan plan, CancellationToken ct)
    {
        if (actor.AccountId == doomed.Id)
        {
            return "You can't delete the sign-in you are using.";
        }

        // A server administrator's sign-in can go only while another one who can sign in remains.
        if (doomed.IsEnabled && plan.ProfileIsServerAdministrator &&
            !await usableAdministrators.HasUsableAdministratorSignInAfterAsync(
                AdministratorSignInLoss.OfAccount(doomed.Id), ct).ConfigureAwait(false))
        {
            return LastAdministratorMessage;
        }

        // A household removal deletes its photos and videos with it (D18), so photos never stand in the way.
        return null;
    }

    private async Task<bool> AnyPersonalPhotosAsync(IEnumerable<Profile> profilesToCheck, CancellationToken ct)
    {
        foreach (var profile in profilesToCheck)
        {
            if (await photoDisposer.HasPersonalPhotosAsync(profile.Id, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A delete failed after photos had already moved into the Shared Library (an unexpected error: every refusal runs first).
    /// Says so, by profile id only (never names), so the person can find the photos.
    /// </summary>
    private void LogPhotosLeftInShared(IEnumerable<ProfilePhotoPlan> plans)
    {
        foreach (var plan in plans.Where(plan => plan.PhotosMovedToShared))
        {
            (logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountAccessMutationService>.Instance).LogError(
                "Deleting profile {ProfileId} failed after its photos were moved to the Shared Library; the photos are now in Shared.",
                plan.ProfileId);
        }
    }

    public async Task<UserDeletionPreview> PreviewDeleteUserAsync(
        RequestAuthority actor,
        Guid userId,
        CancellationToken ct = default)
    {
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var doomed = await accounts.GetByIdAsync(userId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("User not found.");
        var plan = await accounts.PlanUserRemovalAsync(userId, ct).ConfigureAwait(false);
        return new UserDeletionPreview(
            plan,
            await UserDeletionRefusalAsync(actor, doomed, plan, ct).ConfigureAwait(false),
            plan.RemovesHousehold && (plan.HouseholdHoldsSharedItems || await AnyPersonalPhotosAsync(plan.HouseholdProfiles, ct).ConfigureAwait(false)));
    }

    public async Task<Profile> AddHouseholdPersonAsync(
        RequestAuthority actor,
        AddHouseholdPersonCommand command,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same grants.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireHouseholdWriteAsync(actor, command.HouseholdId, ct).ConfigureAwait(false);
        var name = NormalizeDisplayName(command.DisplayName);
        var color = NormalizeAvatarColor(command.AvatarColor);
        // The people of a household are opened by its users (they switch inside the household), so it needs at least one.
        var openers = await UsersOfHouseholdAsync(command.HouseholdId, ct).ConfigureAwait(false);
        if (openers.Count == 0)
        {
            throw new InvalidOperationException("This household has no sign-in to open the new person.");
        }

        var now = clock.GetUtcNow();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = name,
            AvatarColor = color,
            AvatarIcon = ProfileAvatarIcons.Normalize(command.AvatarIcon),
            // Left out, a Kids profile starts on PG; an explicit blank means Everything.
            ContentLimit = command.ContentLimit is null
                ? (command.IsChild ? ProfileContentLimits.KidsDefault : null)
                : ProfileContentLimits.Normalize(command.ContentLimit),
            ContentLimitAllowUnrated = command.ContentLimitAllowUnrated,
            Role = command.IsChild ? ProfileRole.RestrictedProfile : ProfileRole.StandardUser,
            CreatedAt = now,
        };
        await accounts.CreateHouseholdPersonAsync(
            profile, command.HouseholdId, ct,
            await AccessSourceProfileAsync(actor, command.HouseholdId, ct).ConfigureAwait(false)).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(command.Pin))
        {
            try
            {
                await firstParty.SetProfilePinAsync(profile.Id, command.Pin.Trim(), ct).ConfigureAwait(false);
            }
            catch
            {
                // A PIN that does not meet the rules must not leave a half-made person behind.
                await accounts.DeleteManagedProfileAsync(profile.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        foreach (var opener in openers.Skip(1))
        {
            await invalidation.InvalidateAccountAsync(opener.Id, ct).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "household.person_added", "profile", profile.Id.ToString("D"),
            openers[0].Id, profile.Id, ct).ConfigureAwait(false);
        return profile;
    }

    /// <summary>
    /// The profile a new person (one without a user) takes their starting access from: the profile of the person
    /// adding them. An application or service caller has none, so the household's first household-administrator profile is used;
    /// with none of those, <see langword="null"/> (every feature, no library).
    /// </summary>
    private async Task<Guid?> AccessSourceProfileAsync(RequestAuthority actor, Guid? householdId, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human && actor.ActiveProfileId is { } active)
        {
            return active;
        }

        if (householdId is not { } household)
        {
            return null;
        }

        return (await profiles.GetAllAsync(ct).ConfigureAwait(false))
            .Where(profile => profile.HouseholdId == household && profile.IsHouseholdAdministrator)
            .OrderBy(profile => profile.CreatedAt)
            .FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Refuses a change that would leave no enabled server administrator with a working sign-in under the current
    /// sign-in policy. Judged after the change, so a second administrator who can sign in makes it allowed. Callers hold
    /// <c>mutationGate</c> from this check until the change is saved. The data store keeps its own, simpler
    /// last-administrator rule as a backstop.
    /// </summary>
    private async Task RequireAdministratorRemainsAsync(AdministratorSignInLoss loss, CancellationToken ct)
    {
        if (!await usableAdministrators.HasUsableAdministratorSignInAfterAsync(loss, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(LastAdministratorMessage);
        }
    }

    /// <summary>True when the profile is a server administrator and an enabled user is attached to it.</summary>
    private async Task<bool> HoldsAdministratorAccessAsync(Guid profileId, CancellationToken ct) =>
        await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) is { IsServerAdministrator: true } &&
        (await accounts.GetAllAsync(ct).ConfigureAwait(false)).Any(account => account.ProfileId == profileId && account.IsEnabled);

    public async Task<Profile> UpdateProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        UpdateManagedProfileCommand command,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        if (householdOnly)
        {
            await RequireNotAdministratorProfileAsync(profile, actor, ct).ConfigureAwait(false);
        }

        profile.DisplayName = NormalizeDisplayName(command.DisplayName);
        profile.AvatarColor = NormalizeAvatarColor(command.AvatarColor);
        if (command.AvatarIcon is not null)
        {
            // Omitted keeps the current icon; an empty string clears it (back to the initial).
            profile.AvatarIcon = ProfileAvatarIcons.Normalize(command.AvatarIcon);
        }

        var limitBefore = (profile.ContentLimit, profile.ContentLimitAllowUnrated);
        if (command.ContentLimit is not null)
        {
            // Omitted keeps the current limit; an empty string means Everything.
            profile.ContentLimit = ProfileContentLimits.Normalize(command.ContentLimit);
        }

        if (command.ContentLimitAllowUnrated is { } allowUnrated)
        {
            profile.ContentLimitAllowUnrated = allowUnrated;
        }

        await accounts.UpdateManagedProfileAsync(profile, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.updated", "profile", profile.Id.ToString("D"),
            actor.AccountId ?? Guid.Empty, profile.Id, ct).ConfigureAwait(false);
        if (limitBefore != (profile.ContentLimit, profile.ContentLimitAllowUnrated))
        {
            await ChangedAsync(actor, "profile.content_limit_changed", "profile", profile.Id.ToString("D"),
                actor.AccountId ?? Guid.Empty, profile.Id, ct).ConfigureAwait(false);
        }

        return profile;
    }

    public async Task RequireCanChangeProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        if (householdOnly)
        {
            await RequireNotAdministratorProfileAsync(profile, actor, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        ProfilePhotoDisposition photos = ProfilePhotoDisposition.MoveToShared,
        CancellationToken ct = default)
    {
        // Held across the last-administrator check and the save, so two administrators removing each other cannot both pass.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireProfileScopeAsync(actor, profile, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        // The person deleting never removes the profile they are using, or the one their own sign-in opens.
        if (actor.ActiveProfileId == profileId ||
            (actor.AccountId is { } actorUser &&
             (await accounts.GetByIdAsync(actorUser, ct).ConfigureAwait(false))?.ProfileId == profileId))
        {
            throw new InvalidOperationException("You can't delete the profile you are using. Switch to another profile first.");
        }

        if (profile.IsServerAdministrator)
        {
            // A household administrator never touches a server administrator's profile; a server administrator may remove one
            // only while another server administrator can still sign in.
            if (householdOnly)
            {
                throw new UnauthorizedAccessException("Only a server administrator can delete a server administrator's profile.");
            }

            if (await HoldsAdministratorAccessAsync(profileId, ct).ConfigureAwait(false))
            {
                await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfProfile(profileId), ct).ConfigureAwait(false);
            }
        }

        // Check every removal rule first (the household keeps a household administrator, the Owner profile stays), so photos
        // are never moved for a person who then cannot be removed.
        await accounts.ValidateManagedProfileRemovalAsync(profileId, ct).ConfigureAwait(false);

        // Every refusal is behind us: only now do photos move, then the delete (which takes the profile's user with it).
        var plan = await photoDisposer.PrepareAsync(profile, photos, actor.ActiveProfileId ?? profileId, ct).ConfigureAwait(false);
        ProfileRemovalOutcome outcome;
        try
        {
            outcome = await accounts.DeleteManagedProfileAsync(profileId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogPhotosLeftInShared([plan]);
            throw;
        }

        await photoDisposer.CompleteAsync(plan, ct).ConfigureAwait(false);
        if (outcome.DeletedUserId is { } removedUser)
        {
            await invalidation.InvalidateAccountAsync(removedUser, ct).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "profile.deleted", "profile", profileId.ToString("D"),
            actor.AccountId ?? Guid.Empty, profileId, ct,
            new Dictionary<string, string?> { ["sign_in_removed"] = outcome.DeletedUserId is null ? "false" : "true" }).ConfigureAwait(false);
    }

    public async Task ReplaceProfileAccessAsync(
        RequestAuthority actor,
        Guid profileId,
        IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries,
        CancellationToken ct = default)
    {
        // Held so the access a household administrator may hand out is the one they hold at the moment it is saved.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireProfileScopeAsync(actor, profile, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        ValidateLibraries(libraries);
        if (householdOnly)
        {
            // A household administrator never changes a server administrator's access, and hands out nothing beyond their own.
            if (profile.IsServerAdministrator)
            {
                throw new UnauthorizedAccessException("Only a server administrator can change a server administrator's access.");
            }

            (features, libraries) = await MergeHouseholdAccessAsync(actor, profileId, features, libraries, ct).ConfigureAwait(false);
        }

        // The profile's version goes up with the change, so its open sessions and paired apps check again.
        if (!await profileAccess.ReplaceAccessAsync(profileId, features, libraries, clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        await ChangedAsync(actor, "profile.access_replaced", "profile", profileId.ToString("D"),
            await profileAccess.FindUserIdOfProfileAsync(profileId, ct).ConfigureAwait(false) ?? Guid.Empty, profileId, ct)
            .ConfigureAwait(false);
    }

    public async Task SetProfileRolesAsync(
        RequestAuthority actor,
        Guid profileId,
        bool isHouseholdAdministrator,
        bool isServerAdministrator,
        CancellationToken ct = default)
    {
        // Held across the last-administrator checks and the save, so two changes cannot each leave the other nobody.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireProfileScopeAsync(actor, profile, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        var changesServer = isServerAdministrator != profile.IsServerAdministrator;
        var changesHousehold = isHouseholdAdministrator != profile.IsHouseholdAdministrator;
        if (!changesServer && !changesHousehold)
        {
            return;
        }

        var userId = await profileAccess.FindUserIdOfProfileAsync(profileId, ct).ConfigureAwait(false);
        if (householdOnly && profile.IsServerAdministrator)
        {
            throw new UnauthorizedAccessException("Only a server administrator can change a server administrator's roles.");
        }

        // Every rule is checked before either role is written.
        if (changesServer)
        {
            if (householdOnly)
            {
                throw new UnauthorizedAccessException("Only a server administrator can change who is a server administrator.");
            }

            if (isServerAdministrator)
            {
                if (profile.Role == ProfileRole.RestrictedProfile)
                {
                    throw new InvalidOperationException("Child profiles can't be administrators.");
                }

                // D17: the default household only, and a user with an email and a password or passkey.
                await RequireInDefaultHouseholdAsync(profile, ct).ConfigureAwait(false);
                if (userId is not { } adminUser || !await accounts.IsUserSecuredAsync(adminUser, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(HouseholdRules.ServerAdministratorNeedsSecuredSignIn);
                }
            }
            else if (await HoldsAdministratorAccessAsync(profileId, ct).ConfigureAwait(false))
            {
                // At least one server administrator who can sign in always remains.
                await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfProfile(profileId), ct).ConfigureAwait(false);
            }
        }

        if (changesHousehold && isHouseholdAdministrator)
        {
            // D15: a household administrator always has a user, and is never a Kids profile.
            if (userId is null)
            {
                throw new InvalidOperationException("A household administrator needs a sign-in. Attach one to this profile first.");
            }

            if (profile.Role == ProfileRole.RestrictedProfile)
            {
                throw new InvalidOperationException("A Kids profile can't look after a household.");
            }
        }

        if (changesHousehold && !isHouseholdAdministrator && profile.HouseholdId is not null &&
            !await profileAccess.HasAnotherHouseholdAdministratorAsync(profileId, ct).ConfigureAwait(false))
        {
            // D7: a household always keeps one household administrator profile with an enabled user.
            throw new InvalidOperationException(UserRemovalMessages.LastHouseholdAdministrator);
        }

        var now = clock.GetUtcNow();
        if (changesServer)
        {
            await profileAccess.SetServerAdministratorAsync(profileId, isServerAdministrator, now, ct).ConfigureAwait(false);
        }

        if (changesHousehold)
        {
            await profileAccess.SetHouseholdAdministratorAsync(profileId, isHouseholdAdministrator, now, ct).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "profile.roles_changed", "profile", profileId.ToString("D"),
            userId ?? Guid.Empty, profileId, ct,
            new Dictionary<string, string?>
            {
                ["household_administrator"] = isHouseholdAdministrator ? "true" : "false",
                ["server_administrator"] = isServerAdministrator ? "true" : "false",
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Authorizes an action on one profile. As <see cref="RequireHouseholdWriteAsync"/>, except that a household administrator who
    /// reaches for a profile in another household (or one that does not exist) is told it does not exist, so other households
    /// can't be probed. Returns <see langword="true"/> when the actor is limited to their own household.
    /// </summary>
    private async Task<bool> RequireProfileScopeAsync(RequestAuthority actor, Profile? profile, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human && !actor.IsEffectiveAdministrator && actor.IsEffectiveHouseholdAdministrator
            && (profile is null || profile.HouseholdId != actor.ActiveProfileHouseholdId))
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        return await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True only for the passwordless first administrator of a Library that has no server administrator yet and at most one
    /// household (D17). Every later server administrator needs an email and a password or passkey.
    /// </summary>
    private async Task<bool> IsPasswordlessFirstAdministratorAsync(CancellationToken ct)
    {
        if ((await profiles.GetAllAsync(ct).ConfigureAwait(false)).Any(profile => profile.IsServerAdministrator))
        {
            return false;
        }

        return households is null || (await households.ListSummariesAsync(ct).ConfigureAwait(false)).Count <= 1;
    }

    /// <summary>
    /// Refuses starting a second household while any server administrator has no email and password (or passkey) yet (D10). A Library
    /// with one household may be shared without passwords; several households must not be open to anyone who reaches it.
    /// </summary>
    private async Task RequireAdministratorsSecuredAsync(CancellationToken ct)
    {
        if (!await accounts.AreServerAdministratorsSecuredAsync(ct).ConfigureAwait(false))
        {
            throw new SecureAdministratorsFirstException();
        }
    }

    public async Task SetProfileAdminProtectionAsync(
        RequestAuthority actor,
        Guid profileId,
        ProfileAdminProtectionCommand command,
        CancellationToken ct = default)
    {
        var protectedProfile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireProfileScopeAsync(actor, protectedProfile, ct).ConfigureAwait(false);
        if (protectedProfile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        if (householdOnly && !await IsActorsOwnProfileAsync(actor, profileId, ct).ConfigureAwait(false))
        {
            // The protection belongs to the profile. A household administrator changes it only on the profile their own user opens,
            // never on another administrator's (server or household), however the request names it.
            throw new UnauthorizedAccessException("A household administrator can only protect their own administrator access.");
        }

        // Only a profile that holds an administrator role has administrator pages to protect.
        if (!protectedProfile.IsServerAdministrator && !protectedProfile.IsHouseholdAdministrator)
        {
            throw new InvalidOperationException("Administrator access is not enabled for this profile.");
        }

        if (!Enum.IsDefined(command.UnlockMode))
        {
            throw new ArgumentException("Unknown administrator unlock mode.");
        }

        // One PIN: the question can only be switched on for a profile that has a PIN to ask for.
        if (command.Enabled &&
            await identities.GetCredentialAsync(profileId, ProfileCredentialKind.ProfilePin, ct).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("Set a PIN on this profile first. Admin pages ask for the same PIN as the profile.");
        }

        var protection = new ProfileAdminProtection
        {
            ProfileId = profileId,
            IsEnabled = command.Enabled,
            UnlockMode = command.UnlockMode.ToString(),
            UnlockMinutes = command.UnlockMode == AdminUnlockMode.FixedDuration
                ? Math.Clamp(command.UnlockMinutes ?? 30, 1, 120)
                : null,
            ProtectionVersion = 1,
            UpdatedAt = clock.GetUtcNow(),
        };
        await profileAccess.SetAdminProtectionAsync(protection, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.admin_protection_changed", "profile", profileId.ToString("D"),
            await profileAccess.FindUserIdOfProfileAsync(profileId, ct).ConfigureAwait(false) ?? Guid.Empty, profileId, ct)
            .ConfigureAwait(false);
    }

    public async Task<ProfileAdminProtection> GetProfileAdminProtectionAsync(
        RequestAuthority actor,
        Guid profileId,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = false;
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            householdOnly = await RequireProfileScopeAsync(actor, profile, ct).ConfigureAwait(false);
        }
        else
        {
            await RequireReadAsync(actor, ct).ConfigureAwait(false);
        }

        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        if (householdOnly && !await IsActorsOwnProfileAsync(actor, profileId, ct).ConfigureAwait(false))
        {
            // Another administrator's setting is theirs (and the server administrator's) to see.
            throw new UnauthorizedAccessException("A household administrator can only see their own administrator PIN setting.");
        }

        return await profileAccess.GetAdminProtectionAsync(profileId, ct).ConfigureAwait(false)
            ?? new ProfileAdminProtection
            {
                ProfileId = profileId,
                IsEnabled = false,
                UnlockMode = AdminUnlockMode.FixedDuration.ToString(),
                UnlockMinutes = 30,
                ProtectionVersion = 0,
            };
    }

    /// <summary>True when <paramref name="profileId"/> is the profile the actor's own user opens.</summary>
    private async Task<bool> IsActorsOwnProfileAsync(RequestAuthority actor, Guid profileId, CancellationToken ct) =>
        actor.AccountId is { } actorUser
        && (await accounts.GetByIdAsync(actorUser, ct).ConfigureAwait(false))?.ProfileId == profileId;

    /// <summary>The profile a user opens, or <see langword="null"/> for no user.</summary>
    private async Task<Profile?> ProfileOfUserAsync(Account? user, CancellationToken ct) =>
        user is null ? null : await profiles.GetByIdAsync(user.ProfileId, ct).ConfigureAwait(false);

    /// <summary>Every user attached to a profile of the household.</summary>
    private async Task<IReadOnlyList<Account>> UsersOfHouseholdAsync(Guid householdId, CancellationToken ct)
    {
        var householdProfiles = (await profiles.GetAllAsync(ct).ConfigureAwait(false))
            .Where(profile => profile.HouseholdId == householdId)
            .Select(profile => profile.Id)
            .ToHashSet();
        return (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => householdProfiles.Contains(account.ProfileId))
            .ToList();
    }

    public async Task SetProfilePinAsync(
        RequestAuthority actor,
        Guid profileId,
        string? pin,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }
        if (householdOnly)
        {
            // A PIN on an administrator's own person could lock the administrator out of it.
            await RequireNotAdministratorProfileAsync(profile, actor, ct).ConfigureAwait(false);
        }

        await firstParty.SetProfilePinAsync(profileId, pin, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.pin_changed", "profile", profileId.ToString("D"),
            actor.AccountId ?? Guid.Empty, profileId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Authorizes an action on one household. A server administrator (or a trusted application) reaches every household;
    /// a household administrator reaches only their own and never server settings. Returns <see langword="true"/> when the
    /// actor is limited to their own household, so the caller can apply the extra household-only rules. Throws when the
    /// actor has no authority over <paramref name="householdId"/>, including when the target does not exist.
    /// </summary>
    private async Task<bool> RequireHouseholdWriteAsync(RequestAuthority actor, Guid? householdId, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            var administrator = await accountDecisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false);
            if (administrator.IsAllowed)
            {
                return false;
            }

            // A server administrator who has not unlocked yet gets no household shortcut around the unlock.
            // A household administrator follows the same unlock rule as a server administrator: when they turned on
            // their administrator PIN, it must be unlocked.
            if (!actor.IsEffectiveAdministrator && householdId is { } household && actor.ActiveProfileHouseholdId == household &&
                (await accountDecisions.EvaluateHouseholdAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return true;
            }

            throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
        }

        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>A household administrator can't act on a user of a server administrator's or another household administrator's profile.</summary>
    private static void RequireNotAdministratorTarget(Profile? targetProfile, bool householdOnly)
    {
        if (householdOnly && targetProfile is { } profile && (profile.IsServerAdministrator || profile.IsHouseholdAdministrator))
        {
            throw new UnauthorizedAccessException("Only a server administrator can change an administrator's sign-in.");
        }
    }

    /// <summary>
    /// A household administrator can't touch a person who is an administrator (server or household) other than themselves: the
    /// profile they are acting as, or the one their own sign-in opens, is always theirs to change.
    /// </summary>
    private async Task RequireNotAdministratorProfileAsync(
        Profile profile, RequestAuthority actor, CancellationToken ct, Guid? ignoring = null)
    {
        if (!profile.IsServerAdministrator && !profile.IsHouseholdAdministrator)
        {
            return;
        }

        if (profile.Id == actor.ActiveProfileId)
        {
            return;
        }

        // The sign-in that opens the profile (the actor's own, or one being deleted in this very step) does not protect it.
        foreach (var accountId in new[] { actor.AccountId, ignoring })
        {
            if (accountId is { } id && (await accounts.GetByIdAsync(id, ct).ConfigureAwait(false))?.ProfileId == profile.Id)
            {
                return;
            }
        }

        throw new UnauthorizedAccessException("Only a server administrator can change a person who is an administrator's own.");
    }

    /// <summary>
    /// A household administrator changes only what they hold. Against the profile's current access, a request may add or remove a
    /// feature or library only if the acting profile holds it; a grant the administrator does not hold is kept exactly as it is
    /// (whether or not the request lists it), and the request cannot add one. Returns the access to save.
    /// </summary>
    private async Task<(IReadOnlySet<AccountFeatureId> Features, IReadOnlySet<Guid> Libraries)> MergeHouseholdAccessAsync(
        RequestAuthority actor,
        Guid profileId,
        IReadOnlySet<AccountFeatureId> requestedFeatures,
        IReadOnlySet<Guid> requestedLibraries,
        CancellationToken ct)
    {
        var source = actor.ActiveProfileId;
        var heldFeatures = source is { } featureSource
            ? (await profileAccess.GetFeatureGrantsAsync(featureSource, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        var heldLibraries = source is { } librarySource
            ? (await profileAccess.GetLibraryGrantsAsync(librarySource, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        var currentFeatures = (await profileAccess.GetFeatureGrantsAsync(profileId, ct).ConfigureAwait(false)).ToHashSet();
        var currentLibraries = (await profileAccess.GetLibraryGrantsAsync(profileId, ct).ConfigureAwait(false)).ToHashSet();

        if (requestedFeatures.Any(feature => !currentFeatures.Contains(feature) && !heldFeatures.Contains(feature)) ||
            requestedLibraries.Any(library => !currentLibraries.Contains(library) && !heldLibraries.Contains(library)))
        {
            throw new UnauthorizedAccessException(
                "A household administrator can only hand out the libraries and features the household already has.");
        }

        var features = requestedFeatures.Where(heldFeatures.Contains)
            .Concat(currentFeatures.Where(feature => !heldFeatures.Contains(feature)))
            .ToHashSet();
        var libraries = requestedLibraries.Where(heldLibraries.Contains)
            .Concat(currentLibraries.Where(library => !heldLibraries.Contains(library)))
            .ToHashSet();
        return (features, libraries);
    }

    /// <summary>
    /// A server administrator profile lives in the Library's own (default) household (users, profiles and households spec D17). With
    /// no default household marked yet (the very first administrator) the profile's household becomes it.
    /// </summary>
    private async Task RequireInDefaultHouseholdAsync(Profile profile, CancellationToken ct)
    {
        var defaultHousehold = await profileAccess.GetDefaultHouseholdIdAsync(ct).ConfigureAwait(false);
        if (defaultHousehold is { } home && profile.HouseholdId != home)
        {
            throw new InvalidOperationException("A server administrator must belong to the Library's own household.");
        }
    }

    private async Task RequireWriteAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            var administrator = await accountDecisions.EvaluateAdministratorAsync(actor, true, ct)
                .ConfigureAwait(false);
            if (administrator.IsAllowed)
            {
                return;
            }

            throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
        }

        var application = await evaluator.EvaluateAsync(
            actor,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.IdentityUsersWrite),
            null,
            ct).ConfigureAwait(false);
        if (!application.IsAllowed)
        {
            throw new UnauthorizedAccessException("Application identity.users.write permission is required.");
        }

        if (actor.PrincipalKind == PrincipalKind.DelegatedUserClient)
        {
            var administrator = await accountDecisions.EvaluateAdministratorAsync(actor, true, ct)
                .ConfigureAwait(false);
            if (!administrator.IsAllowed)
            {
                throw new UnauthorizedAccessException("Delegated administrator authority is required.");
            }
        }
    }

    /// <summary>
    /// As <see cref="RequireWriteAsync"/> for reading users: an unlocked server administrator, or an application trusted with
    /// identity.users.read (a delegated client also needs its person to be an unlocked server administrator).
    /// </summary>
    private async Task RequireReadAsync(RequestAuthority actor, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human)
        {
            if ((await accountDecisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
            {
                return;
            }

            throw new UnauthorizedAccessException("Unlocked administrator authority is required.");
        }

        var application = await evaluator.EvaluateAsync(
            actor,
            new AuthorizationRequirement(ApplicationPermission: ApplicationPermissionIds.IdentityUsersRead),
            null,
            ct).ConfigureAwait(false);
        if (!application.IsAllowed)
        {
            throw new UnauthorizedAccessException("Application identity.users.read permission is required.");
        }

        if (actor.PrincipalKind == PrincipalKind.DelegatedUserClient &&
            !(await accountDecisions.EvaluateAdministratorAsync(actor, true, ct).ConfigureAwait(false)).IsAllowed)
        {
            throw new UnauthorizedAccessException("Delegated administrator authority is required.");
        }
    }

    private TimeSpan InvitationLifetime() => TimeSpan.FromHours(Math.Clamp(
        configuration.LoadCore().Auth.InvitationLifetimeHours,
        1,
        720));

    /// <summary>A temporary password lasts seven days, independent of the invitation lifetime.</summary>
    private DateTimeOffset TemporaryPasswordExpiry() => clock.GetUtcNow().Add(TemporaryPasswordPolicy.Lifetime);

    private void ValidateLibraries(IReadOnlySet<Guid> libraryIds)
    {
        var known = configuration.LoadLibraries().Libraries
            .Where(library => library.Kind == LibraryKinds.Catalogued)
            .Select(library => Guid.TryParse(library.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        var unknown = libraryIds.Where(id => !known.Contains(id)).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unknown catalogued library '{unknown[0]:D}'.");
        }
    }

    private async Task ChangedAsync(
        RequestAuthority actor,
        string eventType,
        string subjectType,
        string subjectId,
        Guid accountId,
        Guid? profileId,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        await invalidation.InvalidateAccountAsync(accountId, ct).ConfigureAwait(false);
        if (profileId is { } profile)
        {
            await invalidation.InvalidateGrantAsync(accountId, profile, ct).ConfigureAwait(false);
        }

        await audit.WriteAsync(new AuthorizationAuditEvent(
            eventType,
            clock.GetUtcNow(),
            actor.AccountId,
            actor.ActiveProfileId,
            actor.ApplicationId,
            subjectType,
            subjectId,
            details is null
                ? new Dictionary<string, string?> { ["changed"] = "true" }
                : new Dictionary<string, string?>(details) { ["changed"] = "true" }), ct).ConfigureAwait(false);
    }

    private static string NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("An email is needed for a sign-in that works away from home.");
        }

        try
        {
            return new MailAddress(raw.Trim()).Address;
        }
        catch (FormatException)
        {
            throw new ArgumentException("Enter a valid email address.");
        }
    }

    private static string NormalizeDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Display name is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 100)
        {
            throw new ArgumentException("Display name must be 100 characters or fewer.");
        }

        return trimmed;
    }

    private static string NormalizeAvatarColor(string? value)
    {
        var color = string.IsNullOrWhiteSpace(value) ? "#7C4DFF" : value.Trim();
        if (color.Length != 7 || color[0] != '#' || color[1..].Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Avatar color must use #RRGGBB format.");
        }

        return color.ToUpperInvariant();
    }
}
