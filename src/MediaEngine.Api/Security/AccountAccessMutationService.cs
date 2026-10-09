using System.Net.Mail;
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
    IIdentityRepository identities,
    IProfileRepository profiles,
    IConfigurationLoader configuration,
    IAccountAccessDecisionService accountDecisions,
    IAuthorizationEvaluator evaluator,
    IPasswordHasher<GrantAdminProtection> pinHasher,
    IAuthorizationInvalidationService invalidation,
    IAuthorizationAuditWriter audit,
    IFirstPartyIdentityService firstParty,
    TimeProvider clock) : IAccountAccessMutationService
{
    public async Task<Account> CreateAsync(
        RequestAuthority actor,
        CreateAccountAccessCommand command,
        CancellationToken ct = default)
    {
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
        await accounts.CreateAccountAsync(
            account, grant, command.Features, command.Libraries, ct, newProfile).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(command.TemporaryPassword))
        {
            try
            {
                await firstParty.SetTemporaryPasswordAsync(
                    account.Id, command.TemporaryPassword, TemporaryPasswordExpiry(), ct).ConfigureAwait(false);
            }
            catch
            {
                // A password that does not meet the rules must not leave a half-made account behind.
                await accounts.DeleteAccountAsync(account.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            account = await accounts.GetByIdAsync(account.Id, ct).ConfigureAwait(false) ?? account;
        }

        await ChangedAsync(actor, "account.created", "account", account.Id.ToString("D"),
            account.Id, null, ct).ConfigureAwait(false);
        return account;
    }

    public async Task SetTemporaryPasswordAsync(
        RequestAuthority actor,
        Guid accountId,
        string temporaryPassword,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
        target ??= throw new KeyNotFoundException("Account not found.");
        if (actor.AccountId == accountId)
        {
            throw new InvalidOperationException("Use Account > Security to change your own password.");
        }

        RequireNotAdministratorTarget(target, householdOnly);

        // Taking over an administrator's sign-in needs a person who is an unlocked administrator, not an application.
        if (target.IsAdministrator
            && (actor.PrincipalKind != PrincipalKind.Human || !actor.AccountIsAdministrator || !actor.GrantAdminEnabled))
        {
            throw new UnauthorizedAccessException("Only an unlocked administrator can set an administrator's temporary password.");
        }

        await firstParty.SetTemporaryPasswordAsync(
            accountId, temporaryPassword, TemporaryPasswordExpiry(), ct).ConfigureAwait(false);
        await ChangedAsync(actor, "account.temporary_password_set", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
    }

    public async Task ResetTwoStepAsync(
        RequestAuthority actor,
        Guid accountId,
        CancellationToken ct = default)
    {
        var target = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, target?.HouseholdId, ct).ConfigureAwait(false);
        target ??= throw new KeyNotFoundException("Account not found.");
        if (actor.AccountId == accountId)
        {
            throw new InvalidOperationException("Use Account > Security to turn off your own two-step codes.");
        }

        RequireNotAdministratorTarget(target, householdOnly);

        // Switching off an administrator's second sign-in step needs a person who is an unlocked administrator.
        if (target.IsAdministrator
            && (actor.PrincipalKind != PrincipalKind.Human || !actor.AccountIsAdministrator || !actor.GrantAdminEnabled))
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

        if (command.IsAdministrator && !account.IsAdministrator)
        {
            var defaultProfileId = await accounts.GetDefaultProfileIdAsync(accountId, ct).ConfigureAwait(false);
            if (defaultProfileId is { } defaultId &&
                await profiles.GetByIdAsync(defaultId, ct).ConfigureAwait(false) is { Role: ProfileRole.RestrictedProfile })
            {
                throw new InvalidOperationException("Child profiles can't be administrators.");
            }
        }

        var email = NormalizeEmail(command.Email);
        account.Email = email;
        account.NormalizedEmail = email.ToUpperInvariant();
        account.IsEnabled = command.IsEnabled;
        account.IsAdministrator = command.IsAdministrator;
        account.UpdatedAt = clock.GetUtcNow();

        await accounts.UpdateAccountAsync(account, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "account.updated", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
        return (await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false))!;
    }

    public async Task DeleteAsync(
        RequestAuthority actor,
        Guid accountId,
        CancellationToken ct = default)
    {
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        if (await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) is { } doomed)
        {
            await RequireNotLastMainSignInAsync(doomed, ct).ConfigureAwait(false);
        }

        await accounts.DeleteAccountAsync(accountId, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "account.deleted", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
    }

    public async Task<IssuedAccountInvitation> IssueInvitationAsync(
        RequestAuthority actor,
        IssueAccountInvitationCommand command,
        CancellationToken ct = default)
    {
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

            var currentGrants = (await accounts.GetGrantsAsync(account.Id, ct).ConfigureAwait(false))
                .Where(grant => grant.IsEnabled).ToArray();
            if (!currentGrants.Select(grant => grant.ProfileId).ToHashSet().SetEquals(profileIds) ||
                currentGrants.SingleOrDefault(grant => grant.IsDefault)?.ProfileId != defaultProfileId)
            {
                throw new InvalidOperationException("Invitation profiles must match the account's current grants.");
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
            await accounts.CreateInvitedAccountAsync(account, grants, invitation, ct, newPerson).ConfigureAwait(false);
        }

        await ChangedAsync(actor, "account.invitation_issued", "account", account.Id.ToString("D"),
            account.Id, defaultProfileId, ct).ConfigureAwait(false);
        return new IssuedAccountInvitation(account.Id, InvitationCode.Format(code), invitation.ExpiresAt);
    }

    public async Task<Profile> CreateProfileAsync(
        RequestAuthority actor,
        CreateManagedProfileCommand command,
        CancellationToken ct = default)
    {
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
        await accounts.CreateManagedProfileAsync(profile, targetGrant, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.created", "profile", profile.Id.ToString("D"),
            command.AccountId, profile.Id, ct).ConfigureAwait(false);
        return profile;
    }

    public async Task<Profile> AddHouseholdPersonAsync(
        RequestAuthority actor,
        AddHouseholdPersonCommand command,
        CancellationToken ct = default)
    {
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
            Role = command.IsChild ? ProfileRole.RestrictedProfile : ProfileRole.StandardUser,
            CreatedAt = now,
        };
        var grants = openers.Select(account => NewProfileGrant(account.Id, profile.Id, false, now)).ToArray();
        await accounts.CreateHouseholdPersonAsync(profile, command.HouseholdId, grants, ct).ConfigureAwait(false);
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
        var profile = await profiles.GetByIdAsync(command.ProfileId, ct).ConfigureAwait(false);
        await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        profile ??= throw new KeyNotFoundException("Person not found.");
        if (profile.HouseholdId is not { } householdId)
        {
            throw new InvalidOperationException("This person is not in a household yet.");
        }

        var members = (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => account.HouseholdId == householdId)
            .ToList();
        foreach (var member in members.Where(member => member.GrantsInheritFromAccountId is not null))
        {
            if (await OwnProfileOfAsync(member, ct).ConfigureAwait(false) == profile.Id)
            {
                throw new InvalidOperationException($"{profile.DisplayName} already has their own sign-in.");
            }
        }

        var main = members
            .Where(account => account.GrantsInheritFromAccountId is null && account.IsEnabled)
            .OrderBy(account => account.CreatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("This household has no main sign-in to take library access from.");
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
        if (!string.IsNullOrEmpty(command.TemporaryPassword))
        {
            await accounts.CreateAccountAsync(account, grant, new HashSet<AccountFeatureId>(), new HashSet<Guid>(), ct)
                .ConfigureAwait(false);
            try
            {
                await firstParty.SetTemporaryPasswordAsync(
                    account.Id, command.TemporaryPassword, TemporaryPasswordExpiry(), ct).ConfigureAwait(false);
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
            await accounts.CreateInvitedAccountAsync(account, [grant], invitation, ct).ConfigureAwait(false);
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
        var account = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        await RequireHouseholdWriteAsync(actor, account?.HouseholdId, ct).ConfigureAwait(false);
        account ??= throw new KeyNotFoundException("Sign-in not found.");
        const string NotOwnSignIn = "That sign-in is not a person's own sign-in.";
        if (account.GrantsInheritFromAccountId is null || account.IsAdministrator ||
            await OwnProfileOfAsync(account, ct).ConfigureAwait(false) is not { } profileId)
        {
            throw new InvalidOperationException(NotOwnSignIn);
        }

        // Removing the only way to open a person would strand them and their history.
        var someoneElseOpens = false;
        foreach (var other in (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(other => other.Id != accountId && other.HouseholdId == account.HouseholdId))
        {
            if (await accounts.GetGrantAsync(other.Id, profileId, ct).ConfigureAwait(false) is { IsEnabled: true })
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

    /// <summary>The one person an account opens, or <see langword="null"/> when it opens more than one.</summary>
    private async Task<Guid?> OwnProfileOfAsync(Account account, CancellationToken ct)
    {
        var enabled = (await accounts.GetGrantsAsync(account.Id, ct).ConfigureAwait(false))
            .Where(grant => grant.IsEnabled).ToArray();
        return enabled.Length == 1 ? enabled[0].ProfileId : null;
    }

    public async Task<Profile> UpdateProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        UpdateManagedProfileCommand command,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        profile ??= throw new KeyNotFoundException("Profile not found.");
        profile.DisplayName = NormalizeDisplayName(command.DisplayName);
        profile.AvatarColor = NormalizeAvatarColor(command.AvatarColor);
        await accounts.UpdateManagedProfileAsync(profile, ct).ConfigureAwait(false);
        await ChangedAsync(actor, "profile.updated", "profile", profile.Id.ToString("D"),
            actor.AccountId ?? Guid.Empty, profile.Id, ct).ConfigureAwait(false);
        return profile;
    }

    public async Task DeleteProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        if (householdOnly && profile is not null)
        {
            if (actor.ActiveProfileId == profileId)
            {
                throw new InvalidOperationException("Switch to another person before removing this one.");
            }

            await RequireNotAdministratorProfileAsync(profile, ct).ConfigureAwait(false);
        }

        await accounts.DeleteManagedProfileAsync(profileId, ct).ConfigureAwait(false);
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
        target ??= throw new KeyNotFoundException("Account not found.");
        ValidateLibraries(libraries);
        if (target.GrantsInheritFromAccountId is not null)
        {
            throw new InvalidOperationException("This person's access follows the household. Change it on the household's main sign-in.");
        }

        if (householdOnly)
        {
            RequireNotAdministratorTarget(target, householdOnly);
            await RequireWithinHouseholdAccessAsync(target, features, libraries, ct).ConfigureAwait(false);
        }

        await accounts.ReplaceAccountAccessAsync(
            accountId, features, libraries, clock.GetUtcNow(), ct).ConfigureAwait(false);
        await ChangedAsync(actor, "account.access_replaced", "account", accountId.ToString("D"),
            accountId, null, ct).ConfigureAwait(false);
    }

    public async Task UpsertGrantAsync(
        RequestAuthority actor,
        AccountProfileGrant grant,
        CancellationToken ct = default)
    {
        var grantee = await accounts.GetByIdAsync(grant.AccountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, grantee?.HouseholdId, ct).ConfigureAwait(false);
        var grantedProfile = await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile not found.");
        if (householdOnly)
        {
            // A household administrator opens people with sign-ins of their own household, and never makes an administrator.
            if (grantee is null || grantedProfile.HouseholdId != grantee.HouseholdId)
            {
                throw new UnauthorizedAccessException("A household administrator can only manage their own household.");
            }

            RequireNotAdministratorTarget(grantee, householdOnly);
            if (grant.AdminEnabled)
            {
                throw new UnauthorizedAccessException("Only a server administrator can turn on administrator access.");
            }
        }

        if (await accounts.GetByIdAsync(grant.AccountId, ct).ConfigureAwait(false) is { GrantsInheritFromAccountId: not null } follower)
        {
            // A person's own sign-in opens only that person and is never an administrator.
            if (grant.AdminEnabled || await OwnProfileOfAsync(follower, ct).ConfigureAwait(false) != grant.ProfileId)
            {
                throw new InvalidOperationException("A person's own sign-in opens only that person and can't be an administrator.");
            }
        }

        if (grant.AdminEnabled &&
            await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false) is { Role: ProfileRole.RestrictedProfile })
        {
            throw new InvalidOperationException("Child profiles can't be administrators.");
        }

        grant.GrantedAt = clock.GetUtcNow();
        await accounts.UpsertGrantAsync(grant, ct).ConfigureAwait(false);
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
        var grantee = await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, grantee?.HouseholdId, ct).ConfigureAwait(false);
        if (householdOnly && grantee is not null)
        {
            RequireNotAdministratorTarget(grantee, householdOnly);
        }

        await accounts.RevokeGrantAsync(accountId, profileId, ct).ConfigureAwait(false);
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
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var grant = await accounts.GetGrantAsync(accountId, profileId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile grant not found.");
        if (!grant.AdminEnabled)
        {
            throw new InvalidOperationException("Administrator access is not enabled for this grant.");
        }

        if (!Enum.IsDefined(command.UnlockMode))
        {
            throw new ArgumentException("Unknown administrator unlock mode.");
        }

        if (command.Enabled &&
            (string.IsNullOrWhiteSpace(command.Pin) ||
             command.Pin.Length is < 4 or > 12 ||
             command.Pin.Any(character => !char.IsAsciiDigit(character))))
        {
            throw new ArgumentException("PIN must contain 4 to 12 digits.");
        }

        var protection = new GrantAdminProtection
        {
            AccountId = accountId,
            ProfileId = profileId,
            IsEnabled = command.Enabled,
            UnlockMode = command.UnlockMode.ToString(),
            UnlockMinutes = command.UnlockMode == AdminUnlockMode.FixedDuration
                ? Math.Clamp(command.UnlockMinutes ?? 30, 1, 120)
                : null,
            ProtectionVersion = 1,
            UpdatedAt = clock.GetUtcNow(),
        };
        if (command.Enabled)
        {
            protection.HashScheme = "aspnet-passwordhasher-v3";
            protection.PinHash = pinHasher.HashPassword(protection, command.Pin!);
        }

        await accounts.SetAdminProtectionAsync(protection, ct).ConfigureAwait(false);
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

        await accounts.SetHouseholdAdminAsync(accountId, isHouseholdAdmin, clock.GetUtcNow(), ct).ConfigureAwait(false);
        await ChangedAsync(actor, isHouseholdAdmin ? "household.admin_granted" : "household.admin_removed",
            "account", accountId.ToString("D"), accountId, null, ct).ConfigureAwait(false);
    }

    public async Task SetProfilePinAsync(
        RequestAuthority actor,
        Guid profileId,
        string? pin,
        CancellationToken ct = default)
    {
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false);
        var householdOnly = await RequireHouseholdWriteAsync(actor, profile?.HouseholdId, ct).ConfigureAwait(false);
        profile ??= throw new KeyNotFoundException("Profile not found.");
        if (householdOnly)
        {
            // A PIN on an administrator's own person could lock the administrator out of it.
            await RequireNotAdministratorProfileAsync(profile, ct).ConfigureAwait(false);
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
            if (!actor.IsEffectiveAdministrator && actor.IsEffectiveHouseholdAdministrator &&
                householdId is { } household && actor.AccountHouseholdId == household)
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

    /// <summary>A household administrator can't touch a person that a server administrator uses as an administrator.</summary>
    private async Task RequireNotAdministratorProfileAsync(Profile profile, CancellationToken ct)
    {
        foreach (var account in (await accounts.GetAllAsync(ct).ConfigureAwait(false))
            .Where(account => account.HouseholdId == profile.HouseholdId && account.IsAdministrator))
        {
            if (await accounts.GetGrantAsync(account.Id, profile.Id, ct).ConfigureAwait(false) is { IsEnabled: true, AdminEnabled: true })
            {
                throw new UnauthorizedAccessException("Only a server administrator can change a person who administers the server.");
            }
        }
    }

    /// <summary>
    /// A household administrator hands out only what the household already has: nothing beyond the libraries and features of
    /// the household's primary sign-in (the one the server administrator set up).
    /// </summary>
    private async Task RequireWithinHouseholdAccessAsync(
        Account target,
        IReadOnlySet<AccountFeatureId> features,
        IReadOnlySet<Guid> libraries,
        CancellationToken ct)
    {
        var primaryId = target.HouseholdId is { } household
            ? await accounts.GetHouseholdPrimaryAccountIdAsync(household, ct).ConfigureAwait(false)
            : null;
        var allowedFeatures = primaryId is { } featureSource
            ? (await accounts.GetFeatureGrantsAsync(featureSource, ct).ConfigureAwait(false)).Select(feature => feature.Value).ToHashSet()
            : [];
        var allowedLibraries = primaryId is { } librarySource
            ? (await accounts.GetLibraryGrantsAsync(librarySource, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        if (features.Any(feature => !allowedFeatures.Contains(feature.Value)) ||
            libraries.Any(library => !allowedLibraries.Contains(library)))
        {
            throw new UnauthorizedAccessException(
                "A household administrator can only hand out the libraries and features the household already has.");
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
        CancellationToken ct)
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
            new Dictionary<string, string?> { ["changed"] = "true" }), ct).ConfigureAwait(false);
    }

    private static string NormalizeEmail(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("Email is required.");
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
