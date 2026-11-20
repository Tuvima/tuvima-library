using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity;

namespace MediaEngine.Api.Services.Networking;

/// <summary>Reads the outside sign-in records the Engine keeps in its sign-in audit log; it stores nothing.</summary>
public sealed class OutsideSignInHistory(IIdentityRepository identities) : IOutsideSignInHistory
{
    public async Task<IReadOnlyList<OutsideSignInDto>> GetRecentAsync(int limit, CancellationToken ct)
    {
        var records = await identities.GetRecentOutsideSignInsAsync(limit, ct).ConfigureAwait(false);
        return records.Select(record => new OutsideSignInDto
        {
            At = record.At,
            ProfileName = record.ProfileName,
            Succeeded = record.Succeeded,
            Method = MethodName(record.Method),
            RefusalReason = record.Succeeded ? null : OutsideSignInRefusedException.PlainMessage,
        }).ToList();
    }

    private static string MethodName(string method) => method switch
    {
        "Passkey" => OutsideSignInMethods.Passkey,
        "Oidc" => OutsideSignInMethods.LinkedAccount,
        "ThisComputer" => OutsideSignInMethods.NoPassword,
        _ => OutsideSignInMethods.Password,
    };
}

/// <summary>Outside access may only be switched on while an enabled server administrator has a password or passkey.</summary>
public sealed class OutsideAccessGate(IAccountRepository accounts) : IOutsideAccessGate
{
    public async Task<bool> HasSecuredAdministratorAsync(CancellationToken ct)
    {
        foreach (var administrator in await accounts.GetEnabledServerAdministratorsAsync(ct).ConfigureAwait(false))
        {
            if (!administrator.IsThisComputerOnly && await accounts.IsUserSecuredAsync(administrator.Id, ct).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }
}
