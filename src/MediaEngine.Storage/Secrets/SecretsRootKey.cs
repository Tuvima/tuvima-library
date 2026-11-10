using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Services;

namespace MediaEngine.Storage.Secrets;

/// <summary>
/// The one root key that locks every key file in <c>config/secrets/</c>. It lives outside the config folder, so a copied
/// config folder, a backup of it or a support bundle does not carry the means to open the files.
/// Where it comes from, in order: <c>TUVIMA_SECRETS_KEY</c> (base64 of 32 bytes, for a container secret),
/// <c>TUVIMA_SECRETS_KEY_FILE</c> (a mounted secret file), then <c>secrets/root.key</c> in the Library's data folder
/// (owner-only permissions). The same method is used on every platform.
/// </summary>
public interface ISecretsRootKey
{
    /// <summary>The 32-byte key, or null when none exists on this computer. Never creates one.</summary>
    byte[]? TryGet();

    /// <summary>The key, creating and saving a new one when none exists. Throws <see cref="IOException"/> when it cannot be saved.</summary>
    byte[] GetOrCreate();
}

/// <inheritdoc cref="ISecretsRootKey"/>
public sealed class FileSecretsRootKey : ISecretsRootKey
{
    public const int KeyLength = 32;
    public const string KeyEnvironmentVariable = "TUVIMA_SECRETS_KEY";
    public const string KeyFileEnvironmentVariable = "TUVIMA_SECRETS_KEY_FILE";

    private readonly string _path;
    private readonly string? _fromEnvironment;
    private readonly object _gate = new();
    private byte[]? _cached;

    /// <param name="defaultPath">Where the key file lives when no environment setting names one.</param>
    public FileSecretsRootKey(string defaultPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultPath);
        _fromEnvironment = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        var named = Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable);
        _path = Path.GetFullPath(string.IsNullOrWhiteSpace(named) ? defaultPath : named);
    }

    /// <summary>The default key file location: the Library's data folder, never the config folder.</summary>
    public static string DefaultPath() => Path.Combine(new TuvimaDataPaths().Root, "secrets", "root.key");

    public byte[]? TryGet()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!string.IsNullOrWhiteSpace(_fromEnvironment))
            {
                return _cached = Decode(_fromEnvironment);
            }

            if (!File.Exists(_path))
            {
                return null;
            }

            string text;
            try
            {
                text = File.ReadAllText(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The key cannot be opened, which is treated like a missing key; no inner exception travels.
                return null;
            }

            return _cached = Decode(text);
        }
    }

    public byte[] GetOrCreate()
    {
        lock (_gate)
        {
            var existing = TryGet();
            if (existing is not null)
            {
                return existing;
            }

            if (!string.IsNullOrWhiteSpace(_fromEnvironment))
            {
                throw new IOException($"{KeyEnvironmentVariable} is set but is not a valid key (base64 of {KeyLength} bytes).");
            }

            if (File.Exists(_path))
            {
                throw new IOException("The secrets root key file exists but is not a valid key; it was left untouched.");
            }

            var created = RandomNumberGenerator.GetBytes(KeyLength);
            var directory = Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, Convert.ToBase64String(created));
                if (OperatingSystem.IsWindows())
                {
                    RestrictToOwner(temporary);
                }
                else
                {
                    File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                File.Move(temporary, _path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return _cached = created;
        }
    }

    private static byte[]? Decode(string text)
    {
        try
        {
            var bytes = Convert.FromBase64String(text.Trim());
            return bytes.Length == KeyLength ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToOwner(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            owner,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

/// <summary>A fixed key held in memory, for tests and for sealing a backup under a key derived from a PIN.</summary>
public sealed class FixedSecretsRootKey(byte[]? key) : ISecretsRootKey
{
    public byte[]? TryGet() => key;

    public byte[] GetOrCreate() => key ?? throw new IOException("No key is available.");
}

/// <summary>
/// The encrypted file envelope: <c>{ "format": "tuvima.secrets", "v": 1, "alg": "AES-256-GCM", "nonce", "ct", "tag" }</c>.
/// The tag is the check value; the owner is bound in as associated data, so a file copied to another owner's name does not open.
/// </summary>
public static class SecretsEnvelope
{
    public const string FormatName = "tuvima.secrets";
    public const int Version = 1;
    private const string Algorithm = "AES-256-GCM";
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static bool LooksLikeEnvelope(System.Text.Json.JsonElement root) =>
        root.ValueKind == System.Text.Json.JsonValueKind.Object
        && root.TryGetProperty("format", out var format)
        && format.ValueKind == System.Text.Json.JsonValueKind.String
        && format.GetString() == FormatName;

    public static string Seal(byte[] key, string context, string plaintextJson)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var plain = Encoding.UTF8.GetBytes(plaintextJson);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagLength];
        using var aes = new AesGcm(Derive(key), TagLength);
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(context));
        CryptographicOperations.ZeroMemory(plain);
        return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["format"] = FormatName,
            ["v"] = Version,
            ["alg"] = Algorithm,
            ["nonce"] = Convert.ToBase64String(nonce),
            ["ct"] = Convert.ToBase64String(cipher),
            ["tag"] = Convert.ToBase64String(tag),
        });
    }

    /// <summary>The plaintext JSON, or null when the key is wrong, the file was altered, or it belongs to another owner.</summary>
    public static string? TryOpen(byte[] key, string context, System.Text.Json.JsonElement envelope)
    {
        try
        {
            if (!envelope.TryGetProperty("v", out var v) || v.GetInt32() != Version
                || envelope.GetProperty("alg").GetString() != Algorithm)
            {
                return null;
            }

            var nonce = Convert.FromBase64String(envelope.GetProperty("nonce").GetString()!);
            var cipher = Convert.FromBase64String(envelope.GetProperty("ct").GetString()!);
            var tag = Convert.FromBase64String(envelope.GetProperty("tag").GetString()!);
            if (nonce.Length != NonceLength || tag.Length != TagLength)
            {
                return null;
            }

            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Derive(key), TagLength);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(context));
            var text = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return text;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException
                                       or KeyNotFoundException or System.Text.Json.JsonException or OverflowException)
        {
            // Any failure to open means "cannot be read here"; the reason is not exposed because it could help guess keys.
            return null;
        }
    }

    private static byte[] Derive(byte[] root) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, salt: null, info: Encoding.UTF8.GetBytes("tuvima.secrets.files.v1"));
}
