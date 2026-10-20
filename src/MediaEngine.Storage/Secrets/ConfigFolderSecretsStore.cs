using System.Text.Json;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Storage.Secrets;

/// <summary>
/// File-backed <see cref="IConfigSecretsStore"/>. A provider's secrets are
/// <c>config/secrets/{provider}.json</c> and a plugin's are <c>config/secrets/plugins/{id}.json</c>:
/// a flat JSON object of string properties. Files are plaintext and sit in the config folder, so
/// they survive a data store wipe; backups skip <c>secrets/</c>. Writes go to a temporary file and
/// are moved into place. No method logs or returns a value other than the one asked for.
/// </summary>
public sealed class ConfigFolderSecretsStore : IConfigSecretsStore
{
    private const string SecretsDirectoryName = "secrets";
    private const string PluginsDirectoryName = "plugins";
    private const int MaximumSecretNameLength = 128;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _secretsDirectory;
    private readonly object _gate = new();

    public ConfigFolderSecretsStore(string configDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectoryPath);
        _secretsDirectory = Path.GetFullPath(Path.Combine(configDirectoryPath, SecretsDirectoryName));
    }

    public Task<IReadOnlyDictionary<string, string>> GetAllAsync(SecretOwner owner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(owner);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(Read(owner, path));
        }
    }

    public async Task<string?> GetAsync(SecretOwner owner, string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var all = await GetAllAsync(owner, cancellationToken).ConfigureAwait(false);
        return all.TryGetValue(name, out var value) ? value : null;
    }

    public Task SetAsync(SecretOwner owner, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);
        foreach (var pair in values)
        {
            ValidateName(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value, nameof(values));
        }

        var path = ResolvePath(owner);
        lock (_gate)
        {
            var merged = Read(owner, path);
            foreach (var pair in values)
            {
                merged[pair.Key] = pair.Value;
            }

            Write(path, merged);
        }

        return Task.CompletedTask;
    }

    public Task ReplaceAsync(SecretOwner owner, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(values);
        var replacement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            ValidateName(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value, nameof(values));
            replacement[pair.Key] = pair.Value;
        }

        var path = ResolvePath(owner);
        lock (_gate)
        {
            if (replacement.Count == 0)
            {
                DeleteFile(path);
            }
            else
            {
                Write(path, replacement);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(SecretOwner owner, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateName(name);
        var path = ResolvePath(owner);
        lock (_gate)
        {
            var current = Read(owner, path);
            if (!current.Remove(name))
            {
                return Task.CompletedTask;
            }

            if (current.Count == 0)
            {
                DeleteFile(path);
            }
            else
            {
                Write(path, current);
            }
        }

        return Task.CompletedTask;
    }

    public Task RemoveAllAsync(SecretOwner owner, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(owner);
        lock (_gate)
        {
            DeleteFile(path);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ListNamesAsync(SecretOwner owner, CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(owner, cancellationToken).ConfigureAwait(false);
        return all.Keys.Order(StringComparer.Ordinal).ToList();
    }

    private string ResolvePath(SecretOwner owner)
    {
        if (owner.Name is null)
        {
            throw new ArgumentException("A secret owner is required.", nameof(owner));
        }

        var directory = owner.Kind == SecretOwnerKind.Plugin
            ? Path.Combine(_secretsDirectory, PluginsDirectoryName)
            : _secretsDirectory;
        var path = Path.GetFullPath(Path.Combine(directory, $"{owner.Name}.json"));
        // SecretOwner already refuses separators and "..", so this is a second, independent guard.
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The secret owner resolved outside its own secrets folder.");
        }

        return path;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumSecretNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException("A secret name is required, at most 128 characters, with no control characters.", nameof(name));
        }
    }

    private static Dictionary<string, string> Read(SecretOwner owner, string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            // No inner exception: its text must not travel with a secrets file's content.
            throw new IOException($"The secrets file for {owner} could not be read ({ex.GetType().Name}).");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The secrets file for {owner} is not a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return result;
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"The secrets file for {owner} is not valid JSON.");
        }
    }

    private static void Write(string path, IReadOnlyDictionary<string, string> values)
    {
        var directory = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(values, WriteOptions));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
