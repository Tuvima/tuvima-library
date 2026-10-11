using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Setup;
using MediaEngine.Domain.Authorization;
using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>
/// Issues a short-lived browser session before the first account is created.
/// Account creation permanently closes unauthenticated setup; later credential or
/// grant changes use authenticated recovery and never reopen this entry point.
/// </summary>
public sealed class SetupSessionService(
    OnboardingRepository repository,
    IFirstPartyIdentityService identity,
    TimeProvider timeProvider)
{
    public const string SessionHeader = "X-Tuvima-Setup-Session";
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Where the current setup session began. Kept in memory: after a restart the person simply begins again.
    private string? _currentSessionHash;
    private string _currentSessionIngress = ClientIngress.Remote;

    /// <summary>
    /// Starts setup. A visitor on this computer or on a private home-network address can begin without a code: the
    /// person setting up a server is on their own network, and the server's owner can always redo setup. The visitor's place is
    /// recorded for the session but no longer decides whether setup may begin. Setup codes are no longer used. A successful
    /// begin ends every earlier setup session.
    /// </summary>
    public async Task<SetupBeginResult> BeginAsync(string? clientIngress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await identity.IsAdministratorConfiguredAsync(ct).ConfigureAwait(false))
            {
                return SetupBeginResult.AlreadyConfigured;
            }

            var ingress = ClientIngress.Parse(clientIngress);
            if (!ClientIngress.IsLocal(ingress))
            {
                return SetupBeginResult.Refused(
                    SetupBeginRefusalReasons.RemoteRefused,
                    "Setup has to be finished from your home network.");
            }

            var plaintextSession = Token(32);
            var sessionHash = Convert.ToHexStringLower(Hash(plaintextSession));
            var expires = timeProvider.GetUtcNow().AddHours(12);
            var started = await repository.TryBeginAsync(sessionHash, Guid.NewGuid(), expires, ct).ConfigureAwait(false);
            if (!started)
            {
                return SetupBeginResult.AlreadyConfigured;
            }

            _currentSessionHash = sessionHash;
            _currentSessionIngress = ingress;
            return SetupBeginResult.Success(new SetupStartResponse(
                plaintextSession,
                expires,
                await GetStatusAsync(ct).ConfigureAwait(false)));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when the setup session identified by <paramref name="token"/> was begun on this computer or the home network.</summary>
    public bool BeganLocally(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && _currentSessionHash is not null
        && string.Equals(_currentSessionHash, Convert.ToHexStringLower(Hash(token)), StringComparison.Ordinal)
        && ClientIngress.IsLocal(_currentSessionIngress);

    public async Task<bool> ValidateSessionAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)
            || await identity.IsAdministratorConfiguredAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        return await repository.ValidateSessionAsync(
            Convert.ToHexStringLower(Hash(token)),
            ct).ConfigureAwait(false);
    }

    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var bootstrapCompleted = await identity.IsAdministratorConfiguredAsync(ct).ConfigureAwait(false);
        var workflow = repository.Get();
        return new SetupStatusDto(
            workflow.WorkflowVersion,
            workflow.State,
            workflow.CurrentStep,
            workflow.Revision,
            !bootstrapCompleted && workflow.State != "complete",
            bootstrapCompleted && workflow.State != "complete",
            bootstrapCompleted,
            workflow.Steps.Select(step => new SetupStepStatusDto(
                step.Key, step.Status, step.Detail, step.RepairTarget, step.CompletedAt)).ToList());
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static string Token(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
}

/// <summary>The outcome of <see cref="SetupSessionService.BeginAsync"/>.</summary>
public sealed record SetupBeginResult(
    SetupStartResponse? Started,
    SetupBeginRefusalDto? Refusal,
    bool IsAlreadyConfigured)
{
    public static SetupBeginResult AlreadyConfigured { get; } = new(null, null, true);
    public static SetupBeginResult Success(SetupStartResponse started) => new(started, null, false);
    public static SetupBeginResult Refused(string reason, string message) =>
        new(null, new SetupBeginRefusalDto(reason, message), false);
}
