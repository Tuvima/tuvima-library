using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Vault;

/// <summary>
/// Keeps <see cref="VaultLocationGuard"/> in step with the Vault's folders (Videos spec D17, D33): the folders of every library whose
/// id is in the Vault list. It reads once before the Engine starts serving or scanning, then every half minute, so a Vault folder
/// is known to the logging and activity writers from the start. Adding a folder tells the guard directly as well.
/// </summary>
public sealed class VaultLocationGuardRefreshService(
    IVaultLibraryRepository vaultLibraries,
    IConfigurationLoader configuration,
    ILogger<VaultLocationGuardRefreshService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Reads the Vault's folders now and hands them to the guard.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var ids = await vaultLibraries.GetIdsAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            VaultLocationGuard.Replace([]);
            return;
        }

        var folders = configuration.LoadLibraries().Libraries
            .Where(library => ids.Contains(library.Id))
            .SelectMany(library => library.Sources)
            .Select(source => source.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path));
        VaultLocationGuard.Replace(folders);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Fixed words and the kind of error only: the folder list is exactly what must not be written down.
                logger.LogWarning("Could not refresh the list of private folders ({Type}); the last list stays in use.", ex.GetType().Name);
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
