using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Services.Integration;

public interface IAdministratorSurfaceAccessService
{
    Task<bool> EnsureUnlockedAsync(CancellationToken ct = default);
}

public sealed class AdministratorSurfaceAccessService(
    DashboardIdentityClient identity,
    DashboardSessionAccessor session,
    IAppDialogService? dialogs = null) : IAdministratorSurfaceAccessService
{
    private readonly SemaphoreSlim _entry = new(1, 1);

    public async Task<bool> EnsureUnlockedAsync(CancellationToken ct = default)
    {
        if (!await _entry.WaitAsync(0, ct))
        {
            return false;
        }

        try
        {
            var authority = await identity.RevalidateAuthorityAsync(session, ct);
            if (authority?.EffectiveAdministrator != true)
            {
                return false;
            }

            if (authority.AdministratorSurfaceUnlocked
                && (authority.AdministratorUnlockExpiresAt is null
                    || authority.AdministratorUnlockExpiresAt > DateTimeOffset.UtcNow))
            {
                return true;
            }

            if (dialogs is null)
            {
                return false;
            }

            var accountId = session.AccountId;
            var profileId = session.ActiveProfileId;
            var sessionId = session.SessionId;
            var dialog = await dialogs.ShowAsync<AdministratorUnlockDialog>("Unlock administrator settings",
                new AppDialogOptions { MaxWidth = AppMaxWidth.Small, FullWidth = true, CloseButton = true, CloseOnEscapeKey = true });
            using var cancellation = ct.Register(() => dialog.Close());
            var result = await dialog.Result;
            if (result is null || result.Canceled || session.AccountId != accountId
                || session.ActiveProfileId != profileId || session.SessionId != sessionId)
            {
                return false;
            }

            authority = await identity.RevalidateAuthorityAsync(session, ct);
            return authority?.EffectiveAdministrator == true && authority.AdministratorSurfaceUnlocked
                && (authority.AdministratorUnlockExpiresAt is null || authority.AdministratorUnlockExpiresAt > DateTimeOffset.UtcNow);
        }
        finally { _entry.Release(); }
    }
}
