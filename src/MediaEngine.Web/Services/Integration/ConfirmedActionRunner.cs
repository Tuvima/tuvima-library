namespace MediaEngine.Web.Services.Integration;

/// <summary>Asks the person to prove it is them (the "Confirm it's you" dialog). True once they have.</summary>
public interface IItsYouConfirmer
{
    Task<bool> ConfirmAsync(CancellationToken ct = default);
}

/// <summary>
/// Runs a sensitive account action. When the Engine answers <c>confirm_its_you</c> it opens the confirmation once and,
/// if the person confirms, runs the same action one more time. It never loops: a second refusal is returned as it is.
/// </summary>
public sealed class ConfirmedActionRunner(IItsYouConfirmer confirmer)
{
    public async Task<DashboardAccessMutationResult> RunAsync(
        Func<Task<DashboardAccessMutationResult>> action, CancellationToken ct = default)
    {
        var result = await action().ConfigureAwait(false);
        if (result.Failure != DashboardAccessMutationFailure.ConfirmItsYou
            || !await confirmer.ConfirmAsync(ct).ConfigureAwait(false))
        {
            return result;
        }

        return await action().ConfigureAwait(false);
    }

    public async Task<DashboardAccessMutationResult<T>> RunAsync<T>(
        Func<Task<DashboardAccessMutationResult<T>>> action, CancellationToken ct = default)
    {
        var result = await action().ConfigureAwait(false);
        if (result.Failure != DashboardAccessMutationFailure.ConfirmItsYou
            || !await confirmer.ConfirmAsync(ct).ConfigureAwait(false))
        {
            return result;
        }

        return await action().ConfigureAwait(false);
    }
}
