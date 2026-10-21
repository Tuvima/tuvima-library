using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>
/// Computes the file fingerprints a stash-box record can carry, on this machine (Videos spec D35): OSHASH, a short hash of the
/// file's size and its first and last 64 KiB, and MD5 of the whole file. Both identify an exact copy. The perceptual hash (PHASH)
/// is a later addition that needs a licence check before any code is written. Computing a fingerprint reads the file but sends
/// nothing anywhere; the caller decides whether the separate fingerprint permission lets one be sent.
/// </summary>
public static class VaultFileFingerprinter
{
    public const int OshashChunkBytes = 64 * 1024;
    private const int Md5BufferBytes = 1024 * 1024;

    /// <summary>The OSHASH of a file as 16 lower-case hex digits, or null when the file is shorter than 64 KiB (the hash is not defined for it).</summary>
    public static string? ComputeOshash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var size = stream.Length;
        if (size < OshashChunkBytes)
        {
            return null;
        }

        ulong hash = (ulong)size;
        var buffer = new byte[OshashChunkBytes];
        hash = unchecked(hash + SumWords(stream, buffer));
        stream.Seek(-OshashChunkBytes, SeekOrigin.End);
        hash = unchecked(hash + SumWords(stream, buffer));
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>The MD5 of the whole file as 32 lower-case hex digits.</summary>
    public static async Task<string> ComputeMd5Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Md5BufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// OSHASH and MD5 of a file. A file that cannot be read throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>;
    /// the caller then looks the video up without fingerprints. Fingerprints are computed only when the caller has already checked
    /// the fingerprint permission.
    /// </summary>
    public static async Task<IReadOnlyList<VaultStoredFingerprint>> ComputeAsync(string path, TimeProvider time, CancellationToken ct = default)
    {
        var size = new FileInfo(path).Length;
        var now = time.GetUtcNow();
        var result = new List<VaultStoredFingerprint>();
        if (ComputeOshash(path) is { } oshash)
        {
            result.Add(new VaultStoredFingerprint(AdultFingerprintAlgorithms.Oshash, oshash, size, now));
        }

        result.Add(new VaultStoredFingerprint(AdultFingerprintAlgorithms.Md5, await ComputeMd5Async(path, ct).ConfigureAwait(false), size, now));
        return result;
    }

    private static ulong SumWords(FileStream stream, byte[] buffer)
    {
        stream.ReadExactly(buffer, 0, buffer.Length);
        ulong sum = 0;
        for (var offset = 0; offset < buffer.Length; offset += sizeof(ulong))
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong))));
        }

        return sum;
    }
}
