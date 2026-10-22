using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>Supplies the Vault naming rules from <c>config/vault_naming.json</c>, re-read when the file changes.</summary>
public sealed class VaultNamingConfigurationProvider
{
    public const string FileName = "vault_naming.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string?> _configDirectory;
    private readonly object _gate = new();
    private VaultNamingConfiguration _current = new();
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private string? _loadedPath;

    public VaultNamingConfigurationProvider(IConfigurationLoader loader)
        : this(() => loader.ConfigDirectoryPath)
    {
    }

    public VaultNamingConfigurationProvider(Func<string?> configDirectory) => _configDirectory = configDirectory;

    public VaultNamingConfiguration Current
    {
        get
        {
            var directory = _configDirectory();
            var path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
            var writeTime = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            lock (_gate)
            {
                if (path == _loadedPath && writeTime == _loadedWriteTimeUtc)
                {
                    return _current;
                }

                _current = Load(path, writeTime);
                _loadedPath = path;
                _loadedWriteTimeUtc = writeTime;
                return _current;
            }
        }
    }

    private static VaultNamingConfiguration Load(string? path, DateTime writeTime)
    {
        if (path is null || writeTime == DateTime.MinValue)
        {
            return new VaultNamingConfiguration();
        }

        try
        {
            return JsonSerializer.Deserialize<VaultNamingConfiguration>(File.ReadAllText(path), Json) ?? new VaultNamingConfiguration();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken file never stops the Vault from working: the built-in rules apply until it is fixed.
            System.Diagnostics.Trace.TraceWarning($"config/{FileName} could not be read ({ex.GetType().Name}); using the built-in rules.");
            return new VaultNamingConfiguration();
        }
    }
}
