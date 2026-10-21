using MediaEngine.Contracts.Plugins;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Builds the <c>host_services</c> part of a plugin's list entry: the in-memory state of its host services plus how much
/// of the data store its own records use.
/// </summary>
internal sealed class PluginHostServicesReader(PluginHostStatusRegistry statuses, IPluginRecordRepository records)
{
    public async Task<PluginHostServicesDto> DescribeAsync(PluginRegistration registration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var description = statuses.Get(registration.Manifest.Id).Describe();
        if (registration.Manifest.Permissions.Contains(PluginPermissionIds.PluginRecords, StringComparer.Ordinal))
        {
            var usage = await records.GetUsageAsync(registration.Manifest.Id, ct).ConfigureAwait(false);
            description.RecordsUsed = usage.Records;
            description.RecordBytesUsed = usage.Bytes;
        }

        return description;
    }
}
