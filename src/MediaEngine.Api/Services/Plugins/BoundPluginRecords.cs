using System.Text.Json;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// One plugin's view of its own records. The plugin id comes from the plugin's registration, never from the plugin, so a
/// plugin can only ever reach its own rows. Needs the <c>plugin-records</c> permission on every call (so switching the
/// plugin off or revoking the permission takes effect straight away) and enforces the limits in
/// <see cref="PluginHostLimits"/>: a write over a limit throws <see cref="PluginLimitExceededException"/> and the plugin's
/// card shows Needs attention until a later write fits.
/// </summary>
internal sealed class BoundPluginRecords(
    string pluginId,
    IPluginPermissionGate permissions,
    IPluginRecordRepository records,
    PluginHostLimits limits,
    PluginHostStatus status,
    TimeProvider clock) : IPluginRecordStore
{
    private readonly PluginRecordLimits _storageLimits =
        new(limits.RecordValueMaxBytes, limits.RecordsPerPlugin, limits.RecordBytesPerPlugin);

    public async Task<PluginRecord?> GetAsync(string collection, string key, CancellationToken cancellationToken = default)
    {
        Authorise();
        ValidateName(collection, nameof(collection));
        ValidateName(key, nameof(key));
        var stored = await records.GetAsync(pluginId, collection, key, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : ToRecord(stored);
    }

    public async Task PutAsync(string collection, string key, string json, CancellationToken cancellationToken = default)
    {
        Authorise();
        ValidateName(collection, nameof(collection));
        ValidateName(key, nameof(key));
        ValidateJson(json);

        var result = await records
            .PutAsync(pluginId, collection, key, json, clock.GetUtcNow(), _storageLimits, cancellationToken)
            .ConfigureAwait(false);
        switch (result)
        {
            case PluginRecordWriteResult.Saved:
                status.ClearProblem(PluginHostMessages.StorageProblemKey);
                return;
            case PluginRecordWriteResult.ValueTooLarge:
                Refuse(
                    $"A record can be at most {PluginHostMessages.FormatBytes(limits.RecordValueMaxBytes)}.",
                    PluginHostMessages.ValueTooLarge(limits));
                break;
            case PluginRecordWriteResult.TooManyRecords:
                Refuse(
                    $"A plugin can keep at most {limits.RecordsPerPlugin:N0} records.",
                    PluginHostMessages.TooManyRecords(limits));
                break;
            default:
                Refuse(
                    $"A plugin can keep at most {PluginHostMessages.FormatBytes(limits.RecordBytesPerPlugin)} of records.",
                    PluginHostMessages.StorageFull(limits));
                break;
        }
    }

    public Task<bool> DeleteAsync(string collection, string key, CancellationToken cancellationToken = default)
    {
        Authorise();
        ValidateName(collection, nameof(collection));
        ValidateName(key, nameof(key));
        return records.DeleteAsync(pluginId, collection, key, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginRecord>> ListAsync(
        string collection,
        string? keyPrefix = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        Authorise();
        ValidateName(collection, nameof(collection));
        if (!string.IsNullOrEmpty(keyPrefix))
        {
            ValidateName(keyPrefix, nameof(keyPrefix));
        }

        var stored = await records.ListAsync(pluginId, collection, keyPrefix, limit, cancellationToken).ConfigureAwait(false);
        return stored.Select(ToRecord).ToList();
    }

    private void Authorise() => permissions.Demand(pluginId, PluginPermissionIds.PluginRecords);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Refuse(string exceptionMessage, string statusMessage)
    {
        status.SetProblem(PluginHostMessages.StorageProblemKey, PluginHostSeverity.NeedsAttention, statusMessage);
        throw new PluginLimitExceededException(exceptionMessage);
    }

    private void ValidateName(string value, string parameter)
    {
        if (string.IsNullOrEmpty(value) || value.Length > limits.RecordNameMaxLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A record {parameter} must be 1 to {limits.RecordNameMaxLength} printable characters.", parameter);
        }
    }

    private static void ValidateJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("A record must be valid JSON.", nameof(json), ex);
        }
    }

    private static PluginRecord ToRecord(StoredPluginRecord stored) =>
        new(stored.Collection, stored.Key, stored.Json, stored.UpdatedAt);
}
