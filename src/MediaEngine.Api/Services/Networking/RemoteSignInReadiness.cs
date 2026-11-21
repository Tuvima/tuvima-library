using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

/// <summary>Reads the existing sign-in records (users, their profiles and their password or passkey); it stores nothing.</summary>
public sealed class RemoteSignInReadinessReader(IAccountRepository accounts, IProfileRepository profiles) : IRemoteSignInReadinessReader
{
    public async Task<RemoteSignInReadiness> ReadAsync(CancellationToken ct)
    {
        var administratorIds = (await accounts.GetEnabledServerAdministratorsAsync(ct).ConfigureAwait(false))
            .Select(account => account.Id)
            .ToHashSet();

        var anyAdministratorSecured = false;
        var homeOnly = new List<(Guid AccountId, string Name)>();
        foreach (var account in await accounts.GetAllAsync(ct).ConfigureAwait(false))
        {
            if (!account.IsEnabled)
            {
                continue;
            }

            var secured = !account.IsThisComputerOnly
                && await accounts.IsUserSecuredAsync(account.Id, ct).ConfigureAwait(false);
            if (secured)
            {
                anyAdministratorSecured |= administratorIds.Contains(account.Id);
                continue;
            }

            var profile = await profiles.GetByIdAsync(account.ProfileId, ct).ConfigureAwait(false);
            homeOnly.Add((account.Id, profile?.DisplayName ?? account.Email ?? "Unnamed person"));
        }

        // When no administrator has a password that is its own finding, so the administrators are not listed twice.
        var noAdministratorHasPassword = administratorIds.Count > 0 && !anyAdministratorSecured;
        var names = homeOnly
            .Where(person => !noAdministratorHasPassword || !administratorIds.Contains(person.AccountId))
            .Select(person => person.Name)
            .ToList();
        return new RemoteSignInReadiness(names, noAdministratorHasPassword);
    }
}
