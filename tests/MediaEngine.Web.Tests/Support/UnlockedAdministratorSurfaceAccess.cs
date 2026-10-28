using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests.Support;

/// <summary>Administrator surface access that is always unlocked, for tests that are not about the unlock step.</summary>
internal sealed class UnlockedAdministratorSurfaceAccess : IAdministratorSurfaceAccessService
{
    public Task<bool> EnsureUnlockedAsync(CancellationToken ct = default) => Task.FromResult(true);
}
