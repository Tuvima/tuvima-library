using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services.StashBox;

/// <summary>
/// The Stash-box plugin's servers, their order and the fingerprint permission, kept in <c>stashbox_servers.json</c> in the
/// config folder. Account keys are never written there: the default server's key sits with the provider's secrets and each added
/// server's key with the plugin's own secrets, and neither is ever returned to a person.
/// </summary>
public sealed class StashBoxSettings : IStashBoxSettings
{
    public const string ProviderName = "stashbox";
    public const string DefaultServerId = "stashdb";
    public const string DefaultKeySecretName = "api_key";
    public const string FileName = "stashbox_servers.json";
    public const int MaxServers = 20;

    private const string DefaultName = "StashDB";
    private const string DefaultAddress = "https://stashdb.org/graphql";
    private const int MinimumKeyLength = 8;
    private const int MaximumKeyLength = 512;
    private const long MaxFileBytes = 64 * 1024;

    private static readonly SecretOwner ExtraServersOwner = SecretOwner.Plugin("stashbox-servers");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IConfigurationLoader _configuration;
    private readonly IConfigSecretsStore _secrets;
    private readonly ILogger<StashBoxSettings> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StashBoxSettings(IConfigurationLoader configuration, IConfigSecretsStore secrets, ILogger<StashBoxSettings> logger)
    {
        _configuration = configuration;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<StashBoxSettingsSnapshot> GetAsync(CancellationToken ct = default)
    {
        var state = await LoadAsync(ct).ConfigureAwait(false);
        var servers = new List<StashBoxServer>();
        foreach (var entry in state.Servers)
        {
            var hasKey = !string.IsNullOrWhiteSpace(await GetKeyAsync(entry.Id, ct).ConfigureAwait(false));
            var connection = !hasKey
                ? StashBoxConnectionStates.NotConnected
                : entry.Connection is StashBoxConnectionStates.Connected or StashBoxConnectionStates.NotConnected
                    ? entry.Connection
                    : StashBoxConnectionStates.Unknown;
            servers.Add(new StashBoxServer(entry.Id, entry.Name, entry.Address, hasKey, connection));
        }

        return new StashBoxSettingsSnapshot(servers, state.FingerprintPermission);
    }

    public async Task<StashBoxServer?> AddServerAsync(string name, string address, CancellationToken ct = default)
    {
        if (!StashBoxAddress.TryNormalize(address, out var uri))
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            if (state.Servers.Count >= MaxServers
                || state.Servers.Any(s => string.Equals(s.Address, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var cleanName = StashBoxClient.Clean(name, 60) ?? uri.Host;
            var id = UniqueId(state, cleanName, uri.Host);
            state.Servers.Add(new Entry { Id = id, Name = cleanName, Address = uri.AbsoluteUri, Connection = StashBoxConnectionStates.Unknown });
            await SaveAsync(state, ct).ConfigureAwait(false);
            return new StashBoxServer(id, cleanName, uri.AbsoluteUri, false, StashBoxConnectionStates.NotConnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveServerAsync(string serverId, CancellationToken ct = default)
    {
        if (string.Equals(serverId, DefaultServerId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            var removed = state.Servers.RemoveAll(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                await SaveAsync(state, ct).ConfigureAwait(false);
                await _secrets.RemoveAsync(ExtraServersOwner, ExtraKeyName(serverId), ct).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetOrderAsync(IReadOnlyList<string> serverIds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            var ordered = new List<Entry>();
            foreach (var id in serverIds)
            {
                var found = state.Servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                if (found is not null && !ordered.Contains(found))
                {
                    ordered.Add(found);
                }
            }

            ordered.AddRange(state.Servers.Where(s => !ordered.Contains(s)));
            state.Servers = ordered;
            await SaveAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetFingerprintPermissionAsync(bool allowed, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            state.FingerprintPermission = allowed;
            await SaveAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(Uri Address, string Key)?> GetCredentialAsync(string serverId, CancellationToken ct = default)
    {
        var state = await LoadAsync(ct).ConfigureAwait(false);
        var entry = state.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
        if (entry is null || !StashBoxAddress.TryNormalize(entry.Address, out var address))
        {
            return null;
        }

        var key = await GetKeyAsync(entry.Id, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(key) ? null : (address, key);
    }

    public async Task SetKeyAsync(string serverId, string key, CancellationToken ct = default)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        if (trimmed.Length is < MinimumKeyLength or > MaximumKeyLength || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException("That does not look like an account key.", nameof(key));
        }

        var state = await LoadAsync(ct).ConfigureAwait(false);
        if (!state.Servers.Any(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("There is no such server.", nameof(serverId));
        }

        if (IsDefault(serverId))
        {
            await _secrets.SetAsync(SecretOwner.Provider(ProviderName), new Dictionary<string, string> { [DefaultKeySecretName] = trimmed }, ct).ConfigureAwait(false);
        }
        else
        {
            await _secrets.SetAsync(ExtraServersOwner, new Dictionary<string, string> { [ExtraKeyName(serverId)] = trimmed }, ct).ConfigureAwait(false);
        }

        // A new key starts unproven again; "Test connection" or the next lookup settles it.
        await SetConnectionAsync(serverId, StashBoxConnectionStates.Unknown, ct).ConfigureAwait(false);
    }

    public async Task RemoveKeyAsync(string serverId, CancellationToken ct = default)
    {
        if (IsDefault(serverId))
        {
            await _secrets.RemoveAsync(SecretOwner.Provider(ProviderName), DefaultKeySecretName, ct).ConfigureAwait(false);
        }
        else
        {
            await _secrets.RemoveAsync(ExtraServersOwner, ExtraKeyName(serverId), ct).ConfigureAwait(false);
        }

        await SetConnectionAsync(serverId, StashBoxConnectionStates.Unknown, ct).ConfigureAwait(false);
    }

    public async Task SetConnectionAsync(string serverId, string connection, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            var entry = state.Servers.FirstOrDefault(s => string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Connection == connection)
            {
                return;
            }

            entry.Connection = connection is StashBoxConnectionStates.Connected or StashBoxConnectionStates.NotConnected
                ? connection
                : StashBoxConnectionStates.Unknown;
            await SaveAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsDefault(string serverId) => string.Equals(serverId, DefaultServerId, StringComparison.OrdinalIgnoreCase);

    private static string ExtraKeyName(string serverId) => "key_" + serverId.ToLowerInvariant();

    private async Task<string?> GetKeyAsync(string serverId, CancellationToken ct) =>
        IsDefault(serverId)
            ? await _secrets.GetAsync(SecretOwner.Provider(ProviderName), DefaultKeySecretName, ct).ConfigureAwait(false)
            : await _secrets.GetAsync(ExtraServersOwner, ExtraKeyName(serverId), ct).ConfigureAwait(false);

    private static string UniqueId(State state, string name, string host)
    {
        var builder = new StringBuilder();
        foreach (var c in (name + "-" + host).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        slug = slug.Length == 0 ? "server" : slug[..Math.Min(slug.Length, 40)].TrimEnd('-');
        var id = slug;
        for (var n = 2; state.Servers.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)); n++)
        {
            id = $"{slug}-{n}";
        }

        return id;
    }

    private string FilePath => Path.Combine(_configuration.ConfigDirectoryPath, FileName);

    private async Task<State> LoadAsync(CancellationToken ct)
    {
        State? state = null;
        var path = FilePath;
        try
        {
            if (!string.IsNullOrEmpty(_configuration.ConfigDirectoryPath) && File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes)
            {
                await using var stream = File.OpenRead(path);
                state = await JsonSerializer.DeserializeAsync<State>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The Stash-box server list could not be read ({Reason}); the default server is used", ex.GetType().Name);
        }

        state ??= new State();
        state.Servers = state.Servers
            .Where(s => !string.IsNullOrWhiteSpace(s.Id) && StashBoxAddress.TryNormalize(s.Address, out _))
            .Select(s =>
            {
                StashBoxAddress.TryNormalize(s.Address, out var normalized);
                return new Entry { Id = s.Id.ToLowerInvariant(), Name = StashBoxClient.Clean(s.Name, 60) ?? normalized!.Host, Address = normalized!.AbsoluteUri, Connection = s.Connection };
            })
            .DistinctBy(s => s.Id)
            .Take(MaxServers)
            .ToList();

        if (!state.Servers.Any(s => IsDefault(s.Id)))
        {
            state.Servers.Insert(0, new Entry { Id = DefaultServerId, Name = DefaultName, Address = DefaultServerAddress(), Connection = StashBoxConnectionStates.Unknown });
        }

        return state;
    }

    private string DefaultServerAddress()
    {
        var configured = _configuration.LoadProvider(ProviderName)?.Endpoints.GetValueOrDefault("api");
        return StashBoxAddress.TryNormalize(configured, out var uri) ? uri.AbsoluteUri : DefaultAddress;
    }

    private async Task SaveAsync(State state, CancellationToken ct)
    {
        var directory = _configuration.ConfigDirectoryPath;
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("There is no config folder to save the Stash-box settings in.");
        }

        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), Encoding.UTF8, ct).ConfigureAwait(false);
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed class State
    {
        [JsonPropertyName("fingerprint_permission")]
        public bool FingerprintPermission { get; set; }

        [JsonPropertyName("servers")]
        public List<Entry> Servers { get; set; } = [];
    }

    private sealed class Entry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("address")]
        public string Address { get; set; } = string.Empty;

        [JsonPropertyName("connection")]
        public string Connection { get; set; } = StashBoxConnectionStates.Unknown;
    }
}
