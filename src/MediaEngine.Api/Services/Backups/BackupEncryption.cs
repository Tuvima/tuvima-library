using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MediaEngine.Api.Services.Backups;

/// <summary>The passphrase did not unlock this backup. The message is shown to the person as is.</summary>
public sealed class BackupPassphraseException(string message) : Exception(message);

/// <summary>The two halves of the backup key pair (Developer tools spec WP25).</summary>
/// <param name="PublicKey">Encodes nothing secret; the Library keeps it so scheduled backups can be encrypted with no passphrase on hand.</param>
/// <param name="EncryptedPrivateKey">The private key, locked with the passphrase. Also written into the header of every encrypted backup, so a backup can be opened with only the file and the passphrase.</param>
public sealed record BackupKeyMaterial(byte[] PublicKey, byte[] EncryptedPrivateKey);

/// <summary>
/// Encrypts a backup before it leaves the machine and opens it again (WP25, decision D-P). The passphrase is never stored.
/// A key pair is made once from the passphrase: the public key lives in the Library's settings, so a scheduled backup can be
/// encrypted unattended, and the private key is locked with the passphrase (PBKDF2-SHA256, 600,000 rounds, AES-256) and is copied
/// into each backup file's header. Each backup uses its own random file key, locked with the public key, and the data is
/// encrypted in 1 MiB AES-256-GCM chunks that are bound to the header and their position, so a changed, reordered or
/// shortened file fails instead of restoring wrongly.
/// </summary>
public static class BackupEncryption
{
    public const string FileExtension = ".tvbk";
    public const int MinimumPassphraseLength = 10;

    private const int RsaBits = 3072;
    private const int Pbkdf2Rounds = 600_000;
    private const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int FileKeySize = 32;
    private static readonly byte[] Magic = "TVBK"u8.ToArray();
    private const byte Version = 1;

    public static BackupKeyMaterial CreateKeyMaterial(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < MinimumPassphraseLength)
        {
            throw new ArgumentException($"The passphrase needs at least {MinimumPassphraseLength} characters.", nameof(passphrase));
        }

        using var rsa = RSA.Create(RsaBits);
        var parameters = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Rounds);
        return new BackupKeyMaterial(
            rsa.ExportSubjectPublicKeyInfo(),
            rsa.ExportEncryptedPkcs8PrivateKey(Encoding.UTF8.GetBytes(passphrase), parameters));
    }

    /// <summary>True when the passphrase unlocks the private key.</summary>
    public static bool CheckPassphrase(string passphrase, byte[] encryptedPrivateKey)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportEncryptedPkcs8PrivateKey(Encoding.UTF8.GetBytes(passphrase ?? string.Empty), encryptedPrivateKey, out _);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static async Task EncryptAsync(Stream plain, Stream output, BackupKeyMaterial key, CancellationToken ct)
    {
        var fileKey = RandomNumberGenerator.GetBytes(FileKeySize);
        byte[] wrapped;
        using (var rsa = RSA.Create())
        {
            rsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
            wrapped = rsa.Encrypt(fileKey, RSAEncryptionPadding.OaepSHA256);
        }

        var baseNonce = RandomNumberGenerator.GetBytes(8);
        var header = new MemoryStream();
        header.Write(Magic);
        header.WriteByte(Version);
        WriteLengthPrefixed(header, key.EncryptedPrivateKey, 4);
        WriteLengthPrefixed(header, wrapped, 2);
        header.Write(baseNonce);
        var headerBytes = header.ToArray();
        await output.WriteAsync(headerBytes, ct).ConfigureAwait(false);
        var headerHash = SHA256.HashData(headerBytes);

        using var gcm = new AesGcm(fileKey, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var currentLength = await ReadFullAsync(plain, current, ct).ConfigureAwait(false);
        uint index = 0;
        while (true)
        {
            var nextLength = currentLength == ChunkSize ? await ReadFullAsync(plain, next, ct).ConfigureAwait(false) : 0;
            var isFinal = nextLength == 0;
            var nonce = Nonce(baseNonce, index);
            var cipher = new byte[currentLength];
            var tag = new byte[TagSize];
            gcm.Encrypt(nonce, current.AsSpan(0, currentLength), cipher, tag, Aad(headerHash, isFinal, index));

            var frame = new byte[1 + 4];
            frame[0] = isFinal ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)currentLength);
            await output.WriteAsync(frame, ct).ConfigureAwait(false);
            await output.WriteAsync(cipher, ct).ConfigureAwait(false);
            await output.WriteAsync(tag, ct).ConfigureAwait(false);
            if (isFinal)
            {
                break;
            }

            (current, next) = (next, current);
            currentLength = nextLength;
            index++;
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task DecryptAsync(Stream encrypted, Stream output, string passphrase, CancellationToken ct)
    {
        var headerBytes = new MemoryStream();
        var magic = await ReadExactAsync(encrypted, 4, ct).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("This file is not an encrypted Library backup.");
        }

        var version = await ReadExactAsync(encrypted, 1, ct).ConfigureAwait(false);
        if (version[0] != Version)
        {
            throw new InvalidDataException("This backup was made by a newer version of the Library.");
        }

        headerBytes.Write(magic);
        headerBytes.Write(version);
        var encryptedPrivate = await ReadLengthPrefixedAsync(encrypted, headerBytes, 4, 16 * 1024, ct).ConfigureAwait(false);
        var wrapped = await ReadLengthPrefixedAsync(encrypted, headerBytes, 2, 1024, ct).ConfigureAwait(false);
        var baseNonce = await ReadExactAsync(encrypted, 8, ct).ConfigureAwait(false);
        headerBytes.Write(baseNonce);
        var headerHash = SHA256.HashData(headerBytes.ToArray());

        byte[] fileKey;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportEncryptedPkcs8PrivateKey(Encoding.UTF8.GetBytes(passphrase ?? string.Empty), encryptedPrivate, out _);
            fileKey = rsa.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            throw new BackupPassphraseException("That passphrase does not open this backup. Check it and try again.");
        }

        using var gcm = new AesGcm(fileKey, TagSize);
        uint index = 0;
        while (true)
        {
            var frame = await ReadExactAsync(encrypted, 5, ct, "This backup is incomplete.").ConfigureAwait(false);
            if (frame[0] > 1)
            {
                throw new InvalidDataException("This backup is damaged.");
            }

            var isFinal = frame[0] == 1;
            var length = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1));
            if (length > ChunkSize)
            {
                throw new InvalidDataException("This backup is damaged.");
            }

            var cipher = await ReadExactAsync(encrypted, (int)length, ct, "This backup is incomplete.").ConfigureAwait(false);
            var tag = await ReadExactAsync(encrypted, TagSize, ct, "This backup is incomplete.").ConfigureAwait(false);
            var plain = new byte[length];
            try
            {
                gcm.Decrypt(Nonce(baseNonce, index), cipher, tag, plain, Aad(headerHash, isFinal, index));
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("This backup is damaged or has been changed, so it cannot be restored.");
            }

            await output.WriteAsync(plain, ct).ConfigureAwait(false);
            if (isFinal)
            {
                if (encrypted.ReadByte() != -1)
                {
                    throw new InvalidDataException("This backup is damaged or has been changed, so it cannot be restored.");
                }

                break;
            }

            index++;
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static byte[] Nonce(byte[] baseNonce, uint index)
    {
        var nonce = new byte[NonceSize];
        baseNonce.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private static byte[] Aad(byte[] headerHash, bool isFinal, uint index)
    {
        var aad = new byte[headerHash.Length + 5];
        headerHash.CopyTo(aad, 0);
        aad[headerHash.Length] = isFinal ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(headerHash.Length + 1), index);
        return aad;
    }

    private static void WriteLengthPrefixed(Stream target, byte[] value, int prefixBytes)
    {
        Span<byte> prefix = stackalloc byte[4];
        if (prefixBytes == 4)
        {
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)value.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)value.Length);
        }

        target.Write(prefix[..prefixBytes]);
        target.Write(value);
    }

    private static async Task<byte[]> ReadLengthPrefixedAsync(Stream source, MemoryStream header, int prefixBytes, int maxLength, CancellationToken ct)
    {
        var prefix = await ReadExactAsync(source, prefixBytes, ct).ConfigureAwait(false);
        var length = prefixBytes == 4 ? BinaryPrimitives.ReadUInt32BigEndian(prefix) : BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length > maxLength)
        {
            throw new InvalidDataException("This backup is damaged.");
        }

        var value = await ReadExactAsync(source, (int)length, ct).ConfigureAwait(false);
        header.Write(prefix);
        header.Write(value);
        return value;
    }

    private static async Task<byte[]> ReadExactAsync(Stream source, int count, CancellationToken ct, string shortMessage = "This file is not an encrypted Library backup.")
    {
        var buffer = new byte[count];
        var read = await ReadFullAsync(source, buffer, ct).ConfigureAwait(false);
        if (read != count)
        {
            throw new InvalidDataException(shortMessage);
        }

        return buffer;
    }

    private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
