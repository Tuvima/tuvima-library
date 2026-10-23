using MediaEngine.Ingestion.Services.ImportUploads;

namespace MediaEngine.Api.Services;

/// <summary>
/// Cleans up uploads into the Import folder that were left behind (Library setup spec D44): an upload that has had no piece for
/// <c>upload_abandon_hours</c> (24 by default, in <c>config/maintenance.json</c>) is abandoned and its hidden working file deleted, so a
/// browser that was closed mid-upload never leaves a big file behind. The pass runs shortly after the Engine starts, which also removes
/// anything an earlier run left, and then every hour.
/// </summary>
public sealed class ImportUploadSweepHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly ImportUploadService _uploads;
    private readonly ILogger<ImportUploadSweepHostedService> _logger;

    public ImportUploadSweepHostedService(ImportUploadService uploads, ILogger<ImportUploadSweepHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        ArgumentNullException.ThrowIfNull(logger);
        _uploads = uploads;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await _uploads.SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ImportUploadSweepHostedService: a pass failed; the next pass tries again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }
}
