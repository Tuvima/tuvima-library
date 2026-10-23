using System.Net.Mail;
using System.Runtime.CompilerServices;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Identity.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>What a test wants a new user to start with (test shorthand over the data store's user creation).</summary>
internal sealed record CreateAccountAccessCommand(
    string? Email,
    bool IsAdministrator,
    Guid? ProfileId,
    NewAccountProfileCommand? NewProfile,
    IReadOnlySet<AccountFeatureId> Features,
    IReadOnlySet<Guid> Libraries,
    string? TemporaryPassword = null);

internal sealed record NewAccountProfileCommand(string DisplayName, string? AvatarColor);

/// <summary>
/// Test shorthand for setting up a world of users: makes a user, with a new profile or an existing one, optionally a server
/// administrator (in the Library's own household), straight through the data store. The Engine's own actions for making users are
/// under test elsewhere; this only builds the starting state. Attach it to the service a test builds, then call
/// <see cref="CreateAsync"/> on that service.
/// </summary>
internal static class AccountSetupKit
{
    private static readonly ConditionalWeakTable<IAccountAccessMutationService, Setup> Sets = new();

    public static T Attach<T>(
        T mutations, IAccountRepository accounts, IProfileAccessRepository profileAccess, IFirstPartyIdentityService identity)
        where T : class, IAccountAccessMutationService
    {
        Sets.AddOrUpdate(mutations, new Setup(accounts, profileAccess, identity));
        return mutations;
    }

    public static Task<Account> CreateAsync(
        this IAccountAccessMutationService mutations, RequestAuthority actor, CreateAccountAccessCommand command, CancellationToken ct = default) =>
        (Sets.TryGetValue(mutations, out var setup) ? setup : throw new InvalidOperationException("Attach the account setup kit to the service first."))
            .CreateAsync(command, ct);

    /// <summary>Test shorthand: makes the profile a user opens a household administrator (or not), without the Engine's checks.</summary>
    public static async Task SetHouseholdAdminAsync(
        this IAccountAccessMutationService mutations, RequestAuthority actor, Guid userId, bool isHouseholdAdmin, CancellationToken ct = default)
    {
        var setup = Sets.TryGetValue(mutations, out var found) ? found : throw new InvalidOperationException("Attach the account setup kit to the service first.");
        var user = await setup.Accounts.GetByIdAsync(userId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("Account not found.");
        await setup.ProfileAccess.SetHouseholdAdministratorAsync(user.ProfileId, isHouseholdAdmin, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    private sealed class Setup
    {
        private readonly IFirstPartyIdentityService _identity;

        public Setup(IAccountRepository accounts, IProfileAccessRepository profileAccess, IFirstPartyIdentityService identity)
        {
            Accounts = accounts;
            ProfileAccess = profileAccess;
            _identity = identity;
        }

        public IAccountRepository Accounts { get; }

        public IProfileAccessRepository ProfileAccess { get; }

        public async Task<Account> CreateAsync(CreateAccountAccessCommand command, CancellationToken ct)
        {
            if ((command.ProfileId is null) == (command.NewProfile is null))
            {
                throw new ArgumentException("Choose one existing profile or create one new default profile.");
            }

            if (string.IsNullOrWhiteSpace(command.Email))
            {
                throw new ArgumentException("An email is needed for a sign-in that works away from home.");
            }

            string email;
            try
            {
                email = new MailAddress(command.Email.Trim()).Address;
            }
            catch (FormatException)
            {
                throw new ArgumentException("Enter a valid email address.");
            }

            var now = DateTimeOffset.UtcNow;
            Profile? newProfile = null;
            var profileId = command.ProfileId.GetValueOrDefault();
            if (command.NewProfile is { } requested)
            {
                profileId = Guid.NewGuid();
                newProfile = new Profile
                {
                    Id = profileId,
                    DisplayName = requested.DisplayName,
                    AvatarColor = requested.AvatarColor ?? "#7C4DFF",
                    Role = ProfileRole.StandardUser,
                    CreatedAt = now,
                };
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

            var defaultHousehold = await ProfileAccess.GetDefaultHouseholdIdAsync(ct).ConfigureAwait(false);
            var placement = new NewUserPlacement(
                newProfile,
                HouseholdId: newProfile is not null && command.IsAdministrator ? defaultHousehold : null,
                IsServerAdministrator: newProfile is not null && command.IsAdministrator,
                Features: newProfile is null ? null : command.Features,
                Libraries: newProfile is null ? null : command.Libraries);
            await Accounts.CreateUserAsync(account, placement, null, ct).ConfigureAwait(false);
            if (newProfile is null && command.IsAdministrator)
            {
                await ProfileAccess.SetServerAdministratorAsync(profileId, true, now, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(command.TemporaryPassword))
            {
                await _identity.SetTemporaryPasswordAsync(account.Id, command.TemporaryPassword, now.AddDays(7), ct: ct).ConfigureAwait(false);
            }

            return await Accounts.GetByIdAsync(account.Id, ct).ConfigureAwait(false) ?? account;
        }
    }
}
