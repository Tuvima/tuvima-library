using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A stand-in for the usable-administrator check in tests that are not about it: someone can always still sign in, so the
/// last-administrator rule never refuses. The data store keeps its own simpler rule, which these tests still meet.
/// </summary>
internal sealed class AlwaysUsableAdministrators : IUsableAdministratorService
{
    public Task<bool> HasUsableAdministratorSignInAsync(AuthSettings policy, CancellationToken ct) => Task.FromResult(true);

    public Task<bool> HasUsableAdministratorSignInAfterAsync(AdministratorSignInLoss loss, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<UsableAdministratorStatus> EvaluateForRemoteAsync(CancellationToken ct) =>
        Task.FromResult(new UsableAdministratorStatus(true, true, true));

    public Task<bool> OnlyThisComputerAdministratorsAsync(CancellationToken ct) => Task.FromResult(false);
}
