using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Carries on every saved Move to Vault job, also after a restart (Vault sections D37). It sleeps until a move is saved, then works through the
/// open jobs one item at a time; a copy that is waiting for its folder's scan is looked at again every half minute.
/// </summary>
public sealed class VaultMoveWorker(VaultMoveRunner runner, VaultMoveSignal signal, ILogger<VaultMoveWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = TimeSpan.FromSeconds(20);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(wait, stoppingToken).ConfigureAwait(false);
                wait = await runner.RunPassAsync(stoppingToken).ConfigureAwait(false) ? Busy : Idle;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("A Vault move pass failed ({Type}); it is tried again.", ex.GetType().Name);
                wait = Busy;
            }
        }
    }
}
