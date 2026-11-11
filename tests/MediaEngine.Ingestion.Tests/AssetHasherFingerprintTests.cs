using System.Security.Cryptography;
using MediaEngine.Ingestion;

namespace MediaEngine.Ingestion.Tests;

/// <summary>
/// The quick fingerprint that identifies a big media file without reading all of it (a 1.2 GB episode on a slow network drive took
/// seven minutes to hash whole), and the full hash that proves two files are the same. Real temporary files.
/// </summary>
public sealed class AssetHasherFingerprintTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuvima-hasher-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetHasher _hasher = new();

    public AssetHasherFingerprintTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Test cleanup is best effort.
        }
    }

    private string Write(string name, long length, int seed = 7)
    {
        var path = Path.Combine(_folder, name);
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Change(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        stream.Seek(offset, SeekOrigin.Begin);
        var value = stream.ReadByte();
        stream.Seek(offset, SeekOrigin.Begin);
        stream.WriteByte((byte)(value ^ 0xFF));
    }

    [Fact]
    public async Task Fingerprint_OfASmallFile_IsItsPlainSha256()
    {
        var path = Write("small.bin", 100_000);

        var fingerprint = await _hasher.ComputeAsync(path);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), fingerprint.Hex);
    }

    [Fact]
    public async Task Fingerprint_OfABigFile_IsStable_AndNotTheFullHash()
    {
        var path = Write("big.bin", 20L * 1024 * 1024);

        var first = await _hasher.ComputeAsync(path);
        var second = await _hasher.ComputeAsync(path);
        var full = await _hasher.ComputeFullAsync(path);

        Assert.Equal(first.Hex, second.Hex);
        Assert.NotEqual(full.Hex, first.Hex);
        Assert.Equal(20L * 1024 * 1024, first.FileSize);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), full.Hex);
    }

    [Fact]
    public async Task Fingerprint_OfABigFile_NoticesAChangeAtTheStartOrTheEnd()
    {
        var path = Write("big.bin", 20L * 1024 * 1024);
        var before = (await _hasher.ComputeAsync(path)).Hex;

        Change(path, 10);
        var afterStart = (await _hasher.ComputeAsync(path)).Hex;
        Change(path, 10);
        Change(path, 20L * 1024 * 1024 - 10);
        var afterEnd = (await _hasher.ComputeAsync(path)).Hex;

        Assert.NotEqual(before, afterStart);
        Assert.NotEqual(before, afterEnd);
    }

    [Fact]
    public async Task Fingerprint_OfTwoBigFilesOfDifferentSize_Differs()
    {
        var a = Write("a.bin", 12L * 1024 * 1024);
        var b = Write("b.bin", 12L * 1024 * 1024 + 1);

        Assert.NotEqual((await _hasher.ComputeAsync(a)).Hex, (await _hasher.ComputeAsync(b)).Hex);
    }

    [Fact]
    public async Task FullHash_NoticesAChangeTheQuickFingerprintSkips()
    {
        var path = Write("big.bin", 20L * 1024 * 1024);
        var quick = (await _hasher.ComputeAsync(path)).Hex;
        var full = (await _hasher.ComputeFullAsync(path)).Hex;

        // Byte 5 MB in sits between the head, the samples and the tail: only a whole read can see it.
        Change(path, 5L * 1024 * 1024 + 3);

        Assert.Equal(quick, (await _hasher.ComputeAsync(path)).Hex);
        Assert.NotEqual(full, (await _hasher.ComputeFullAsync(path)).Hex);
    }
}
