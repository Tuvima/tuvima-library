using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Secrets;

namespace MediaEngine.Api.Services.Backups;

/// <summary>What opening a sealed keys entry did. Names only, never values.</summary>
internal sealed record KeysRestoreOutcome(string Status, IReadOnlyList<string> RestoredKeys)
{
    public const string NoneInBackup = "none";
    public const string Restored = "restored";
    public const string PinMissing = "pin_needed";
    public const string PinWrong = "pin_wrong";
}

/// <summary>
/// The optional "Include keys" entry of a backup (<c>keys.sealed</c>). The provider and plugin keys are locked with a
/// key made from the person's 4-digit PIN (PBKDF2, deliberately slow), never the machine's root key. The PIN is a
/// convenience lock, not strong protection, and is never stored. Restoring opens it with the PIN and writes the keys back
/// through the store, so they are locked again under the new machine's root key.
/// </summary>
internal static class SealedKeysBundle
{
    public const string EntryName = "keys.sealed";
    private const int Iterations = 600_000;
    private const string Format = "tuvima.sealed-keys";
    private const string VaultFolderKeyOwner = "vault";

    public static bool IsValidPin(string? pin) => pin is { Length: 4 } && pin.All(char.IsAsciiDigit);

    public static string Seal(IConfigSecretsStore store, string pin)
    {
        var all = new List<object>();
        foreach (var owner in store.ListOwners())
        {
            // The Vault's folder-id key belongs to this install and never travels.
            if (owner.Kind == SecretOwnerKind.Provider && owner.Name == VaultFolderKeyOwner)
            {
                continue;
            }

            try
            {
                var values = store.GetAllAsync(owner).GetAwaiter().GetResult();
                if (values.Count > 0)
                {
                    all.Add(new { kind = owner.Kind.ToString(), name = owner.Name, values });
                }
            }
            catch (IOException)
            {
                // Keys this computer cannot open are left out of the backup; they would have to be re-entered anyway.
            }
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Derive(pin, salt, Iterations);
        var envelope = SecretsEnvelope.Seal(key, "backup-keys", JsonSerializer.Serialize(new { owners = all }));
        CryptographicOperations.ZeroMemory(key);
        using var document = JsonDocument.Parse(envelope);
        var map = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        map["format"] = Format;
        map["kdf"] = "PBKDF2-SHA256";
        map["iterations"] = Iterations;
        map["salt"] = Convert.ToBase64String(salt);
        map["inner_format"] = SecretsEnvelope.FormatName;
        return JsonSerializer.Serialize(map);
    }

    public static bool Exists(System.IO.Compression.ZipArchive archive) => archive.GetEntry(EntryName) is not null;

    /// <summary>Opens the entry with the PIN and writes the keys into <paramref name="store"/>. A missing or wrong PIN restores nothing and is not an error.</summary>
    public static KeysRestoreOutcome Restore(System.IO.Compression.ZipArchive archive, string? pin, IConfigSecretsStore store)
    {
        var entry = archive.GetEntry(EntryName);
        if (entry is null)
        {
            return new KeysRestoreOutcome(KeysRestoreOutcome.NoneInBackup, []);
        }

        if (!IsValidPin(pin))
        {
            return new KeysRestoreOutcome(KeysRestoreOutcome.PinMissing, []);
        }

        if (entry.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("The keys in this backup are damaged.");
        }

        string plain;
        try
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            using var envelope = JsonDocument.Parse(reader.ReadToEnd());
            var root = envelope.RootElement;
            var iterations = root.GetProperty("iterations").GetInt32();
            if (iterations is < 100_000 or > 5_000_000)
            {
                throw new InvalidDataException("The keys in this backup are damaged.");
            }

            var salt = Convert.FromBase64String(root.GetProperty("salt").GetString()!);
            var key = Derive(pin!, salt, iterations);
            var opened = SecretsEnvelope.TryOpen(key, "backup-keys", root);
            CryptographicOperations.ZeroMemory(key);
            if (opened is null)
            {
                return new KeysRestoreOutcome(KeysRestoreOutcome.PinWrong, []);
            }

            plain = opened;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("The keys in this backup are damaged.");
        }

        var restored = new List<string>();
        using var bundle = JsonDocument.Parse(plain);
        foreach (var item in bundle.RootElement.GetProperty("owners").EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString();
            var name = item.GetProperty("name").GetString()!;
            var owner = kind == nameof(SecretOwnerKind.Plugin) ? SecretOwner.Plugin(name) : SecretOwner.Provider(name);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.GetProperty("values").EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    values[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            if (values.Count > 0)
            {
                store.ReplaceAsync(owner, values).GetAwaiter().GetResult();
                restored.Add(owner.ToString());
            }
        }

        return new KeysRestoreOutcome(KeysRestoreOutcome.Restored, restored);
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, 32);
}
