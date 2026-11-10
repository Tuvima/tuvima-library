using System.Text.Json;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Storage.Secrets;

/// <summary>
/// File-backed <see cref="IConfigSecretsStore"/>. A provider's secrets are
/// <c>config/secrets/{provider}.json</c> and a plugin's are <c>config/secrets/plugins/{id}.json</c>.
/// Each file holds an encrypted envelope (<see cref="SecretsEnvelope"/>) around a flat JSON object of string
/// properties, locked with the root key (<see cref="ISecretsRootKey"/>), which lives outside the config folder.
/// Files sit in the config folder, so they survive a data store wipe; backups skip <c>secrets/</c> unless the person
/// asks for sealed keys. A file the root key cannot open reads as "cannot be read here"
/// (<see cref="SecretsUnreadableException"/>); nothing crashes. Writes go to a temporary file and
/// are moved into place. No method logs or returns a value other than the one asked for.
/// </summary>
public sealed class ConfigFolderSecretsStore : IConfigSecretsStore
{
    private const string SecretsDirectoryName = "secrets";
    private const string PluginsDirectoryName = "plugins";
    private const int MaximumSecretNameLength = 128;

    private readonly string _secretsDirectory;
    private readonly object _gate = new();

    private readonly ISecretsRootKey _rootKey;

    public ConfigFolderSecretsStore(string configDirectoryPath, ISecretsRootKey? rootKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectoryPath);
        _secretsDirectory = Path.GetFullPath(Path.Combine(configDirectoryPath, SecretsDirectoryName));
        _rootKey = rootKey ?? new FileSecretsRootKey(FileSecretsRootKey.DefaultPath());
    }

    public SecretFileState GetState(SecretOwner owner)
    {
        var path = ResolvePath(owner);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return SecretFileState.None;
            }

            try
            {
                Read(owner, path);
                return SecretFileState.Readable;
            }
            catch (SecretsUnreadableException)
            {
                return SecretFileState.CannotBeRead;
            }
            catch (InvalidDataException)
            {
                return SecretFileState.CannotBeRead;
            }
        }
    }

    public IReadOnlyList<SecretOwner> ListOwners()
    {
        var owners = new List<SecretOwner>();
        lock (_gate)
        {
            foreach (var (kind, directory) in new[] { (SecretOwnerKind.Provider, _secretsDirectory), (SecretOwnerKind.Plugin, Path.Combine(_secretsDirectory, PluginsDirectoryName)) })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        owners.Add(kind == SecretOwnerKind.Plugin ? SecretOwner.Plugin(name) : SecretOwner.Provider(name));
                    }
                    catch (ArgumentException)
                    {
                        // A file whose name cannot be an owner is not a key file.
                    }
                }
            }
        }

        return owners;
    }

    /// <summary>
    /// Locks every plain key file left by an earlier version, once. The plain original is replaced only after the
    /// encrypted copy has been written and read back correctly; a file that cannot be converted is left untouched and
    /// retried at the next start. Returns the owners converted and the owners left as they were (names only).
    /// </summary>
    public SecretsConversionResult ConvertPlainFiles()
    {
        var converted = new List<string>();
        var failed = new List<string>();
        lock (_gate)
        {
            foreach (var (kind, directory) in new[] { (SecretOwnerKind.Provider, _secretsDirectory), (SecretOwnerKind.Plugin, Path.Combine(_secretsDirectory, PluginsDirectoryName)) })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    SecretOwner owner;
                    try
                    {
                        owner = kind == SecretOwnerKind.Plugin ? SecretOwner.Plugin(name) : SecretOwner.Provider(name);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    try
                    {
                        if (!IsPlain(file))
                        {
                            continue;
                        }

                        var values = Read(owner, file);
                        Write(owner, file, values);
                        var back = Read(owner, file);
                        if (back.Count != values.Count || values.Any(pair => !back.TryGetValue(pair.Key, out var v) || v != pair.Value))
                        {
                            throw new InvalidDataException("The encrypted copy did not read back correctly.");
                        }

                        converted.Add(owner.ToString());
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    {
                        failed.Add(owner.ToString());
                    }
                }
            }
        }

        return new SecretsConversionResult(converted, failed);
    }

    private static bool IsPlain(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.ValueKind == JsonValueKind.Object && !SecretsEnvelope.LooksLikeEnvelope(document.RootElement);
    }

    private static string ContextFor(SecretOwner owner) => $"{owner.Kind}:{owner.Name}";

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
            // Keys that cannot be opened here are replaced by what the person enters now.
            var merged = TryReadForUpdate(owner, path);
            foreach (var pair in values)
            {
                merged[pair.Key] = pair.Value;
            }

            Write(owner, path, merged);
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
                Write(owner, path, replacement);
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
            var current = TryReadForUpdate(owner, path);
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
                Write(owner, path, current);
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

    private Dictionary<string, string> TryReadForUpdate(SecretOwner owner, string path)
    {
        try
        {
            return Read(owner, path);
        }
        catch (SecretsUnreadableException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private Dictionary<string, string> Read(SecretOwner owner, string path)
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
            using var outer = JsonDocument.Parse(json);
            if (outer.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"The secrets file for {owner} is not a JSON object.");
            }

            var document = outer;
            JsonDocument? opened = null;
            if (SecretsEnvelope.LooksLikeEnvelope(outer.RootElement))
            {
                var key = _rootKey.TryGet();
                var plain = key is null ? null : SecretsEnvelope.TryOpen(key, ContextFor(owner), outer.RootElement);
                if (plain is null)
                {
                    throw new SecretsUnreadableException(
                        $"The keys for {owner} cannot be read on this computer ({(key is null ? "no root key" : "root key does not open them")}). Enter them again.");
                }

                opened = JsonDocument.Parse(plain);
                document = opened;
            }

            using var _ = opened;
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

    private void Write(SecretOwner owner, string path, IReadOnlyDictionary<string, string> values)
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
            File.WriteAllText(temporaryPath, SecretsEnvelope.Seal(_rootKey.GetOrCreate(), ContextFor(owner), JsonSerializer.Serialize(values)));
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

/// <summary>What first-start conversion did: owners locked, and owners left as they were (names only, never values).</summary>
public sealed record SecretsConversionResult(IReadOnlyList<string> Converted, IReadOnlyList<string> Failed);
