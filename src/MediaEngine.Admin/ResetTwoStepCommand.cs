using MediaEngine.Identity.Contracts;

namespace MediaEngine.Admin;

/// <summary>
/// <c>tuvima-admin auth reset-two-step</c>: turns two-step codes off for someone who lost both their authenticator
/// app and their recovery codes. It runs on the computer that hosts Tuvima, so it needs the same elevated access as
/// the password reset.
/// </summary>
public sealed class ResetTwoStepCommand(
    IHostRecoveryAuthorizer authorizer,
    IHostTwoStepRecoveryService recovery,
    IAdminConsole console)
{
    public async Task<int> ExecuteAsync(string? suppliedEmail, CancellationToken ct = default)
    {
        try
        {
            authorizer.EnsureAuthorized();
            var email = string.IsNullOrWhiteSpace(suppliedEmail)
                ? console.ReadLine("Account email: ")
                : suppliedEmail;
            if (string.IsNullOrWhiteSpace(email))
            {
                console.WriteError("The account email is required.");
                return 2;
            }

            var hadTwoStep = await recovery.ResetTwoStepFromHostAsync(email, ct).ConfigureAwait(false);
            console.WriteLine(hadTwoStep
                ? "Two-step codes are now off for this account. They can turn them back on from Account > Security."
                : "This account did not have two-step codes on. No changes were made.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            console.WriteError("The reset was cancelled. No changes were made.");
            return 130;
        }
        catch (UnauthorizedAccessException ex)
        {
            console.WriteError(ex.Message);
            return 3;
        }
        catch (ArgumentException ex)
        {
            console.WriteError(ex.Message);
            return 2;
        }
        catch (InvalidOperationException ex)
        {
            console.WriteError(ex.Message);
            return 1;
        }
    }
}
