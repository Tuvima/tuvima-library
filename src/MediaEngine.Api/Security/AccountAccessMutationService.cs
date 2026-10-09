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
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        if (await accounts.GetByIdAsync(accountId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Account not found.");
        }

        await firstParty.SetTemporaryPasswordAsync(
            accountId, temporaryPassword, TemporaryPasswordExpiry(), ct).ConfigureAwait(false);
        await ChangedAsync(actor, "account.temporary_password_set", "account", accountId.ToString("D"),
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

    public async Task<Profile> UpdateProfileAsync(
        RequestAuthority actor,
        Guid profileId,
        UpdateManagedProfileCommand command,
        CancellationToken ct = default)
    {
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        var profile = await profiles.GetByIdAsync(profileId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Profile not found.");
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
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
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
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        ValidateLibraries(libraries);
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
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
        if (await profiles.GetByIdAsync(grant.ProfileId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Profile not found.");
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
        await RequireWriteAsync(actor, ct).ConfigureAwait(false);
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

    /// <summary>A temporary password lasts as long as an invitation.</summary>
    private DateTimeOffset TemporaryPasswordExpiry() => clock.GetUtcNow().Add(InvitationLifetime());

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
