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

    /// <summary>
    /// Starts setup. A visitor on this computer needs nothing; a visitor on the home network needs the
    /// one-time setup code from <c>tuvima-admin setup code</c>; anyone else is refused, because setup is
    /// never done over the internet. A successful begin ends every earlier setup session.
    /// </summary>
    public async Task<SetupBeginResult> BeginAsync(string? clientIngress, string? setupCode, CancellationToken ct)
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

            var codeRequired = ingress != ClientIngress.ThisComputer;
            if (codeRequired && string.IsNullOrWhiteSpace(setupCode))
            {
                return SetupBeginResult.Refused(
                    SetupBeginRefusalReasons.CodeRequired,
                    "Enter the setup code from your server to continue.");
            }

            var plaintextSession = Token(32);
            var sessionHash = Convert.ToHexStringLower(Hash(plaintextSession));
            var expires = timeProvider.GetUtcNow().AddHours(12);
            // The code is used up in the same transaction that starts the session.
            var (started, check) = await repository.TryBeginAsync(
                sessionHash, Guid.NewGuid(), expires, setupCode, codeRequired, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            if (!started)
            {
                return check is null || check == SetupCodeCheck.Accepted
                    ? SetupBeginResult.AlreadyConfigured
                    : SetupBeginResult.Refused(
                        SetupBeginRefusalReasons.CodeInvalid,
                        check switch
                        {
                            SetupCodeCheck.Wrong => "That setup code isn't right. Check it and try again.",
                            SetupCodeCheck.TooManyAttempts => "Too many wrong codes. Generate a new setup code on your server.",
                            _ => "There is no valid setup code. Generate a new one on your server.",
                        });
            }

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
