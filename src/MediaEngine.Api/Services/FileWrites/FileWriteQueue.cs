using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// Puts files on the durable write list (<see cref="IFileWriteQueue"/>). Nothing is queued when write-back is off or the trigger's own
/// switch in <c>config/writeback.json</c> is off, so turning write-back off really stops every file write.
/// </summary>
public sealed class FileWriteQueue(
    MediaFileWriteIntentRepository intents,
    IConfigurationLoader configuration,
    ILogger<FileWriteQueue> logger) : IFileWriteQueue
{
    public Task EnqueueAssetAsync(Guid assetId, string trigger, CancellationToken ct = default) =>
        EnqueueAssetsAsync([assetId], trigger, ct);

    public async Task EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        if (assetIds.Count == 0 || !Allowed(trigger))
        {
            return;
        }

        var queued = await intents.EnqueueAssetsAsync(assetIds, trigger, ct);
        logger.LogDebug("File write list: {Queued} of {Asked} file(s) now waiting (trigger {Trigger}).", queued, assetIds.Count, trigger);
    }

    public async Task EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        if (!Allowed(trigger))
        {
            return;
        }

        await intents.EnqueueViewItemAsync(localItemId, trigger, ct);
    }

    private bool Allowed(string trigger) =>
        (configuration.LoadConfig<WriteBackConfiguration>(string.Empty, "writeback") ?? new WriteBackConfiguration())
        .AllowsTrigger(trigger);
}
