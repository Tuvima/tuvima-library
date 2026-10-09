using MediaEngine.Storage;

namespace MediaEngine.Admin;

/// <summary>
/// <c>tuvima-admin setup code</c>: prints the one-time code that proves a person can reach the server
/// before first-run setup starts from another device. Only a hash is stored.
/// </summary>
public sealed class SetupCodeCommand(
    IHostRecoveryAuthorizer authorizer,
    Func<CancellationToken, Task<bool>> administratorExists,
    SetupCodeRepository codes,
    IAdminConsole console,
    TimeProvider timeProvider)
{
    public const int AdministratorExistsExitCode = 5;

    public async Task<int> ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            authorizer.EnsureAuthorized();
            if (await administratorExists(ct).ConfigureAwait(false))
            {
                console.WriteError("This server already has an administrator, so setup is closed and no setup code is needed.");
                return AdministratorExistsExitCode;
            }

            var code = SetupCodeRepository.GenerateCode();
            var now = timeProvider.GetUtcNow();
            await codes.IssueAsync(code, now, ct).ConfigureAwait(false);

            console.WriteLine($"Setup code: {code}");
            console.WriteLine($"Valid for {(int)SetupCodeRepository.Lifetime.TotalMinutes} minutes and works once. Enter it on the setup page.");
            console.WriteLine("Generating another code makes this one stop working.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            console.WriteError("Setup code generation was cancelled. No code was created.");
            return 130;
        }
        catch (UnauthorizedAccessException ex)
        {
            console.WriteError(ex.Message);
            return 3;
        }
    }
}
