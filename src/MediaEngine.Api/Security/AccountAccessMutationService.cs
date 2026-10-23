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
    ILogger<AccountAccessMutationService>? logger = null) : IAccountAccessMutationService
{
    /// <summary>What a person is told when a change would leave nobody able to sign in as a server administrator.</summary>
    public const string LastAdministratorMessage =
        "This is the last administrator who can sign in. Make someone else an administrator first.";

    public async Task<Account> CreateAsync(
        RequestAuthority actor,
        CreateAccountAccessCommand command,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same grants.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        if ((command.ProfileId is null) == (command.NewProfile is null))
        {
            throw new ArgumentException("Choose one existing profile or create one new default profile.");
        }

        Profile? newProfile = null;
        var profileId = command.ProfileId.GetValueOrDefault();
        if (command.NewProfile is { } requestedProfile)
        {
            profileId = Guid.NewGuid();
            newProfile = new Profile
            {
                Id = profileId,
                DisplayName = NormalizeDisplayName(requestedProfile.DisplayName),
                AvatarColor = NormalizeAvatarColor(requestedProfile.AvatarColor),
                // The profile that carries an administrator account is a standard profile; child profiles never administer.
                Role = command.IsAdministrator ? ProfileRole.StandardUser : ProfileRole.RestrictedProfile,
                CreatedAt = clock.GetUtcNow(),
            };
        }
        else if (await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) is not { } existingProfile)
        {
            throw new KeyNotFoundException("Profile not found.");
        }
        else if (command.IsAdministrator && existingProfile.Role == ProfileRole.RestrictedProfile)
        {
            throw new InvalidOperationException("Child profiles can't be administrators.");
        }
        else if (command.IsAdministrator)
        {
            await RequireInDefaultHouseholdAsync(existingProfile, ct).ConfigureAwait(false);
        }
        ValidateLibraries(command.Libraries);

        var now = clock.GetUtcNow();
        var email = NormalizeEmail(command.Email);
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            IsAdministrator = command.IsAdministrator,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            // A server administrator lives in the Library's own (default) household (D17); anyone else starts their own household.
            HouseholdId = newProfile is not null && command.IsAdministrator
                ? await profileAccess.GetDefaultHouseholdIdAsync(ct).ConfigureAwait(false)
                : null,
        };
        var grant = new AccountProfileGrant
        {
            AccountId = account.Id,
            ProfileId = profileId,
            IsDefault = true,
            IsEnabled = true,
            AdminEnabled = command.IsAdministrator,
            AuthorizationVersion = 1,
            GrantedAt = now,
        };
        // A brand-new profile takes the roles and access chosen here. A person who already exists keeps exactly what they have:
        // attaching a sign-in never changes it (only an explicit administrator choice below turns the server administrator role on).
        await accounts.CreateAccountAsync(
            account, grant, command.Features, command.Libraries, ct, newProfile,
            preserveProfileAccess: newProfile is null).ConfigureAwait(false);
        if (newProfile is null && command.IsAdministrator)
        {
            await profileAccess.SetServerAdministratorAsync(profileId, true, now, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(command.TemporaryPassword))
        {
            try
            {
                await firstParty.SetTemporaryPasswordAsync(
                    account.Id, command.TemporaryPassword, TemporaryPasswordExpiry(), ct: ct).ConfigureAwait(false);
            }
            catch
            {
                // A password that does not meet the rules must not leave a half-made account behind.
                await accounts.DeleteAccountAsync(account.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

        }

        account = await accounts.GetByIdAsync(account.Id, ct).ConfigureAwait(false) ?? account;
        await ChangedAsync(actor, "account.created", "account", account.Id.ToString("D"),
            account.Id, null, ct).ConfigureAwait(false);
        await AuditIfHouseholdAdminAsync(actor, account.Id, ct).ConfigureAwait(false);
        return account;
    }

    public async Task SetTemporaryPasswordAsync(
        RequestAuthority actor,
        Guid accountId,
        string temporaryPassword,
        bool mustChangePassword = true,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        if (actor.AccountId == accountId)
        {
            throw new InvalidOperationException("Use Account > Security to change your own password.");
        }

        RequireNotAdministratorTarget(target, householdOnly);

        // Taking over an administrator's sign-in needs a person who is an unlocked administrator, not an application.
        if (target.IsAdministrator
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
        var householdOnly = await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        RequireMayChangeEmail(actor, target, householdOnly);

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

    /// <summary>
    /// The rules for moving someone's sign-in address, shared by <see cref="ChangeEmailAsync"/> and the email part of
    /// <see cref="UpdateAsync"/> so neither route can do what the other refuses.
    /// </summary>
    private static void RequireMayChangeEmail(RequestAuthority actor, Account target, bool householdOnly)
    {
        if (actor.AccountId == target.Id)
        {
            throw new InvalidOperationException("Email changes are made by an administrator, not for your own sign-in.");
        }

        RequireNotAdministratorTarget(target, householdOnly);

        // Taking over an administrator's sign-in address would let its password reset reach someone else.
        if (target.IsAdministrator
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
        var householdOnly = await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        if (actor.AccountId == accountId)
        {
            throw new InvalidOperationException("Use Account > Security to turn off your own two-step codes.");
        }

        RequireNotAdministratorTarget(target, householdOnly);

        // Switching off an administrator's second sign-in step needs a person who is an unlocked administrator.
        if (target.IsAdministrator
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

    public async Task<Account> UpdateAsync(
        RequestAuthority actor,
        Guid accountId,
        UpdateAccountAccessCommand command,
        CancellationToken ct = default)
    {
        // Held across the last-administrator check and the save, so two administrators removing each other cannot both pass.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Account not found.");
        if (account.GrantsInheritFromAccountId is not null && command.IsAdministrator)
        {
            throw new InvalidOperationException("A person's own sign-in can't be an administrator.");
        }

        if (account.IsEnabled && !command.IsEnabled)
        {
            await RequireNotLastMainSignInAsync(account, ct).ConfigureAwait(false);
        }

        // Switching an administrator off, or taking administrator away, must leave someone who can still sign in as one.
        if (account.IsEnabled && account.IsAdministrator && (!command.IsEnabled || !command.IsAdministrator))
        {
            await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfAccount(accountId), ct).ConfigureAwait(false);
        }

        var changesAdministrator = command.IsAdministrator != account.IsAdministrator;
        if (command.IsAdministrator && !account.IsAdministrator)
        {
            // The role is the profile's: the profile the user opens must be a grown-up's, in the Library's own household (D17).
            var ownProfile = account.ProfileId is { } ownId
                ? await profiles.GetByIdAsync(ownId, ct).ConfigureAwait(false)
                : null;
            if (ownProfile is null)
            {
                throw new InvalidOperationException("This user has no profile to make an administrator.");
            }

            if (ownProfile.Role == ProfileRole.RestrictedProfile)
            {
                throw new InvalidOperationException("Child profiles can't be administrators.");
            }

            await RequireInDefaultHouseholdAsync(ownProfile, ct).ConfigureAwait(false);
        }

        // An account made on this computer has no email yet; turning it on or off must not demand one.
        var emailChanged = false;
        if (!string.IsNullOrWhiteSpace(command.Email) || account.HasEmail)
        {
            var email = NormalizeEmail(command.Email);
            var emailKey = email.ToUpperInvariant();
            emailChanged = !string.Equals(account.NormalizedEmail, emailKey, StringComparison.Ordinal);
            if (emailChanged)
            {
                // RequireWriteAsync above already limits this route to an unlocked server administrator or a trusted application.
                RequireMayChangeEmail(actor, account, householdOnly: false);
                await RequireEmailAvailableAsync(accountId, emailKey, ct).ConfigureAwait(false);
            }

            account.Email = email;
            account.NormalizedEmail = emailKey;
        }

        account.IsEnabled = command.IsEnabled;
        account.IsAdministrator = command.IsAdministrator;
        account.UpdatedAt = clock.GetUtcNow();

        await UpdateAccountWithEmailGuardAsync(account, emailChanged, ct).ConfigureAwait(false);
        if (changesAdministrator && account.ProfileId is { } administratorProfile)
        {
            // The server administrator role is written on the profile, which keeps its own last-administrator backstop.
            await profileAccess.SetServerAdministratorAsync(
                administratorProfile, command.IsAdministrator, account.UpdatedAt, ct).ConfigureAwait(false);
        }

        if (emailChanged)
        {
            await InvalidateChallengesForOldEmailAsync(accountId, ct).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "account.updated", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
        return (await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false))!;
    }

    public async Task DeleteAsync(
        RequestAuthority actor,
        Guid accountId,
        ProfilePhotoDisposition photos = ProfilePhotoDisposition.MoveToShared,
        IReadOnlyCollection<Guid>? confirmedProfileIds = null,
        CancellationToken ct = default)
    {
        // Held across the last-administrator and last-owner checks and the save, so two people removing each other cannot both pass.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var doomed = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireAccountDeleteScopeAsync(actor, doomed, ct).ConfigureAwait(false);
        if (doomed is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }

        if (householdOnly)
        {
            // A household owner can delete another user of their household, another owner included, but never a server
            // administrator, and never their own sign-in (another owner does that).
            if (doomed.IsAdministrator)
            {
                throw new UnauthorizedAccessException("Only a server administrator can delete an administrator's sign-in.");
            }

            if (actor.AccountId == accountId)
            {
                throw new InvalidOperationException("Ask another owner of the household to delete your sign-in.");
            }
        }

        await RequireNotLastMainSignInAsync(doomed, ct).ConfigureAwait(false);
        if (doomed.IsEnabled && doomed.IsAdministrator)
        {
            await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfAccount(accountId), ct).ConfigureAwait(false);
        }

        await RequireNotLastOwnerAsync(doomed, ct).ConfigureAwait(false);

        // The user's own profiles go with them (see AccountRemovalPlan). Every rule is checked before any photo moves.
        var plan = await accounts.PlanAccountRemovalAsync(accountId, ct).ConfigureAwait(false);
        foreach (var profile in plan.OwnProfiles)
        {
            if (householdOnly)
            {
                if (actor.ActiveProfileId == profile.Id)
                {
                    throw new InvalidOperationException("Switch to another person before deleting this user.");
                }

                await RequireNotAdministratorProfileAsync(profile, actor, ct, ignoring: accountId).ConfigureAwait(false);
            }

            if (await HoldsAdministratorAccessAsync(profile.Id, ct).ConfigureAwait(false))
            {
                await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfProfile(profile.Id), ct).ConfigureAwait(false);
            }
        }

        // What goes must be what the caller confirmed (the grant paths hold the same gate, so nothing moves between this and the delete).
        if (confirmedProfileIds is not null && !confirmedProfileIds.ToHashSet().SetEquals(plan.OwnProfiles.Select(profile => profile.Id)))
        {
            throw new AccountRemovalChangedException();
        }

        // Every refusal is behind us: only now do photos move, then the delete.
        var photoPlans = new List<ProfilePhotoPlan>();
        AccountRemovalOutcome outcome;
        try
        {
            foreach (var profile in plan.OwnProfiles)
            {
                photoPlans.Add(await photoDisposer.PrepareAsync(profile, photos, actor.ActiveProfileId ?? profile.Id, ct).ConfigureAwait(false));
            }

            outcome = await accounts.DeleteAccountWithOwnProfilesAsync(
                accountId, plan.OwnProfiles.Select(profile => profile.Id).ToArray(), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogPhotosLeftInShared(photoPlans);
            throw;
        }

        foreach (var photoPlan in photoPlans)
        {
            await photoDisposer.CompleteAsync(photoPlan, ct).ConfigureAwait(false);
        }

        // Anyone who lost a grant to a deleted profile has their open sessions re-checked.
        foreach (var affected in outcome.AffectedAccountIds)
        {
            await invalidation.InvalidateAccountAsync(affected, ct).ConfigureAwait(false);
        }

        // How many profiles went is recorded; never their names.
        await ChangedAsync(actor, "account.deleted", "account", accountId.ToString("D"),
            accountId, null, ct,
            new Dictionary<string, string?> { ["profiles_deleted"] = outcome.DeletedProfileIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .ConfigureAwait(false);
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

    public async Task<IReadOnlyList<Profile>> PreviewDeleteAsync(
        RequestAuthority actor,
        Guid accountId,
        CancellationToken ct = default)
    {
        var doomed = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        await RequireAccountDeleteScopeAsync(actor, doomed, ct).ConfigureAwait(false);
        if (doomed is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }

        return (await accounts.PlanAccountRemovalAsync(accountId, ct).ConfigureAwait(false)).OwnProfiles;
    }

    /// <summary>
    /// Who may delete this sign-in: a server administrator or a trusted application anywhere, a household owner only inside their
    /// own household. Another household's sign-in answers exactly like a missing one, so it cannot be discovered. Returns true when
    /// the actor is limited to their own household.
    /// </summary>
    private async Task<bool> RequireAccountDeleteScopeAsync(RequestAuthority actor, Account? target, CancellationToken ct)
    {
        if (actor.PrincipalKind == PrincipalKind.Human && !actor.IsEffectiveAdministrator && actor.IsEffectiveHouseholdAdministrator
            && (target is null || target.HouseholdId != actor.ActiveProfileHouseholdId))
        {
            throw new KeyNotFoundException("Account not found.");
        }

        return await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A household that has an enabled owner (<c>household_admin</c>) keeps one: its last enabled owner cannot be deleted.
    /// Anyone, a server administrator included, is held to this.
    /// </summary>
    private async Task RequireNotLastOwnerAsync(Account doomed, CancellationToken ct)
    {
        if (!doomed.HouseholdAdmin || !doomed.IsEnabled || doomed.HouseholdId is not { } household)
        {
            return;
        }

        var otherOwner = (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Any(other => other.Id != doomed.Id && other.HouseholdId == household && other.HouseholdAdmin && other.IsEnabled);
        if (!otherOwner)
        {
            var name = await DisplayNameAsync(doomed, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"{name} is the last owner of this household. Make someone else an owner first.");
        }
    }

    /// <summary>The name a sentence uses for a user: the person they sign in as, else their email.</summary>
    private async Task<string> DisplayNameAsync(Account account, CancellationToken ct)
    {
        var profile = account.ProfileId is { } ownId ? await profiles.GetByIdAsync(ownId, ct).ConfigureAwait(false) : null;
        return !string.IsNullOrWhiteSpace(profile?.DisplayName) ? profile!.DisplayName
            : !string.IsNullOrWhiteSpace(account.Email) ? account.Email!
            : "This user";
    }

    public async Task<IssuedAccountInvitation> IssueInvitationAsync(
        RequestAuthority actor,
        IssueAccountInvitationCommand command,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same grants.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var email = NormalizeEmail(command.Email);
        var profileIds = command.ProfileIds.Distinct().ToArray();
        Profile? newPerson = null;
        if (command.NewHouseholdPersonName is not null)
        {
            // Someone outside the household: they start a household of their own, with one person to open.
            if (profileIds.Length != 0)
            {
                throw new ArgumentException("An invitation to a new household cannot also open existing profiles.");
            }

            if (command.DefaultProfileId is not null)
            {
                throw new ArgumentException("An invitation to a new household chooses its first person itself, so it cannot name a default profile.");
            }

            if (string.IsNullOrWhiteSpace(command.NewHouseholdPersonName))
            {
                throw new ArgumentException("Give the new household's first person a name.");
            }

            newPerson = new Profile
            {
                Id = Guid.NewGuid(),
                DisplayName = NormalizeDisplayName(command.NewHouseholdPersonName),
                AvatarColor = NormalizeAvatarColor(null),
                Role = ProfileRole.StandardUser,
                CreatedAt = clock.GetUtcNow(),
            };
            profileIds = [newPerson.Id];
        }
        else if (profileIds.Length is 0 or > 8)
        {
            throw new ArgumentException("An invitation must grant between one and eight profiles.");
        }

        var defaultProfileId = command.DefaultProfileId ?? profileIds[0];
        if (!profileIds.Contains(defaultProfileId))
        {
            throw new ArgumentException("The default profile must be included in the invitation.");
        }

        foreach (var profileId in profileIds.Where(id => newPerson is null || id != newPerson.Id))
        {
            if (await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false) is null)
            {
                throw new KeyNotFoundException($"Profile '{profileId:D}' was not found.");
            }
        }
        var now = clock.GetUtcNow();
        var account = await accounts.GetByNormalizedEmailAsync(email.ToUpperInvariant(), ct).ConfigureAwait(false);
        var isExisting = account is not null;
        account ??= new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            IsAdministrator = false,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        if (isExisting && newPerson is not null)
        {
            throw new InvalidOperationException("This email already has a sign-in, so it cannot start a new household.");
        }

        if (isExisting)
        {
            if (!account.IsEnabled ||
                await identities.GetAccountCredentialAsync(
                    account.Id, AccountCredentialKind.Password, ct).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("This account cannot receive an invitation.");
            }

            // A new invitation for a user who has not chosen a password yet opens the profile that user is attached to, and only
            // people of that user's own household.
            if (account.ProfileId != defaultProfileId || !profileIds.Contains(defaultProfileId))
            {
                throw new InvalidOperationException("Invitation profiles must match the account's current grants.");
            }

            foreach (var invitedProfile in profileIds)
            {
                if (!await accounts.HasProfileAccessAsync(account.Id, invitedProfile, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Invitation profiles must match the account's current grants.");
                }
            }
        }
        var grants = profileIds.Select(profileId => new AccountProfileGrant
        {
            AccountId = account.Id,
            ProfileId = profileId,
            IsDefault = profileId == defaultProfileId,
            IsEnabled = true,
            AdminEnabled = false,
            AuthorizationVersion = 1,
            GrantedAt = now,
        }).ToArray();
        var code = InvitationCode.Generate();
        var invitation = new AccountInvitation
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            TokenHash = InvitationCode.Hash(code),
            CreatedAt = now,
            ExpiresAt = now.Add(InvitationLifetime()),
        };

        if (isExisting)
        {
            await accounts.InsertInvitationAsync(invitation, ct).ConfigureAwait(false);
        }
        else
        {
            // A new person is set up from this invitation's account; people who already exist keep exactly what they have.
            await accounts.CreateInvitedAccountAsync(
                account, grants, invitation, ct, newPerson, preserveProfileAccess: newPerson is null).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "account.invitation_issued", "account", account.Id.ToString("D"),
            account.Id, defaultProfileId, ct).ConfigureAwait(false);
        await AuditIfHouseholdAdminAsync(actor, account.Id, ct).ConfigureAwait(false);
        return new IssuedAccountInvitation(account.Id, InvitationCode.Format(code), invitation.ExpiresAt);
    }

    public async Task<Profile> CreateProfileAsync(
        RequestAuthority actor,
        CreateManagedProfileCommand command,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same grants.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            DisplayName = NormalizeDisplayName(command.DisplayName),
            AvatarColor = NormalizeAvatarColor(command.AvatarColor),
            Role = ProfileRole.RestrictedProfile,
            CreatedAt = now,
        };
        if (await accounts.GetByIdAsync(command.AccountId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Target account not found.");
        }

        var targetGrant = NewProfileGrant(
            command.AccountId, profile.Id, command.IsDefault, now);
        var targetHousehold = (await accounts.GetByIdAsync(command.AccountId, ct).ConfigureAwait(false))?.HouseholdId;
        await accounts.CreateManagedProfileAsync(
            profile, targetGrant, ct, await AccessSourceProfileAsync(actor, targetHousehold, ct).ConfigureAwait(false)).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.created", "profile", profile.Id.ToString("D"),
            command.AccountId, profile.Id, ct).ConfigureAwait(false);
        return profile;
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
        var openers = (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => account.HouseholdId == command.HouseholdId && account.GrantsInheritFromAccountId is null)
            .ToList();
        if (openers.Count == 0)
        {
            throw new InvalidOperationException("This household has no main sign-in to open the new person.");
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
        var grants = openers.Select(account => NewProfileGrant(account.Id, profile.Id, false, now)).ToArray();
        await accounts.CreateHouseholdPersonAsync(
            profile, command.HouseholdId, grants, ct,
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

    public async Task<GivenOwnSignIn> GiveOwnSignInAsync(
        RequestAuthority actor,
        GiveOwnSignInCommand command,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same grants.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(command.ProfileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            throw new KeyNotFoundException("Person not found.");
        }
        if (profile.HouseholdId is not { } householdId)
        {
            throw new InvalidOperationException("This person is not in a household yet.");
        }
        if (householdOnly)
        {
            // A direct sign-in to an administrator's own person would sidestep that person's PIN and the administrator.
            await RequireNotAdministratorProfileAsync(profile, actor, ct).ConfigureAwait(false);
        }

        var members = (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => account.HouseholdId == householdId)
            .ToList();
        foreach (var member in members.Where(member => member.GrantsInheritFromAccountId is not null))
        {
            if (member.ProfileId == profile.Id)
            {
                throw new InvalidOperationException($"{profile.DisplayName} already has their own sign-in.");
            }
        }

        var main = members
            .Where(account => account.GrantsInheritFromAccountId is null && account.IsEnabled)
            .OrderBy(account => account.CreatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("This household has no main sign-in to take library access from.");
        // An own sign-in always has a password: either the one chosen here, or the one the person picks from an invitation.
        if (command.Invite && !string.IsNullOrEmpty(command.TemporaryPassword))
        {
            throw new ArgumentException("Choose a starting password or an invitation link, not both.");
        }
        if (!command.Invite && string.IsNullOrEmpty(command.TemporaryPassword))
        {
            throw new ArgumentException("Set a starting password.");
        }

        var email = NormalizeEmail(command.Email);
        if (await accounts.GetByNormalizedEmailAsync(email.ToUpperInvariant(), ct).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("That email already has a sign-in.");
        }

        var now = clock.GetUtcNow();
        var account = new Account
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            IsEnabled = true,
            IsAdministrator = false,
            AuthorizationVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
            HouseholdId = householdId,
            // Library and feature access follows the household's main sign-in, so later changes reach this person.
            GrantsInheritFromAccountId = main.Id,
        };
        var grant = NewProfileGrant(account.Id, profile.Id, true, now);
        IssuedAccountInvitation? issued = null;
        if (!command.Invite)
        {
            // The person already exists: their sign-in is attached to them and changes nothing about what they may do.
            await accounts.CreateAccountAsync(account, grant, new HashSet<AccountFeatureId>(), new HashSet<Guid>(), ct,
                preserveProfileAccess: true).ConfigureAwait(false);
            try
            {
                await firstParty.SetTemporaryPasswordAsync(
                    account.Id, command.TemporaryPassword!, TemporaryPasswordExpiry(), command.MustChangePassword, ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A password that does not meet the rules must not leave a half-made sign-in behind.
                await accounts.DeleteAccountAsync(account.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            account = await accounts.GetByIdAsync(account.Id, ct).ConfigureAwait(false) ?? account;
        }
        else
        {
            var code = InvitationCode.Generate();
            var invitation = new AccountInvitation
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                TokenHash = InvitationCode.Hash(code),
                CreatedAt = now,
                ExpiresAt = now.Add(InvitationLifetime()),
            };
            await accounts.CreateInvitedAccountAsync(account, [grant], invitation, ct, preserveProfileAccess: true).ConfigureAwait(false);
            issued = new IssuedAccountInvitation(account.Id, InvitationCode.Format(code), invitation.ExpiresAt);
        }

        await ChangedAsync(actor, "household.own_sign_in_given", "account", account.Id.ToString("D"),
            account.Id, profile.Id, ct).ConfigureAwait(false);
        return new GivenOwnSignIn(account, issued);
    }

    public async Task RemoveOwnSignInAsync(
        RequestAuthority actor,
        Guid accountId,
        CancellationToken ct = default)
    {
        // Held across the checks and the save, so a delete's plan and the delete itself see the same grants.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        await RequireHouseholdWriteAsync(actor, account?.HouseholdId, ct).ConfigureAwait(false);
        if (account is null)
        {
            throw new KeyNotFoundException("Sign-in not found.");
        }
        const string NotOwnSignIn = "That sign-in is not a person's own sign-in.";
        if (account.GrantsInheritFromAccountId is null || account.IsAdministrator || account.ProfileId is not { } profileId)
        {
            throw new InvalidOperationException(NotOwnSignIn);
        }

        // Removing the only way to open a person would strand them and their history.
        var someoneElseOpens = false;
        foreach (var other in (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(other => other.Id != accountId && other.HouseholdId == account.HouseholdId))
        {
            if (await accounts.HasProfileAccessAsync(other.Id, profileId, ct).ConfigureAwait(false))
            {
                someoneElseOpens = true;
                break;
            }
        }

        if (!someoneElseOpens)
        {
            throw new InvalidOperationException("Nobody else in the household can open this person, so their sign-in cannot be removed.");
        }

        // Deleting the account also ends its sessions and paired devices; the person stays in the household.
        await accounts.DeleteAccountAsync(accountId, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "household.own_sign_in_removed", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The profile a new person (one without a sign-in of their own) takes their starting access from: the profile of the person
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

    /// <summary>Own sign-ins follow a household's main sign-in, so the last enabled one must stay while any follow it.</summary>
    private async Task RequireNotLastMainSignInAsync(Account account, CancellationToken ct)
    {
        if (account.GrantsInheritFromAccountId is not null || account.HouseholdId is null)
        {
            return;
        }

        var household = (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(other => other.HouseholdId == account.HouseholdId && other.Id != account.Id)
            .ToList();
        if (household.Any(other => other.GrantsInheritFromAccountId is not null) &&
            !household.Any(other => other.GrantsInheritFromAccountId is null && other.IsEnabled))
        {
            throw new InvalidOperationException("Remove the household's own sign-ins first. They follow this sign-in's access.");
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
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        if (householdOnly && profile is not null)
        {
            if (actor.ActiveProfileId == profileId)
            {
                throw new InvalidOperationException("Switch to another person before removing this one.");
            }

            await RequireNotAdministratorProfileAsync(profile, actor, ct).ConfigureAwait(false);
        }

        if (profile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        // Check every removal rule first, so photos are never moved for a person who then cannot be removed.
        if (await HoldsAdministratorAccessAsync(profileId, ct).ConfigureAwait(false))
        {
            await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfProfile(profileId), ct).ConfigureAwait(false);
        }

        await accounts.ValidateManagedProfileRemovalAsync(profileId, ct).ConfigureAwait(false);

        // Every refusal is behind us: only now do photos move, then the delete.
        var plan = await photoDisposer.PrepareAsync(profile, photos, actor.ActiveProfileId ?? profileId, ct).ConfigureAwait(false);
        try
        {
            await accounts.DeleteManagedProfileAsync(profileId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogPhotosLeftInShared([plan]);
            throw;
        }

        await photoDisposer.CompleteAsync(plan, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.deleted", "profile", profileId.ToString("D"),
            actor.AccountId ?? Guid.Empty, profileId, ct).ConfigureAwait(false);
    }

    public async Task ReplaceAccessAsync(
        RequestAuthority actor,
        Guid accountId,
        IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
        if (target is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }
        ValidateLibraries(libraries);
        if (householdOnly)
        {
            RequireNotAdministratorTarget(target, householdOnly);
            await RequireWithinHouseholdAccessAsync(actor, features, libraries, ct).ConfigureAwait(false);
        }

        // What a user may use is its profile's: the access is written there, and the profile's version goes up so its open sessions
        // and paired apps check again.
        if (target.ProfileId is not { } targetProfile ||
            !await profileAccess.ReplaceAccessAsync(targetProfile, features, libraries, clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        await ChangedAsync(actor, "account.access_replaced", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
    }

    public async Task UpsertGrantAsync(
        RequestAuthority actor,
        AccountProfileGrant grant,
        CancellationToken ct = default)
    {
        // Held across the last-administrator check and the save, so two administrators removing each other cannot both pass.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var grantee = await accounts.GetByIdAsync(grant.AccountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, grantee?.HouseholdId, ct).ConfigureAwait(false);
        var grantedProfile = await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false);
        if (householdOnly)
        {
            // A household administrator opens people with sign-ins of their own household, and never makes an administrator.
            // A person who is missing or in another household gets the same answer, so other households can't be probed.
            if (grantee is null || grantedProfile is null || grantedProfile.HouseholdId != grantee.HouseholdId)
            {
                throw new UnauthorizedAccessException("A household administrator can only manage their own household.");
            }

            RequireNotAdministratorTarget(grantee, householdOnly);
            if (grant.AdminEnabled)
            {
                throw new UnauthorizedAccessException("Only a server administrator can turn on administrator access.");
            }
        }

        if (grantedProfile is null)
        {
            throw new KeyNotFoundException("Profile not found.");
        }

        if (await accounts.GetByIdAsync(grant.AccountId, ct).ConfigureAwait(false) is { GrantsInheritFromAccountId: not null } follower)
        {
            // A person's own sign-in opens only that person and is never an administrator.
            if (grant.AdminEnabled || follower.ProfileId != grant.ProfileId)
            {
                throw new InvalidOperationException("A person's own sign-in opens only that person and can't be an administrator.");
            }
        }

        // Administrator access is the profile's role, and a user can only hold it on the profile it opens.
        if (grant.AdminEnabled && grantee?.ProfileId != grant.ProfileId)
        {
            throw new InvalidOperationException("Administrator access belongs to the profile a sign-in opens.");
        }

        if (grant.AdminEnabled &&
            await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false) is { Role: ProfileRole.RestrictedProfile })
        {
            throw new InvalidOperationException("Child profiles can't be administrators.");
        }

        // Switching administrator access off for an administrator's grant must leave someone who can still sign in as one.
        var demotesAdministrator = grantee is { IsEnabled: true, IsAdministrator: true } &&
            grantee.ProfileId == grant.ProfileId && (!grant.IsEnabled || !grant.AdminEnabled);
        if (demotesAdministrator)
        {
            await RequireAdministratorRemainsAsync(
                AdministratorSignInLoss.OfGrant(grant.AccountId, grant.ProfileId), ct).ConfigureAwait(false);
        }

        grant.GrantedAt = clock.GetUtcNow();
        await accounts.UpsertGrantAsync(grant, ct).ConfigureAwait(false);
        if (demotesAdministrator)
        {
            await profileAccess.SetServerAdministratorAsync(grant.ProfileId, false, grant.GrantedAt, ct).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "account.grant_updated", "grant",
            $"{grant.AccountId:D}/{grant.ProfileId:D}", grant.AccountId, grant.ProfileId, ct)
            .ConfigureAwait(false);
    }

    public async Task RevokeGrantAsync(
        RequestAuthority actor,
        Guid accountId,
        Guid profileId,
        CancellationToken ct = default)
    {
        // Held across the last-administrator check and the save, so two administrators removing each other cannot both pass.
        using var mutation = await mutationGate.EnterAsync(ct).ConfigureAwait(false);
        var grantee = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, grantee?.HouseholdId, ct).ConfigureAwait(false);
        if (householdOnly && grantee is not null)
        {
            RequireNotAdministratorTarget(grantee, householdOnly);
        }

        // Taking an administrator's own profile away from them also takes the role: the role is the profile's.
        var losesAdministrator = grantee is { IsEnabled: true, IsAdministrator: true } && grantee.ProfileId == profileId;
        if (losesAdministrator)
        {
            await RequireAdministratorRemainsAsync(AdministratorSignInLoss.OfGrant(accountId, profileId), ct).ConfigureAwait(false);
        }

        await accounts.RevokeGrantAsync(accountId, profileId, ct).ConfigureAwait(false);
        if (losesAdministrator)
        {
            await profileAccess.SetServerAdministratorAsync(profileId, false, clock.GetUtcNow(), ct).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "account.grant_revoked", "grant",
            $"{accountId:D}/{profileId:D}", accountId, profileId, ct).ConfigureAwait(false);
    }

    public async Task SetAdminProtectionAsync(
        RequestAuthority actor,
        Guid accountId,
        Guid profileId,
        GrantAdminProtectionCommand command,
        CancellationToken ct = default)
    {
        var protectedAccount = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, protectedAccount?.HouseholdId, ct).ConfigureAwait(false);
        if (householdOnly && accountId != actor.AccountId)
        {
            // A household administrator protects their own household administration, and no one else's.
            throw new UnauthorizedAccessException("A household administrator can only protect their own administrator access.");
        }

        if (householdOnly)
        {
            // The protection belongs to the profile. A person changes it only on the profile their own sign-in opens; a household
            // administrator never changes another administrator's (server or household), however the request names it.
            var ownProfileId = actor.AccountId is { } actorAccountId
                ? (await accounts.GetByIdAsync(actorAccountId, ct).ConfigureAwait(false))?.ProfileId
                : null;
            if (ownProfileId != profileId)
            {
                throw new UnauthorizedAccessException("A household administrator can only protect their own administrator access.");
            }
        }

        // The question is about a profile the user can act as (its household), and only a profile that holds an administrator role has
        // administrator pages to protect.
        var protectedProfile = protectedAccount is not null &&
            await accounts.HasProfileAccessAsync(accountId, profileId, ct).ConfigureAwait(false)
            ? await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false)
            : null;
        if (protectedProfile is null)
        {
            throw new KeyNotFoundException("Profile grant not found.");
        }

        if (!protectedProfile.IsServerAdministrator && !protectedProfile.IsHouseholdAdministrator)
        {
            throw new InvalidOperationException("Administrator access is not enabled for this grant.");
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
        await ChangedAsync(actor, "grant.admin_protection_changed", "grant",
            $"{accountId:D}/{profileId:D}", accountId, profileId, ct).ConfigureAwait(false);
    }

    public async Task SetHouseholdAdminAsync(
        RequestAuthority actor,
        Guid accountId,
        bool isHouseholdAdmin,
        CancellationToken ct = default)
    {
        // Only a server administrator (or an application they trust) decides who looks after a household.
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Account not found.");
        if (account.HouseholdId is null)
        {
            throw new InvalidOperationException("This sign-in is not in a household.");
        }

        if (isHouseholdAdmin && account.GrantsInheritFromAccountId is not null)
        {
            throw new InvalidOperationException("A person's own sign-in can't be a household administrator.");
        }

        if (isHouseholdAdmin && !account.IsEnabled)
        {
            throw new InvalidOperationException("Turn this sign-in on before making it a household administrator.");
        }

        // The role is the profile's, and a household administrator always has a user: the profile this sign-in opens.
        var ownProfile = account.ProfileId is { } ownId
            ? await profiles.GetByIdAsync(ownId, ct).ConfigureAwait(false)
            : null;
        if (isHouseholdAdmin && ownProfile is null)
        {
            throw new InvalidOperationException("A household administrator needs a profile. Attach this sign-in to one first.");
        }

        if (isHouseholdAdmin && ownProfile is { Role: ProfileRole.RestrictedProfile })
        {
            throw new InvalidOperationException("A Kids profile can't look after a household.");
        }

        if (ownProfile is not null)
        {
            await profileAccess.SetHouseholdAdministratorAsync(ownProfile.Id, isHouseholdAdmin, clock.GetUtcNow(), ct).ConfigureAwait(false);
        }

        await accounts.SetHouseholdAdminAsync(accountId, isHouseholdAdmin, clock.GetUtcNow(), ct).ConfigureAwait(false);
        await ChangedAsync(actor, isHouseholdAdmin ? "household.admin_granted" : "household.admin_removed",
            "account", accountId.ToString("D"), accountId, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The first main sign-in of a household becomes its administrator when the account is created; that is a change of
    /// authority, so it is recorded like any other grant of household administration.
    /// </summary>
    private async Task AuditIfHouseholdAdminAsync(RequestAuthority actor, Guid accountId, CancellationToken ct)
    {
        if (await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) is { HouseholdAdmin: true })
        {
            await ChangedAsync(actor, "household.admin_granted", "account", accountId.ToString("D"),
                accountId, null, ct).ConfigureAwait(false);
        }
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

    /// <summary>A household administrator can't act on a server administrator or on another household administrator.</summary>
    private static void RequireNotAdministratorTarget(Account target, bool householdOnly)
    {
        if (householdOnly && (target.IsAdministrator || target.HouseholdAdmin))
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
    /// A household administrator hands out only what they already hold: nothing beyond the libraries and features of the profile
    /// they are acting as.
    /// </summary>
    private async Task RequireWithinHouseholdAccessAsync(
        RequestAuthority actor,
        IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries,
        CancellationToken ct)
    {
        var source = actor.ActiveProfileId;
        var allowedFeatures = source is { } featureSource
            ? (await profileAccess.GetFeatureGrantsAsync(featureSource, ct).ConfigureAwait(false)).Select(feature => feature.Value).ToHashSet()
            : [];
        var allowedLibraries = source is { } librarySource
            ? (await profileAccess.GetLibraryGrantsAsync(librarySource, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        if (features.Any(feature => !allowedFeatures.Contains(feature.Value)) ||
            libraries.Any(library => !allowedLibraries.Contains(library)))
        {
            throw new UnauthorizedAccessException(
                "A household administrator can only hand out the libraries and features the household already has.");
        }
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

    private static AccountProfileGrant NewProfileGrant(
        Guid accountId, Guid profileId, bool isDefault, DateTimeOffset now) => new()
        {
            AccountId = accountId,
            ProfileId = profileId,
            IsDefault = isDefault,
            IsEnabled = true,
            AdminEnabled = false,
            AuthorizationVersion = 1,
            GrantedAt = now,
        };

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
