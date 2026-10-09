using MediaEngine.Domain.Services;

namespace MediaEngine.AI.Infrastructure;

/// <summary>Cross-process leases for Tuvima model readers and mutations. Lock files are persistent;
/// deleting a lock file would let two processes lock different inodes on Unix.</summary>
public static class SharedModelArtifact
{
    public static FileStream AcquireRead(string artifact)
    {
        EnsureLeaseFile(artifact);
        return new FileStream(LeasePath(artifact), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static FileStream AcquireWrite(string artifact)
    {
        EnsureLeaseFile(artifact);
        return new FileStream(LeasePath(artifact), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public static string MetadataRoot(string artifact) => Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(artifact)))!, ".tuvima");

    public static string StagingPath(string artifact)
    {
        var directory = Path.Combine(MetadataRoot(artifact), "staging");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.downloading");
    }

    public static string OwnershipPath(string artifact) => Path.Combine(MetadataRoot(artifact), "manifests", Key(artifact) + ".sha256");

    public static void RecordOwnership(string artifact)
    {
        var path = OwnershipPath(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.OpenRead(artifact);
        File.WriteAllText(path, Hashing.Sha256Hex(stream));
    }

    public static void RequireManaged(string artifact)
    {
        var path = OwnershipPath(artifact);
        using var stream = File.OpenRead(artifact);
        if (!File.Exists(path) || !File.ReadAllText(path).Trim().Equals(Hashing.Sha256Hex(stream), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This model was installed outside Tuvima or has changed. Preserve it and manage its removal explicitly outside the app.");
        }
    }

    private static string Key(string artifact)
    {
        var path = Path.GetFullPath(artifact);
        return Hashing.Sha256Hex(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path);
    }

    private static string LeasePath(string artifact) => Path.Combine(MetadataRoot(artifact), "leases", Key(artifact) + ".lock");

    private static void EnsureLeaseFile(string artifact)
    {
        var path = LeasePath(artifact);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            return;
        }
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created the same persistent lease file first.
        }
    }
}
