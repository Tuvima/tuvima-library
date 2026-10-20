namespace MediaEngine.Domain.Contracts;

/// <summary>Whether a secret owner is a metadata provider or a plugin.</summary>
public enum SecretOwnerKind
{
    /// <summary>A metadata or subtitle provider (<c>config/secrets/{name}.json</c>).</summary>
    Provider = 0,

    /// <summary>A plugin (<c>config/secrets/plugins/{id}.json</c>).</summary>
    Plugin,
}

/// <summary>
/// Who a set of secrets belongs to. The name is validated when the owner is created (lowercase
/// letters, digits, <c>-</c>, <c>_</c> and <c>.</c>; no path separators and no <c>..</c>), so an
/// owner can never resolve to a file outside its own.
/// </summary>
public readonly record struct SecretOwner
{
    private const int MaximumNameLength = 128;

    private SecretOwner(SecretOwnerKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public SecretOwnerKind Kind { get; }

    /// <summary>The validated, lower-case provider name or plugin id. Null only for <c>default(SecretOwner)</c>, which every store refuses.</summary>
    public string Name { get; }

    public static SecretOwner Provider(string name) => new(SecretOwnerKind.Provider, Normalize(name, "provider name"));

    public static SecretOwner Plugin(string id) => new(SecretOwnerKind.Plugin, Normalize(id, "plugin id"));

    /// <summary>Owner kind and name only; never any secret value.</summary>
    public override string ToString() => Name is null ? "(no owner)" : $"{Kind.ToString().ToLowerInvariant()} '{Name}'";

    private static string Normalize(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException($"A secret owner needs a {what}.", nameof(name));
        }

        var normalized = name.Trim().ToLowerInvariant();
        if (normalized.Length > MaximumNameLength
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized[0] == '.'
            || !normalized.All(static c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.'))
        {
            throw new ArgumentException(
                $"The {what} may only use lowercase letters, digits, '-', '_' and '.'.", nameof(name));
        }

        return normalized;
    }
}

/// <summary>
/// The one secrets store for provider keys and plugin keys. Values live in files under the
/// config folder, so they survive a data store wipe and are excluded from backups. Each owner can
/// only reach its own secrets.
///
/// SECURITY: implementations and callers MUST NOT log or return secret values anywhere an
/// administrator or plugin has not asked for that exact value; log the owner and secret names only.
/// This is not <see cref="ISecretStore"/>, which encrypts values that live inside the data store.
/// </summary>
public interface IConfigSecretsStore
{
    /// <summary>Every secret the owner has, by name. Empty when none are set.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(SecretOwner owner, CancellationToken cancellationToken = default);

    /// <summary>One secret value, or null when it is not set.</summary>
    Task<string?> GetAsync(SecretOwner owner, string name, CancellationToken cancellationToken = default);

    /// <summary>Sets one or more secrets, leaving the owner's other secrets as they are.</summary>
    Task SetAsync(SecretOwner owner, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default);

    /// <summary>Replaces everything the owner has with exactly <paramref name="values"/>; an empty set removes the owner's file.</summary>
    Task ReplaceAsync(SecretOwner owner, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default);

    /// <summary>Removes one secret. Does nothing when it is not set.</summary>
    Task RemoveAsync(SecretOwner owner, string name, CancellationToken cancellationToken = default);

    /// <summary>Removes every secret the owner has.</summary>
    Task RemoveAllAsync(SecretOwner owner, CancellationToken cancellationToken = default);

    /// <summary>The names of the owner's configured secrets. Never values.</summary>
    Task<IReadOnlyList<string>> ListNamesAsync(SecretOwner owner, CancellationToken cancellationToken = default);
}
