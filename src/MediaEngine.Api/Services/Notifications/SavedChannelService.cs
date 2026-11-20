using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>
/// One channel the person added. <see cref="Fields"/> holds what is safe to show again; <see cref="Secrets"/> holds the links and
/// tokens, encrypted by the secrets store, and is never sent back to the Dashboard.
/// </summary>
internal sealed class StoredChannelInstance
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public string Name { get; set; } = "";

    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class StoredChannelInstances
{
    public List<StoredChannelInstance> Channels { get; set; } = [];
}

/// <summary>Reads and writes the channels people added, in the notification settings of the data store.</summary>
internal sealed class SavedChannelService(INotificationRepository repository, ISecretStore secrets, TimeProvider clock)
{
    private const string Key = "channels";
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<StoredChannelInstance>> ListAsync(CancellationToken ct) =>
        (await LoadAsync(ct).ConfigureAwait(false)).Channels;

    public async Task<StoredChannelInstance?> FindAsync(string id, CancellationToken ct) =>
        (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(channel => string.Equals(channel.Id, id, StringComparison.Ordinal));

    /// <summary>Plain fields and decrypted secrets together, for sending.</summary>
    public IReadOnlyDictionary<string, string> Reveal(StoredChannelInstance channel)
    {
        var values = new Dictionary<string, string>(channel.Fields, StringComparer.Ordinal);
        foreach (var (key, cipher) in channel.Secrets)
        {
            values[key] = secrets.Decrypt(cipher);
        }

        return values;
    }

    public string Protect(string value) => secrets.Encrypt(value);

    public async Task SaveAsync(StoredChannelInstance channel, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var index = document.Channels.FindIndex(existing => string.Equals(existing.Id, channel.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                document.Channels[index] = channel;
            }
            else
            {
                document.Channels.Add(channel);
            }

            await WriteAsync(document, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadAsync(ct).ConfigureAwait(false);
            var removed = document.Channels.RemoveAll(channel => string.Equals(channel.Id, id, StringComparison.Ordinal)) > 0;
            if (removed)
            {
                await WriteAsync(document, ct).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<StoredChannelInstances> LoadAsync(CancellationToken ct)
    {
        var json = await repository.GetSettingAsync(Key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new StoredChannelInstances();
        }

        try
        {
            return JsonSerializer.Deserialize<StoredChannelInstances>(json, MediaEngineJson.Web) ?? new StoredChannelInstances();
        }
        catch (JsonException)
        {
            // A damaged row shows no added channels rather than stopping every notification.
            return new StoredChannelInstances();
        }
    }

    private Task WriteAsync(StoredChannelInstances document, CancellationToken ct) =>
        repository.SetSettingAsync(Key, JsonSerializer.Serialize(document, MediaEngineJson.Web), clock.GetUtcNow(), ct);
}


internal sealed class AddedChannelSource(
    SavedChannelService store,
    ChannelTypeCatalogue types,
    IHttpClientFactory httpClients,
    ILogger<AddedChannelSource> logger) : IAsyncNotificationChannelSource
{
    public async Task<IReadOnlyList<INotificationChannel>> GetChannelsAsync(CancellationToken ct)
    {
        var channels = new List<INotificationChannel>();
        foreach (var instance in await store.ListAsync(ct).ConfigureAwait(false))
        {
            if (types.Find(instance.Type) is { } type)
            {
                channels.Add(new TemplatedNotificationChannel(instance.Id, instance.Name, type, store.Reveal(instance), httpClients, logger));
            }
        }

        return channels;
    }
}
