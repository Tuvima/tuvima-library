using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion;

/// <summary>
/// Streams a media file in fixed-size chunks and computes a SHA-256 digest
/// without ever loading the full file into memory.
///
/// ──────────────────────────────────────────────────────────────────
/// Memory model
/// ──────────────────────────────────────────────────────────────────
///  • One 80 KB buffer is rented from <see cref="ArrayPool{T}.Shared"/>
///    per call.  It is returned in the <c>finally</c> block regardless
///    of success or failure.
///  • The 32-byte SHA-256 output is stack-allocated via <c>stackalloc</c>.
///  • Total managed heap pressure per call: ~0 bytes (beyond object headers).
///
/// ──────────────────────────────────────────────────────────────────
/// I/O model
/// ──────────────────────────────────────────────────────────────────
///  • FileStream is opened with <see cref="FileOptions.Asynchronous"/>
///    so <c>ReadAsync</c> yields the calling thread to the thread pool
///    while the OS services the read request.
///  • <see cref="FileOptions.SequentialScan"/> signals the OS to maximise
///    read-ahead prefetching and avoid polluting the file-system cache
///    with random-access patterns.
///  • <see cref="FileShare.Read"/> allows concurrent readers but fails if
///    a writer still holds an exclusive lock — consistent with the probe in
///    <see cref="DebounceQueue"/>.
///
/// Spec: Phase 7 – Hash-Based Uniqueness; Asset Integrity.
/// </summary>
public sealed class AssetHasher : IAssetHasher
{
    // 80 KB: large enough to amortize per-syscall overhead across large media files,
    // small enough to fit comfortably in L2 cache on modern hardware.
    private const int ReadBufferSize = 81_920;

    /// <summary>Files up to this size are read whole by the quick fingerprint, so only large media files are sampled.</summary>
    public const long FullReadLimitBytes = 8L * 1024 * 1024;

    private const int HeadBytes = 2 * 1024 * 1024;
    private const int TailBytes = 2 * 1024 * 1024;
    private const int SampleBytes = 256 * 1024;
    private const int SampleCount = 8;

    /// <summary>
    /// The quick identity fingerprint. A small file (up to <see cref="FullReadLimitBytes"/>) gets its plain SHA-256. A larger one gets
    /// a SHA-256 over its size, its first and last 2 MB and eight 256 KB samples spread through the middle, about 6 MB however big
    /// the file is, so a 1.2 GB episode on a slow network drive is identified in seconds instead of minutes. It is an identity only:
    /// anything that must prove two files hold the same bytes (copy checks, duplicate removal, Vault moves) uses
    /// <see cref="ComputeFullAsync"/>.
    /// </summary>
    public async Task<HashResult> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var length = new FileInfo(filePath).Length;
        if (length <= FullReadLimitBytes)
        {
            return await ComputeFullAsync(filePath, ct).ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.Asynchronous);
        var size = stream.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HeadBytes);
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData("tuvima-fp2:"u8);
            hasher.AppendData(BitConverter.GetBytes(size));

            async Task AppendAsync(long offset, int count)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                hasher.AppendData(buffer.AsSpan(0, count));
            }

            await AppendAsync(0, HeadBytes).ConfigureAwait(false);
            var middleStart = (long)HeadBytes;
            var span = size - HeadBytes - TailBytes;
            for (var index = 0; index < SampleCount; index++)
            {
                await AppendAsync(middleStart + span * index / SampleCount, SampleBytes).ConfigureAwait(false);
            }

            await AppendAsync(size - TailBytes, TailBytes).ConfigureAwait(false);
            sw.Stop();
            return new HashResult
            {
                FilePath = filePath,
                Hex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
                FileSize = size,
                Elapsed = sw.Elapsed,
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    /// <summary>The SHA-256 of every byte of the file: the proof two files are identical.</summary>
    public async Task<HashResult> ComputeFullAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var sw = Stopwatch.StartNew();

        // Open the file for sequential async reading.
        // FileShare.Read is intentional — the DebounceQueue already confirmed
        // no exclusive writer is active, but we still want to allow other readers.
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        // Rent a buffer to avoid a 80 KB heap allocation on every call.
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            // IncrementalHash feeds data in chunks without requiring the full
            // content upfront.  It is the underlying engine of SHA256.HashDataAsync.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytesHashed = 0;
            int bytesRead;

            // Read until the stream is exhausted.  ReadAsync returns 0 at EOF.
            // ct is forwarded so a cancellation mid-file terminates promptly
            // and does not waste I/O on a result that will be discarded.
            while ((bytesRead = await stream.ReadAsync(rentedBuffer, ct)) > 0)
            {
                // Pass only the filled portion; the rented buffer may be larger
                // than ReadBufferSize (ArrayPool rounds up to power-of-two).
                hasher.AppendData(rentedBuffer.AsSpan(0, bytesRead));
                bytesHashed += bytesRead;
            }

            sw.Stop();

            // Stack-allocate the 32-byte output (SHA-256 digest is always 32 bytes).
            // TryGetHashAndReset finalises the hash and resets the internal state.
            Span<byte> digest = stackalloc byte[32];
            if (!hasher.TryGetHashAndReset(digest, out int written) || written != 32)
            {
                throw new InvalidOperationException("SHA-256 finalisation returned unexpected length.");
            }

            return new HashResult
            {
                FilePath = filePath,
                // Lowercase hex matches the format stored in media_assets.content_hash.
                Hex = Convert.ToHexString(digest).ToLowerInvariant(),
                FileSize = bytesHashed,
                Elapsed = sw.Elapsed,
            };
        }
        finally
        {
            // Return without clearing: the buffer contains no sensitive data.
            ArrayPool<byte>.Shared.Return(rentedBuffer, clearArray: false);
        }
    }
}
